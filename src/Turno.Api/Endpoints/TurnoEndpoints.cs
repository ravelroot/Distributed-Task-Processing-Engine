using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Turno.Api.Security;
using Turno.Contracts;
using Turno.Contracts.Api;
using Turno.Core.Domain;
using Turno.Core.Options;
using Turno.Infrastructure.Jobs;
using Turno.Infrastructure.Options;

namespace Turno.Api.Endpoints;

public sealed record JobListQuery(JobState? State, string? Type, string? Queue, string? Search, Guid? RecurringJobId, int? Page, int? PageSize);

public static class TurnoEndpoints
{
    private const int MaxBatch = 1000;

    public static IEndpointRouteBuilder MapTurnoApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/info", GetInfo).WithTags("Sistema").WithSummary("Versão e modo de acesso da API");

        var jobs = api.MapGroup("/jobs").WithTags("Tarefas");
        jobs.MapPost("/", Enqueue).WithSummary("Enfileira ou agenda uma tarefa");
        jobs.MapPost("/batch", EnqueueBatch).WithSummary("Enfileira até 1000 tarefas de uma vez");
        jobs.MapGet("/", ListJobs).WithSummary("Lista tarefas com filtros e paginação");
        jobs.MapGet("/{id:guid}", GetJob).WithSummary("Detalhes, tentativas e histórico de uma tarefa");
        jobs.MapPost("/{id:guid}/cancel", Cancel).WithSummary("Cancela uma tarefa agendada, na fila ou em execução");
        jobs.MapPost("/{id:guid}/requeue", Requeue).WithSummary("Devolve à fila uma tarefa com falha ou cancelada");
        jobs.MapDelete("/{id:guid}", Delete).WithSummary("Exclui uma tarefa finalizada");

        api.MapGet("/job-types", ListJobTypes).WithTags("Tarefas").WithSummary("Tipos de tarefa anunciados pelos workers online");
        api.MapGet("/stats", GetStats).WithTags("Monitoramento").WithSummary("Indicadores gerais, vazão e filas");
        api.MapGet("/workers", ListWorkers).WithTags("Monitoramento").WithSummary("Workers registrados e seus sinais de vida");

        var recurring = api.MapGroup("/recurring-jobs").WithTags("Recorrentes");
        recurring.MapGet("/", ListRecurring).WithSummary("Lista os agendamentos recorrentes");
        recurring.MapPut("/", UpsertRecurring).WithSummary("Cria ou atualiza um agendamento pelo nome");
        recurring.MapPost("/{id:guid}/trigger", TriggerRecurring).WithSummary("Dispara uma execução agora");
        recurring.MapPost("/{id:guid}/enable", EnableRecurring).WithSummary("Ativa o agendamento");
        recurring.MapPost("/{id:guid}/disable", DisableRecurring).WithSummary("Pausa o agendamento");
        recurring.MapDelete("/{id:guid}", DeleteRecurring).WithSummary("Exclui o agendamento");

        var deadLetters = api.MapGroup("/dead-letters").WithTags("Fila de erros");
        deadLetters.MapGet("/", ListDeadLetters).WithSummary("Lista as tarefas que esgotaram as tentativas");
        deadLetters.MapPost("/{id:guid}/requeue", RequeueDeadLetter).WithSummary("Reenfileira a tarefa original");
        deadLetters.MapDelete("/{id:guid}", DiscardDeadLetter).WithSummary("Descarta o registro da fila de erros");

        return app;
    }

    private static ServerInfo GetInfo(IOptions<ApiOptions> api, IOptions<MessagingOptions> messaging, IHostEnvironment environment) =>
        new(typeof(TurnoEndpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0", !string.IsNullOrEmpty(api.Value.ApiKey), messaging.Value.Transport.ToString(), environment.EnvironmentName);

    private static async Task<IResult> Enqueue(
        EnqueueJobRequest request,
        [FromHeader(Name = TurnoHeaders.IdempotencyKey)] string? idempotencyKey,
        JobStore store,
        IOptions<ResilienceOptions> resilience,
        CancellationToken cancellationToken)
    {
        var submission = JobRequestMapper.TryMap(request, idempotencyKey, resilience.Value, out var errors);
        if (submission is null)
        {
            return Results.ValidationProblem(errors, title: "Dados inválidos para a tarefa.");
        }

        var result = await store.SubmitAsync(submission, cancellationToken);
        var response = new EnqueueJobResponse(result.Id, result.State, result.Duplicate);
        return result.Duplicate ? Results.Ok(response) : Results.Created($"/api/jobs/{result.Id}", response);
    }

    private static async Task<IResult> EnqueueBatch(
        EnqueueBatchRequest request,
        JobStore store,
        IOptions<ResilienceOptions> resilience,
        CancellationToken cancellationToken)
    {
        if (request.Jobs is not { Count: > 0 and <= MaxBatch })
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["jobs"] = [$"Envie entre 1 e {MaxBatch} tarefas."] }, title: "Lote inválido.");
        }

        var submissions = new List<JobSubmission>(request.Jobs.Count);
        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < request.Jobs.Count; i++)
        {
            var submission = JobRequestMapper.TryMap(request.Jobs[i], null, resilience.Value, out var itemErrors);
            if (submission is null)
            {
                foreach (var (key, value) in itemErrors)
                {
                    errors[$"jobs[{i}].{key}"] = value;
                }

                continue;
            }

            submissions.Add(submission);
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, title: "Há tarefas inválidas no lote; nada foi enfileirado.");
        }

        var results = new List<EnqueueJobResponse>(submissions.Count);
        foreach (var submission in submissions)
        {
            var result = await store.SubmitAsync(submission, cancellationToken);
            results.Add(new EnqueueJobResponse(result.Id, result.State, result.Duplicate));
        }

        var duplicates = results.Count(r => r.Duplicate);
        return Results.Ok(new EnqueueBatchResponse(results, results.Count - duplicates, duplicates));
    }

    private static async Task<IResult> ListJobs([AsParameters] JobListQuery query, JobQueries queries, CancellationToken cancellationToken)
    {
        var filter = new JobListFilter(query.State, query.Type, query.Queue, query.Search, query.RecurringJobId, query.Page ?? 1, query.PageSize ?? 25);
        return Results.Ok(await queries.ListJobsAsync(filter, cancellationToken));
    }

    private static async Task<IResult> GetJob(Guid id, JobQueries queries, CancellationToken cancellationToken) =>
        await queries.GetJobAsync(id, cancellationToken) is { } details
            ? Results.Ok(details)
            : Results.Problem(title: "Tarefa não encontrada.", statusCode: StatusCodes.Status404NotFound);

    private static async Task<IResult> Cancel(Guid id, JobStore store, CancellationToken cancellationToken) =>
        ToResult(await store.CancelAsync(id, cancellationToken));

    private static async Task<IResult> Requeue(Guid id, JobStore store, CancellationToken cancellationToken) =>
        ToResult(await store.RequeueAsync(id, "Reenfileirada manualmente.", cancellationToken));

    private static async Task<IResult> Delete(Guid id, JobStore store, CancellationToken cancellationToken) =>
        ToResult(await store.DeleteAsync(id, cancellationToken));

    private static async Task<IResult> ListJobTypes(JobQueries queries, CancellationToken cancellationToken) =>
        Results.Ok(await queries.ListKnownJobTypesAsync(cancellationToken));

    private static async Task<IResult> GetStats(JobQueries queries, CancellationToken cancellationToken) =>
        Results.Ok(await queries.GetStatsAsync(cancellationToken));

    private static async Task<IResult> ListWorkers(JobQueries queries, CancellationToken cancellationToken) =>
        Results.Ok(await queries.ListWorkersAsync(cancellationToken));

    private static async Task<IResult> ListRecurring(JobQueries queries, CancellationToken cancellationToken) =>
        Results.Ok(await queries.ListRecurringAsync(cancellationToken));

    private static async Task<IResult> UpsertRecurring(
        UpsertRecurringJobRequest request,
        JobStore store,
        IOptions<ResilienceOptions> resilience,
        CancellationToken cancellationToken)
    {
        var definition = JobRequestMapper.TryMap(request, resilience.Value, out var errors);
        if (definition is null)
        {
            return Results.ValidationProblem(errors, title: "Dados inválidos para o agendamento.");
        }

        return ToResult(await store.UpsertRecurringAsync(definition, false, cancellationToken));
    }

    private static async Task<IResult> TriggerRecurring(Guid id, JobStore store, CancellationToken cancellationToken) =>
        ToResult(await store.TriggerRecurringNowAsync(id, cancellationToken));

    private static async Task<IResult> EnableRecurring(Guid id, JobStore store, CancellationToken cancellationToken) =>
        ToResult(await store.SetRecurringEnabledAsync(id, true, cancellationToken));

    private static async Task<IResult> DisableRecurring(Guid id, JobStore store, CancellationToken cancellationToken) =>
        ToResult(await store.SetRecurringEnabledAsync(id, false, cancellationToken));

    private static async Task<IResult> DeleteRecurring(Guid id, JobStore store, CancellationToken cancellationToken) =>
        ToResult(await store.DeleteRecurringAsync(id, cancellationToken));

    private static async Task<IResult> ListDeadLetters(bool? pending, int? page, int? pageSize, JobQueries queries, CancellationToken cancellationToken) =>
        Results.Ok(await queries.ListDeadLettersAsync(pending ?? true, page ?? 1, pageSize ?? 25, cancellationToken));

    private static async Task<IResult> RequeueDeadLetter(Guid id, JobStore store, CancellationToken cancellationToken) =>
        ToResult(await store.RequeueDeadLetterAsync(id, cancellationToken));

    private static async Task<IResult> DiscardDeadLetter(Guid id, JobStore store, CancellationToken cancellationToken) =>
        ToResult(await store.DiscardDeadLetterAsync(id, cancellationToken));

    private static IResult ToResult(CommandResult result) => result.Status switch
    {
        CommandStatus.Ok => Results.Ok(new ActionResponse(result.Id, result.State, result.Message)),
        CommandStatus.NotFound => Results.Problem(title: result.Message, statusCode: StatusCodes.Status404NotFound),
        CommandStatus.Conflict => Results.Problem(title: result.Message, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem(title: result.Message, statusCode: StatusCodes.Status400BadRequest)
    };
}

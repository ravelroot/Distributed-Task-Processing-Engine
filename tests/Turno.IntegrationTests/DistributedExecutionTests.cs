using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Turno.Contracts;
using Turno.Contracts.Messages;
using Turno.Infrastructure.Jobs;
using Turno.Infrastructure.Messaging;
using Turno.Infrastructure.Options;

namespace Turno.IntegrationTests;

[Collection(ClusterCollection.Name)]
public sealed class DistributedExecutionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [IntegrationFact]
    public async Task Distributes_jobs_across_workers_and_runs_each_exactly_once()
    {
        await using var cluster = await TestCluster.CreateAsync();
        await cluster.StartWorkerAsync("alfa");
        await cluster.StartWorkerAsync("beta");
        await cluster.StartWorkerAsync("gama");

        var ids = new List<Guid>();
        for (var i = 0; i < 60; i++)
        {
            ids.Add(await cluster.SubmitAsync("test.record"));
        }

        await cluster.WaitForStatesAsync(ids, JobState.Succeeded, Timeout);

        var executions = cluster.Probe.Executions.ToList();
        Assert.Equal(60, executions.Count);
        Assert.Equal(60, executions.Select(e => e.JobId).Distinct().Count());
        Assert.True(executions.Select(e => e.WorkerId).Distinct().Count() >= 2, "As tarefas deveriam se espalhar por mais de um worker.");

        var attempts = await cluster.QueryAsync(db => db.JobAttempts.AsNoTracking().Where(a => ids.Contains(a.JobId)).CountAsync());
        Assert.Equal(60, attempts);
    }

    [IntegrationFact]
    public async Task Duplicate_and_stale_messages_never_run_a_job_twice()
    {
        await using var cluster = await TestCluster.CreateAsync();
        var producer = await cluster.StartWorkerAsync("produtor", new Dictionary<string, string?> { ["Turno:Worker:Queues:0:Name"] = "outra" });

        var ids = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            ids.Add(await cluster.SubmitAsync("test.record"));
        }

        var dispatches = await cluster.QueryAsync(db => db.Jobs.AsNoTracking().Where(j => ids.Contains(j.Id)).Select(j => new { j.Id, j.DispatchId, j.Type, j.Queue }).ToListAsync());
        Assert.All(dispatches, d => Assert.NotNull(d.DispatchId));

        var prefix = producer.Services.GetRequiredService<IOptions<MessagingOptions>>().Value.QueuePrefix;
        var endpoint = await producer.Services.GetRequiredService<IBus>().GetSendEndpoint(QueueAddress.For(prefix, "default"));
        foreach (var job in dispatches)
        {
            for (var copy = 0; copy < 3; copy++)
            {
                await endpoint.Send(new ExecuteJob(job.Id, job.DispatchId!.Value, job.Type, job.Queue, null));
                await endpoint.Send(new ExecuteJob(job.Id, Guid.NewGuid(), job.Type, job.Queue, null));
            }
        }

        await cluster.StartWorkerAsync("alfa");
        await cluster.StartWorkerAsync("beta");
        await cluster.WaitForStatesAsync(ids, JobState.Succeeded, Timeout);
        await Task.Delay(2000);

        var executions = cluster.Probe.Executions.Where(e => ids.Contains(e.JobId)).ToList();
        Assert.Equal(10, executions.Count);
        Assert.Equal(10, executions.Select(e => e.JobId).Distinct().Count());
        var attempts = await cluster.QueryAsync(db => db.JobAttempts.AsNoTracking().Where(a => ids.Contains(a.JobId)).CountAsync());
        Assert.Equal(10, attempts);
    }

    [IntegrationFact]
    public async Task Recovers_a_job_whose_worker_stopped_responding_and_discards_the_zombie_result()
    {
        await using var cluster = await TestCluster.CreateAsync();
        var zombie = await cluster.StartWorkerAsync("zumbi", new Dictionary<string, string?>
        {
            ["Turno:Worker:LeaseRenewalInterval"] = "01:00:00",
            ["Turno:Recovery:Enabled"] = "false",
            ["Turno:Scheduler:Enabled"] = "false"
        });
        cluster.Probe.ZombieWorker = TestCluster.WorkerId(zombie);

        var id = await cluster.SubmitAsync("test.zombie");
        await cluster.WaitForAsync(() => Task.FromResult(cluster.Probe.Counters.ContainsKey("zombie-started")), Timeout, "o worker zumbi começar");

        // O zumbi para de consumir a fila; a execução travada segue rodando até o fim.
        // Sem isso, quando ele libera o lock pode pegar a tarefa de novo, travar outra vez
        // e esgotar as tentativas: o teste passava ou falhava conforme a distribuição do RabbitMQ.
        using var zombieStop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var zombieStopped = zombie.StopAsync(zombieStop.Token);

        var healthy = await cluster.StartWorkerAsync("saudavel");
        var healthyId = TestCluster.WorkerId(healthy);

        await cluster.WaitForStatesAsync([id], JobState.Succeeded, Timeout);
        await zombieStopped;
        await cluster.WaitForAsync(() => Task.FromResult(cluster.Probe.Counters.ContainsKey("zombie-finished")), Timeout, "o worker zumbi terminar");
        await Task.Delay(1000);

        var job = await cluster.QueryAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == id));
        var attempts = await cluster.QueryAsync(db => db.JobAttempts.AsNoTracking().Where(a => a.JobId == id).OrderBy(a => a.Id).ToListAsync());

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(healthyId, job.WorkerId);
        Assert.Contains(attempts, a => a.Outcome == AttemptOutcome.Lost && a.WorkerId == cluster.Probe.ZombieWorker);
        Assert.Single(attempts, a => a.Outcome == AttemptOutcome.Succeeded);
        Assert.Single(cluster.Probe.Executions, e => e.JobId == id && e.WorkerId == healthyId);
    }

    [IntegrationFact]
    public async Task Exhausted_retries_move_the_job_to_the_dead_letter_queue()
    {
        await using var cluster = await TestCluster.CreateAsync();
        await cluster.StartWorkerAsync("alfa");

        var id = await cluster.SubmitAsync("test.fail", maxAttempts: 3);
        await cluster.WaitForStatesAsync([id], JobState.Failed, Timeout);

        Assert.Equal(3, cluster.Probe.Counters[$"fail:{id}"]);
        var deadLetter = await cluster.QueryAsync(db => db.DeadLetters.AsNoTracking().SingleAsync(d => d.JobId == id));
        Assert.Equal(3, deadLetter.Attempts);
        Assert.Equal(typeof(InvalidOperationException).FullName, deadLetter.ErrorType);

        var requeue = await cluster.WithStoreAsync(store => store.RequeueDeadLetterAsync(deadLetter.Id, CancellationToken.None));
        Assert.Equal(CommandStatus.Ok, requeue.Status);
        await cluster.WaitForAsync(async () => await cluster.StateAsync(id) == JobState.Failed && cluster.Probe.Counters[$"fail:{id}"] == 6, Timeout, "o novo ciclo de tentativas terminar");
        Assert.NotNull(await cluster.QueryAsync(db => db.DeadLetters.AsNoTracking().Where(d => d.Id == deadLetter.Id).Select(d => d.RequeuedAt).SingleAsync()));
    }

    [IntegrationFact]
    public async Task Permanent_errors_skip_the_remaining_attempts()
    {
        await using var cluster = await TestCluster.CreateAsync();
        await cluster.StartWorkerAsync("alfa");

        var id = await cluster.SubmitAsync("test.permanent", maxAttempts: 5);
        await cluster.WaitForStatesAsync([id], JobState.Failed, Timeout);

        Assert.Equal(1, cluster.Probe.Counters[$"permanent:{id}"]);
    }

    [IntegrationFact]
    public async Task Concurrency_key_prevents_overlapping_executions_across_workers()
    {
        await using var cluster = await TestCluster.CreateAsync();
        await cluster.StartWorkerAsync("alfa");
        await cluster.StartWorkerAsync("beta");

        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await cluster.SubmitAsync("test.exclusive", concurrencyKey: "cliente-42"));
        }

        await cluster.WaitForStatesAsync(ids, JobState.Succeeded, Timeout);

        var runs = cluster.Probe.Executions.Where(e => ids.Contains(e.JobId)).OrderBy(e => e.StartedAt).ToList();
        Assert.Equal(6, runs.Count);
        for (var i = 1; i < runs.Count; i++)
        {
            Assert.True(runs[i].StartedAt >= runs[i - 1].FinishedAt, $"Execuções sobrepostas: {runs[i - 1].JobId} e {runs[i].JobId}.");
        }
    }

    [IntegrationFact]
    public async Task Cancels_a_running_job_cooperatively()
    {
        await using var cluster = await TestCluster.CreateAsync();
        await cluster.StartWorkerAsync("alfa");

        var id = await cluster.SubmitAsync("test.slow", """{"seconds": 30}""");
        await cluster.WaitForStatesAsync([id], JobState.Processing, Timeout);

        var result = await cluster.WithStoreAsync(store => store.CancelAsync(id, CancellationToken.None));
        Assert.Equal(CommandStatus.Ok, result.Status);

        await cluster.WaitForStatesAsync([id], JobState.Cancelled, TimeSpan.FromSeconds(10));
        var outcome = await cluster.QueryAsync(db => db.JobAttempts.AsNoTracking().Where(a => a.JobId == id).Select(a => a.Outcome).SingleAsync());
        Assert.Equal(AttemptOutcome.Cancelled, outcome);
    }

    [IntegrationFact]
    public async Task Graceful_shutdown_returns_running_jobs_to_the_queue()
    {
        await using var cluster = await TestCluster.CreateAsync();
        var leaving = await cluster.StartWorkerAsync("saindo", new Dictionary<string, string?> { ["Turno:Worker:ShutdownTimeout"] = "00:00:06" });

        var id = await cluster.SubmitAsync("test.slow", """{"seconds": 4}""");
        await cluster.WaitForStatesAsync([id], JobState.Processing, Timeout);
        var leavingId = TestCluster.WorkerId(leaving);

        await cluster.StartWorkerAsync("ficando");
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            await leaving.StopAsync(stop.Token);
        }

        await cluster.WaitForStatesAsync([id], JobState.Succeeded, Timeout);
        var attempts = await cluster.QueryAsync(db => db.JobAttempts.AsNoTracking().Where(a => a.JobId == id).OrderBy(a => a.Id).ToListAsync());
        var job = await cluster.QueryAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == id));

        Assert.NotEqual(leavingId, job.WorkerId);
        Assert.Equal(AttemptOutcome.Interrupted, attempts[0].Outcome);
        Assert.Equal(AttemptOutcome.Succeeded, attempts[^1].Outcome);
        Assert.Equal(1, job.Attempt);
    }

    [IntegrationFact]
    public async Task Scheduled_jobs_wait_for_their_time()
    {
        await using var cluster = await TestCluster.CreateAsync();
        await cluster.StartWorkerAsync("alfa");

        var runAt = DateTimeOffset.UtcNow.AddSeconds(3);
        var id = await cluster.SubmitAsync("test.record", runAt: runAt);
        Assert.Equal(JobState.Scheduled, await cluster.StateAsync(id));

        await cluster.WaitForStatesAsync([id], JobState.Succeeded, Timeout);
        var started = await cluster.QueryAsync(db => db.Jobs.AsNoTracking().Where(j => j.Id == id).Select(j => j.StartedAt).SingleAsync());
        Assert.True(started >= runAt.AddMilliseconds(-50));
    }

    [IntegrationFact]
    public async Task Recurring_jobs_trigger_on_schedule_without_overlap()
    {
        await using var cluster = await TestCluster.CreateAsync();
        await cluster.StartWorkerAsync("alfa");
        await cluster.StartWorkerAsync("beta");

        var definition = new RecurringDefinition("a-cada-segundo", "test.record", "default", "* * * * * *", "UTC", null, true, false, 3, null);
        var created = await cluster.WithStoreAsync(store => store.UpsertRecurringAsync(definition, false, CancellationToken.None));
        Assert.Equal(CommandStatus.Ok, created.Status);

        await cluster.WaitForAsync(
            async () => await cluster.QueryAsync(db => db.Jobs.AsNoTracking().CountAsync(j => j.RecurringJobId == created.Id && j.State == JobState.Succeeded)) >= 3,
            Timeout,
            "três execuções recorrentes");

        await cluster.WithStoreAsync(store => store.SetRecurringEnabledAsync(created.Id!.Value, false, CancellationToken.None));
        var runs = cluster.Probe.Executions.OrderBy(e => e.StartedAt).ToList();
        Assert.True(runs.Count >= 3);
    }

    [IntegrationFact]
    public async Task Idempotency_key_returns_the_existing_job()
    {
        await using var cluster = await TestCluster.CreateAsync();
        await cluster.StartWorkerAsync("alfa");

        var submission = new JobSubmission("test.record", "default", null, null, 3, null, "pedido-123", null, null, "Teste.");
        var first = await cluster.WithStoreAsync(store => store.SubmitAsync(submission, CancellationToken.None));
        var tasks = Enumerable.Range(0, 8).Select(_ => cluster.WithStoreAsync(store => store.SubmitAsync(submission, CancellationToken.None)));
        var others = await Task.WhenAll(tasks);

        Assert.False(first.Duplicate);
        Assert.All(others, o => Assert.Equal(first.Id, o.Id));
        Assert.All(others, o => Assert.True(o.Duplicate));
        await cluster.WaitForStatesAsync([first.Id], JobState.Succeeded, Timeout);
        Assert.Single(cluster.Probe.Executions, e => e.JobId == first.Id);
    }
}

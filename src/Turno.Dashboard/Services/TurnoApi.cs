using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Turno.Contracts;
using Turno.Contracts.Api;

namespace Turno.Dashboard.Services;

public sealed class ApiKeyState
{
    public string? Key { get; private set; }

    public bool Required { get; private set; }

    public bool Missing => Required && string.IsNullOrEmpty(Key);

    public bool WasRejected { get; private set; }

    public event Action? Changed;

    public void SetRequired(bool required)
    {
        Required = required;
        Changed?.Invoke();
    }

    public void SetKey(string? key)
    {
        WasRejected = false;
        Key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        Changed?.Invoke();
    }

    public void Reject()
    {
        WasRejected = Key is not null;
        Key = null;
        Required = true;
        Changed?.Invoke();
    }
}

public sealed class ApiException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class TurnoApi(HttpClient http, ApiKeyState keys)
{
    private static readonly JsonSerializerOptions Json = TurnoJson.Options;

    public Task<ServerInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ServerInfo>(HttpMethod.Get, "api/info", null, cancellationToken);

    public Task<StatsResponse> GetStatsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<StatsResponse>(HttpMethod.Get, "api/stats", null, cancellationToken);

    public Task<PagedResult<JobSummary>> ListJobsAsync(JobState? state, string? search, string? type, string? queue, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (state is { } value)
        {
            query.Add($"state={value}");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add($"search={Uri.EscapeDataString(search.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            query.Add($"type={Uri.EscapeDataString(type)}");
        }

        if (!string.IsNullOrWhiteSpace(queue))
        {
            query.Add($"queue={Uri.EscapeDataString(queue)}");
        }

        return SendAsync<PagedResult<JobSummary>>(HttpMethod.Get, $"api/jobs?{string.Join('&', query)}", null, cancellationToken);
    }

    public Task<JobDetails> GetJobAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync<JobDetails>(HttpMethod.Get, $"api/jobs/{id}", null, cancellationToken);

    public Task<EnqueueJobResponse> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<EnqueueJobResponse>(HttpMethod.Post, "api/jobs", request, cancellationToken);

    public Task<ActionResponse> CancelAsync(Guid id) => SendAsync<ActionResponse>(HttpMethod.Post, $"api/jobs/{id}/cancel", null, default);

    public Task<ActionResponse> RequeueAsync(Guid id) => SendAsync<ActionResponse>(HttpMethod.Post, $"api/jobs/{id}/requeue", null, default);

    public Task<ActionResponse> DeleteAsync(Guid id) => SendAsync<ActionResponse>(HttpMethod.Delete, $"api/jobs/{id}", null, default);

    public Task<IReadOnlyList<string>> ListJobTypesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<string>>(HttpMethod.Get, "api/job-types", null, cancellationToken);

    public Task<IReadOnlyList<WorkerDto>> ListWorkersAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<WorkerDto>>(HttpMethod.Get, "api/workers", null, cancellationToken);

    public Task<IReadOnlyList<RecurringJobDto>> ListRecurringAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<RecurringJobDto>>(HttpMethod.Get, "api/recurring-jobs", null, cancellationToken);

    public Task<ActionResponse> UpsertRecurringAsync(UpsertRecurringJobRequest request) =>
        SendAsync<ActionResponse>(HttpMethod.Put, "api/recurring-jobs", request, default);

    public Task<ActionResponse> TriggerRecurringAsync(Guid id) => SendAsync<ActionResponse>(HttpMethod.Post, $"api/recurring-jobs/{id}/trigger", null, default);

    public Task<ActionResponse> SetRecurringEnabledAsync(Guid id, bool enabled) =>
        SendAsync<ActionResponse>(HttpMethod.Post, $"api/recurring-jobs/{id}/{(enabled ? "enable" : "disable")}", null, default);

    public Task<ActionResponse> DeleteRecurringAsync(Guid id) => SendAsync<ActionResponse>(HttpMethod.Delete, $"api/recurring-jobs/{id}", null, default);

    public Task<PagedResult<DeadLetterDto>> ListDeadLettersAsync(bool pendingOnly, int page, int pageSize, CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<DeadLetterDto>>(HttpMethod.Get, $"api/dead-letters?pending={pendingOnly.ToString().ToLowerInvariant()}&page={page}&pageSize={pageSize}", null, cancellationToken);

    public Task<ActionResponse> RequeueDeadLetterAsync(Guid id) => SendAsync<ActionResponse>(HttpMethod.Post, $"api/dead-letters/{id}/requeue", null, default);

    public Task<ActionResponse> DiscardDeadLetterAsync(Guid id) => SendAsync<ActionResponse>(HttpMethod.Delete, $"api/dead-letters/{id}", null, default);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(keys.Key))
        {
            request.Headers.Add(TurnoHeaders.ApiKey, keys.Key);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            keys.Reject();
            throw new ApiException("Informe a chave de acesso da API.", response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(await ReadProblemAsync(response, cancellationToken), response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
        return result ?? throw new ApiException("A API respondeu sem conteúdo.", response.StatusCode);
    }

    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = document.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var details = errors.EnumerateObject()
                    .SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetString()))
                    .Where(v => !string.IsNullOrWhiteSpace(v));
                return $"{title} {string.Join(' ', details)}".Trim();
            }

            return title ?? $"A API respondeu {(int)response.StatusCode}.";
        }
        catch (JsonException)
        {
            return $"A API respondeu {(int)response.StatusCode}.";
        }
    }
}

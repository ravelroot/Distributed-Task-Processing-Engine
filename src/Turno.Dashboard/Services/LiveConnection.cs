using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Turno.Contracts;
using Turno.Contracts.Messages;

namespace Turno.Dashboard.Services;

public enum LiveState
{
    Connecting = 0,
    Live = 1,
    Reconnecting = 2,
    Offline = 3
}

public sealed class LiveConnection(NavigationManager navigation, ApiKeyState keys) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private HubConnection? connection;
    private CancellationTokenSource? retry;

    public LiveState State { get; private set; } = LiveState.Connecting;

    public event Action<IReadOnlyList<JobStatusChanged>>? JobsChanged;

    public event Action<IReadOnlyList<WorkerStatusChanged>>? WorkersChanged;

    public event Action? StateChanged;

    public async Task StartAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            connection = new HubConnectionBuilder()
                .WithUrl(navigation.ToAbsoluteUri("hubs/jobs"), options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult(keys.Key);
                })
                .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)])
                .AddJsonProtocol(options => TurnoJson.Configure(options.PayloadSerializerOptions))
                .Build();

            connection.On<List<JobStatusChanged>>(HubMethods.JobsChanged, changes => JobsChanged?.Invoke(changes));
            connection.On<List<WorkerStatusChanged>>(HubMethods.WorkersChanged, changes => WorkersChanged?.Invoke(changes));
            connection.Reconnecting += _ =>
            {
                SetState(LiveState.Reconnecting);
                return Task.CompletedTask;
            };
            connection.Reconnected += _ =>
            {
                SetState(LiveState.Live);
                return Task.CompletedTask;
            };
            connection.Closed += _ =>
            {
                SetState(LiveState.Offline);
                ScheduleRetry();
                return Task.CompletedTask;
            };

            await ConnectAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        retry?.Cancel();
        retry?.Dispose();
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }

        gate.Dispose();
    }

    private async Task ConnectAsync()
    {
        if (connection is null)
        {
            return;
        }

        SetState(LiveState.Connecting);
        try
        {
            await connection.StartAsync();
            SetState(LiveState.Live);
        }
        catch (HttpRequestException)
        {
            SetState(LiveState.Offline);
            ScheduleRetry();
        }
    }

    private void ScheduleRetry()
    {
        retry?.Cancel();
        retry?.Dispose();
        retry = new CancellationTokenSource();
        var token = retry.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token);
                if (connection is { State: HubConnectionState.Disconnected })
                {
                    await ConnectAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void SetState(LiveState state)
    {
        State = state;
        StateChanged?.Invoke();
    }
}

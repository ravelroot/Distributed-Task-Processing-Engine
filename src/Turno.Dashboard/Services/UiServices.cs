namespace Turno.Dashboard.Services;

public enum ToastKind
{
    Info = 0,
    Success = 1,
    Error = 2
}

public sealed record Toast(Guid Id, string Message, ToastKind Kind);

public sealed class Toasts
{
    private readonly List<Toast> items = [];

    public IReadOnlyList<Toast> Items => items;

    public event Action? Changed;

    public void Success(string message) => Show(message, ToastKind.Success);

    public void Error(string message) => Show(message, ToastKind.Error);

    public void Info(string message) => Show(message, ToastKind.Info);

    public void Dismiss(Guid id)
    {
        if (items.RemoveAll(t => t.Id == id) > 0)
        {
            Changed?.Invoke();
        }
    }

    private void Show(string message, ToastKind kind)
    {
        var toast = new Toast(Guid.NewGuid(), message, kind);
        items.Add(toast);
        if (items.Count > 4)
        {
            items.RemoveAt(0);
        }

        Changed?.Invoke();
        _ = Task.Delay(kind == ToastKind.Error ? 8000 : 4500).ContinueWith(_ => Dismiss(toast.Id), TaskScheduler.Default);
    }
}

public sealed class UiState
{
    public bool NewJobOpen { get; private set; }

    public event Action? Changed;

    public event Action? JobCreated;

    public void OpenNewJob()
    {
        NewJobOpen = true;
        Changed?.Invoke();
    }

    public void CloseNewJob()
    {
        NewJobOpen = false;
        Changed?.Invoke();
    }

    public void NotifyJobCreated() => JobCreated?.Invoke();
}

public sealed class Debouncer(TimeSpan delay, Func<Task> action) : IDisposable
{
    private readonly object sync = new();
    private Timer? timer;
    private bool disposed;

    public void Trigger()
    {
        lock (sync)
        {
            if (disposed || timer is not null)
            {
                return;
            }

            timer = new Timer(_ => Fire(), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
        }
    }

    private void Fire()
    {
        lock (sync)
        {
            timer?.Dispose();
            timer = null;
            if (disposed)
            {
                return;
            }
        }

        _ = action();
    }
}

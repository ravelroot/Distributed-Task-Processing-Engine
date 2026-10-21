namespace Turno.Core.Locking;

public interface IDistributedLock : IAsyncDisposable
{
    string Resource { get; }

    string LockId { get; }

    bool IsHeld { get; }
}

public interface IDistributedLockProvider
{
    Task<IDistributedLock?> TryAcquireAsync(string resource, TimeSpan expiry, CancellationToken cancellationToken);

    Task<IDistributedLock?> AcquireAsync(string resource, TimeSpan expiry, TimeSpan wait, TimeSpan retry, CancellationToken cancellationToken);
}

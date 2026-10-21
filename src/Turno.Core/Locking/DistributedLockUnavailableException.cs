namespace Turno.Core.Locking;

public sealed class DistributedLockUnavailableException : Exception
{
    public DistributedLockUnavailableException()
    {
    }

    public DistributedLockUnavailableException(string message) : base(message)
    {
    }

    public DistributedLockUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

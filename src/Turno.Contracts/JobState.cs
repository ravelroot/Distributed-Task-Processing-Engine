namespace Turno.Contracts;

public enum JobState
{
    Scheduled = 0,
    Enqueued = 1,
    Processing = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5
}

public enum AttemptOutcome
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    Deferred = 3,
    Interrupted = 4,
    Lost = 5,
    Cancelled = 6
}

public enum WorkerStatus
{
    Online = 0,
    Stopping = 1,
    Offline = 2
}

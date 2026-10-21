using System.Collections.Frozen;
using Turno.Contracts;

namespace Turno.Core.Domain;

public static class JobStateMachine
{
    private static readonly FrozenDictionary<JobState, JobState[]> Transitions = new Dictionary<JobState, JobState[]>
    {
        [JobState.Scheduled] = [JobState.Enqueued, JobState.Cancelled],
        [JobState.Enqueued] = [JobState.Processing, JobState.Scheduled, JobState.Enqueued, JobState.Cancelled],
        [JobState.Processing] = [JobState.Succeeded, JobState.Failed, JobState.Scheduled, JobState.Enqueued, JobState.Cancelled],
        [JobState.Succeeded] = [],
        [JobState.Failed] = [JobState.Enqueued],
        [JobState.Cancelled] = [JobState.Enqueued]
    }.ToFrozenDictionary();

    public static IReadOnlyList<JobState> TerminalStates { get; } = [JobState.Succeeded, JobState.Failed, JobState.Cancelled];

    public static IReadOnlyList<JobState> CancellableStates { get; } = [JobState.Scheduled, JobState.Enqueued, JobState.Processing];

    public static IReadOnlyList<JobState> RequeueableStates { get; } = [JobState.Failed, JobState.Cancelled];

    public static bool CanTransition(JobState from, JobState to) =>
        Transitions.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static IReadOnlyList<JobState> NextStates(JobState from) =>
        Transitions.TryGetValue(from, out var targets) ? targets : [];

    public static void EnsureTransition(JobState from, JobState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidJobTransitionException(from, to);
        }
    }

    public static bool IsTerminal(JobState state) => state is JobState.Succeeded or JobState.Failed or JobState.Cancelled;

    public static bool CanCancel(JobState state) => state is JobState.Scheduled or JobState.Enqueued or JobState.Processing;

    public static bool CanRequeue(JobState state) => state is JobState.Failed or JobState.Cancelled;

    public static bool CanDelete(JobState state) => IsTerminal(state);
}

public sealed class InvalidJobTransitionException : InvalidOperationException
{
    public InvalidJobTransitionException()
    {
    }

    public InvalidJobTransitionException(string message) : base(message)
    {
    }

    public InvalidJobTransitionException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public InvalidJobTransitionException(JobState from, JobState to)
        : base($"Transição de {from} para {to} não é permitida.")
    {
        From = from;
        To = to;
    }

    public JobState From { get; }
    public JobState To { get; }
}

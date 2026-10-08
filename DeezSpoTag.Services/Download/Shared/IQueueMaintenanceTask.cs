namespace DeezSpoTag.Services.Download.Shared;

/// <summary>
///     Periodic housekeeping a download engine wants run alongside the queue loop.
/// </summary>
/// <remarks>
///     <para>
///         This exists so an engine can prune its own stale state without a per-engine background service.
///         Two existing guardrails forbid the per-engine pattern, and rightly so: it is how engine-specific
///         workers multiplied. The queue loop already ticks once a minute, so an engine registers a task here
///         instead of a new hosted service, and this shared code stays engine-agnostic because it knows only
///         this interface.
///     </para>
/// </remarks>
public interface IQueueMaintenanceTask
{
    /// <summary>Gets the engine this task belongs to, for logging.</summary>
    string Engine { get; }

    /// <summary>
    ///     Runs the housekeeping.
    /// </summary>
    /// <remarks>
    ///     Implementations must swallow their own failures. This is called from the queue loop, and a
    ///     housekeeping error must never be able to stop downloads from being processed.
    /// </remarks>
    Task RunAsync(CancellationToken cancellationToken);
}

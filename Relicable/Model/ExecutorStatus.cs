namespace Relicable.Model;

// Returned by an executor every tick. The controller advances only on Complete.
public enum ExecutorStatus
{
    InProgress,
    Complete,
    Failed,
    // Not doable right now, but not a failure: the controller should move on to a
    // different objective and may re-select this one later. Used by ParticipateFate
    // to rotate off a book FATE that has not spawned within the configured window,
    // so the run does not idle forever on one dead FATE. Unlike Failed, this does
    // not count toward the failure backoff.
    Rotate,
}

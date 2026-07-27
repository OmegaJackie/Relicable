using Relicable.Model;

namespace Relicable.Steps;

// One executor per StepType. The controller resolves the executor by Handles and
// drives its lifecycle: Start once, Update every tick until Complete or Failed,
// Stop on completion or abort.
public interface ITaskExecutor
{
    StepType Handles { get; }

    // Issue the action. Called once when the step becomes active.
    void Start(StepData step, ExecutionContext ctx);

    // Evaluate progress. Called every framework tick.
    ExecutorStatus Update(StepData step, ExecutionContext ctx);

    // Release any held resources (stop movement, disable combat backend).
    void Stop(ExecutionContext ctx);
}

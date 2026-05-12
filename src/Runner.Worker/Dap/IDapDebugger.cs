using System.Collections.Generic;
using System.Threading.Tasks;
using GitHub.Runner.Common;

namespace GitHub.Runner.Worker.Dap
{
    public enum DapSessionState
    {
        NotStarted,
        WaitingForConnection,
        Initializing,
        Ready,
        Paused,
        Running,
        Terminated
    }

    [ServiceLocator(Default = typeof(DapDebugger))]
    public interface IDapDebugger : IRunnerService
    {
        Task StartAsync(IExecutionContext jobContext);
        Task WaitUntilReadyAsync();
        Task OnStepStartingAsync(IStep step);
        void OnStepCompleted(IStep step);

        /// <summary>
        /// Called after JobExtension.InitializeJob has returned and the initial
        /// step queue + post-step stack have been populated. The debugger uses
        /// these snapshots to build the synthesized job execution view served
        /// via the DAP source request.
        /// </summary>
        Task OnJobStepsInitializedAsync(IEnumerable<IStep> mainQueue, IEnumerable<IStep> initialPostStack);

        /// <summary>
        /// Called from ExecutionContext.RegisterPostJobStep after a post-step
        /// is pushed onto the post-job stack. The debugger appends the step
        /// to the running execution view so the rendered YAML reflects the
        /// newly-known post-step.
        /// </summary>
        void OnPostStepRegistered(IStep step);

        Task OnJobCompletedAsync();
        Task StopAsync();
    }
}

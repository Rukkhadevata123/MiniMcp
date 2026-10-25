using MiniMcp.Configuration;
using MiniMcp.Runtime;

namespace MiniMcp.Cli;

internal static class CliRunSession
{
    internal static async Task<int> RunAsync(RuntimeConfiguration configuration, TextWriter output, TextWriter error,
        CancellationToken cancellationToken, Func<McpRuntime>? runtimeFactory)
    {
        var runtime = runtimeFactory is null ? new McpRuntime() : runtimeFactory();
        var budget = new CliShutdownBudget();
        using var stopping = cancellationToken.UnsafeRegister(_ => budget.Begin(), null);
        var presentation = new CliPresentation(runtime, output, error, budget);
        var startupCancellation = new CancellationTokenSource();
        var cancellingStartup = Task.CompletedTask;
        void CancelStartup()
        {
            if (startupCancellation.IsCancellationRequested) return;
            cancellingStartup = startupCancellation.CancelAsync();
            Observe(cancellingStartup);
        }
        var starting = runtime.StartAsync(configuration, startupCancellation.Token);
        var started = false;
        var startupFinished = false;
        var cleaned = false;
        Exception? problem = null;
        try
        {
            var first = await Task.WhenAny(starting, presentation.Failure.Task, budget.Started.Task).ConfigureAwait(false);
            if (first != starting)
            {
                budget.Begin();
                CancelStartup();
            }
            try
            {
                if (budget.Started.Task.IsCompleted)
                    await starting.WaitAsync(budget.CleanupRemaining).ConfigureAwait(false);
                else await starting.ConfigureAwait(false);
                started = true;
            }
            catch (Exception exception) { problem = exception; }
            startupFinished = starting.IsCompleted;

            if (started)
            {
                presentation.Ready(runtime.Snapshot);
                await Task.WhenAny(budget.Started.Task, presentation.Failure.Task).ConfigureAwait(false);
            }
            budget.Begin();
            presentation.Capture(runtime.Snapshot);

            // StartAsync already attempted cleanup on failure. Do not silently add another full
            // stop/dispose wait if that attempt is unfinished or retained its resources.
            if (startupFinished && (started || !runtime.Snapshot.HasOwnedResources))
            {
                using var deadline = new CancellationTokenSource(budget.CleanupRemaining);
                var disposing = runtime.DisposeAsync(deadline.Token).AsTask();
                try
                {
                    await disposing.ConfigureAwait(false);
                    cleaned = true;
                }
                catch (Exception exception) { problem = exception; Observe(disposing); }
            }
        }
        finally
        {
            // A startup that ignores cancellation remains owned by Runtime. The CLI may stop
            // waiting, but never hides a late exception or disposes its token source too early.
            if (!starting.IsCompleted) CancelStartup();
            if (starting.IsCompleted && cancellingStartup.IsCompleted) startupCancellation.Dispose();
            else
            {
                _ = Task.WhenAll(starting, cancellingStartup).ContinueWith(task => { _ = task.Exception; startupCancellation.Dispose(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        var cancelledStartup = !started && problem is OperationCanceledException && cancellationToken.IsCancellationRequested;
        var failed = !cleaned || runtime.Snapshot.FaultCount != 0 || presentation.HadFault ||
            presentation.OutputFailed || (problem is not null && !cancelledStartup);
        var code = failed ? CliExitCodes.Failure : cancelledStartup ? CliExitCodes.Cancelled : CliExitCodes.Success;
        var detail = presentation.OutputFailed ? presentation.Failure.Task.Result.Message : problem?.Message;
        var message = !cleaned
            ? $"启动或停止失败：清理尚未完成，请核对受管进程与修改记录。{detail}"
            : code == CliExitCodes.Cancelled ? "启动已取消；清理完成。"
            : code == CliExitCodes.Success ? "已停止。"
            : problem is not null || presentation.OutputFailed ? $"启动或停止失败：{detail}；清理完成。"
            : "运行发生故障；MCP 和受管隧道清理完成。";
        if (!await presentation.FinishAsync(message, code != CliExitCodes.Success).ConfigureAwait(false))
        {
            code = CliExitCodes.Failure;
            await presentation.ReportUnavailableAsync(cleaned
                ? "输出未完成；MCP 和受管隧道清理完成。" : "输出与清理未完成；请核对受管进程与修改记录。").ConfigureAwait(false);
        }
        return code;
    }

    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}

using MiniMcp.Configuration;
using MiniMcp.Desktop.ViewModels;
using MiniMcp.Recovery;
using MiniMcp.Services;

namespace MiniMcp.DesktopChecks;

internal static class RecoveryHistoryChecks
{
    internal static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("history: invalidating an in-flight preview cannot publish stale restoration evidence", async () =>
        {
            await using var fixture = new SettingsFixture();
            File.WriteAllText(Path.Combine(fixture.Root, "sample.md"), "old\n");
            var configuration = new ConfigurationValidator(fixture.Paths).Validate(fixture.Settings, fixture.Source,
                new ExecutionPolicy { Mode = ExecutionMode.ProjectEdit });
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var execution = new ToolExecutionService(Guid.NewGuid(), configuration, null,
                recoveryPreviewCheckpoint: async (stage, _) =>
                { if (stage == RecoveryPreviewStage.BeforeReturn) { entered.TrySetResult(); await release.Task; } });
            var source = await execution.ApplyPatchAsync("*** Begin Patch\n*** Update File: sample.md\n@@\n-old\n+new\n*** End Patch");
            await using var model = new RecoveryHistory();
            await model.LoadAsync(new LocalRecoverySession(execution, false), true, CancellationToken.None);
            var pending = model.SelectAsync(model.Records.Single(), CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                model.Invalidate("会话已改变"); release.TrySetResult(); await pending;
                if (model.CanRestore || model.Diff.Length != 0 || model.Message != "会话已改变")
                    throw new Exception("An invalidated preview republished a restore preparation");
            }
            finally { release.TrySetResult(); await pending; }
        });
    }
}

using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MiniMcp.Desktop.ViewModels;

namespace MiniMcp.DesktopChecks;

internal static class RecoveryUiChecks
{
    internal static async Task Run(McpRuntime runtime, SettingsFixture fixture, Window window, FrameworkElement root,
        Func<string, FrameworkElement> find, Func<string, Task> navigate, Action<Button> invoke, Func<ContentDialog?> openDialog, string? screenshots)
    {
        T Find<T>(string id) where T : FrameworkElement => (T)find(id);
        async Task Mode(int index)
        {
            await navigate("SettingsNavigation");
            Find<ComboBox>("ModeEditor").SelectedIndex = index;
            await Until(() => fixture.Editor.ModeIndex == index && fixture.Editor.IsPolicyDirty);
            invoke(Find<Button>("SavePolicy"));
            await Until(() => fixture.Editor.CanLoad && !fixture.Editor.IsBusy && !fixture.Editor.IsPolicyDirty && fixture.Editor.NeedsRestart);
            var oldRun = runtime.Snapshot.RunId;
            await navigate("OverviewNavigation"); invoke(Find<Button>("Restart"));
            await Until(() => runtime.Snapshot.State == RuntimeState.Running && runtime.Snapshot.RunId != oldRun &&
                (int)runtime.Snapshot.Configuration!.Policy.Mode == index && Find<Button>("Stop").IsEnabled);
        }
        string[] Snapshot() => Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(fixture.Root, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
        await Mode(2);
        File.WriteAllText(Path.Combine(fixture.Root, "history-edit.md"), "original\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, "history-deleted.md"), string.Concat(Enumerable.Range(0, 900).Select(index =>
            $"uncommitted-{index:D4}-" + new string('x', 30) + "\r\n")) + "END-OF-GUI-PREVIEW\r\n", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(fixture.Root, "history-move.md"), "move\n", new UTF8Encoding(false));
        var before = Snapshot();
        var source = (await runtime.Execution!.ApplyPatchAsync("*** Begin Patch\n*** Add File: history-added.md\n+added\n" +
            "*** Update File: history-edit.md\n@@\n-original\n+changed\n*** Delete File: history-deleted.md\n" +
            "*** Update File: history-move.md\n*** Move to: history-moved.md\n@@\n-move\n+moved\n*** End Patch")).Commit!.ChangeId;
        var after = Snapshot();
        var recordPath = Path.Combine(runtime.Snapshot.Configuration!.Policy.RecoveryDirectory, source.ToString("N"), "manifest.json");
        var originalRecord = File.ReadAllBytes(recordPath);
        async Task<ListView> Load(Guid id)
        {
            await navigate("RecoveryNavigation"); invoke(Find<Button>("LoadRecords"));
            var list = Find<ListView>("RecoveryList");
            await Until(() => Find<Button>("LoadRecords").IsEnabled && list.Items.OfType<DesktopRecoveryRow>().Any(row => row.ChangeId == id));
            list.SelectedItem = list.Items.OfType<DesktopRecoveryRow>().Single(row => row.ChangeId == id);
            await Until(() => Find<Button>("LoadRecords").IsEnabled && Find<TextBox>("RecoveryDiff").Text.Length > 0);
            return list;
        }
        async Task Confirm(bool accept)
        {
            invoke(Find<Button>("RestoreRecord"));
            await Until(() => openDialog() is not null || Find<InfoBar>("Error").IsOpen);
            Assert(openDialog() is not null, "Restore confirmation: " + Find<InfoBar>("Error").Message);
            var dialog = openDialog()!;
            Assert(dialog.Title!.ToString()!.Contains("确认恢复") && dialog.Content!.ToString()!.Contains("完整反向预览"), "Concrete restore confirmation");
            if (accept) invoke(Walk(dialog).OfType<Button>().First(button => button.Name == "PrimaryButton"));
            else dialog.Hide();
            await Until(() => Find<Button>("LoadRecords").IsEnabled);
        }
        await Mode(0); await Load(source);
        Assert(!Find<Button>("RestoreRecord").IsEnabled && Find<TextBlock>("RecoveryMessage").Text.Contains("ReadOnly") &&
            Find<TextBox>("RecoveryDiff").Text.Contains("uncommitted"), "ReadOnly preview: " + runtime.Snapshot.Configuration!.Policy.Mode +
            " / " + Find<TextBlock>("RecoveryMessage").Text + " / " + Find<TextBox>("RecoveryDiff").Text);
        Assert(before.SequenceEqual(Snapshot()) == false && after.SequenceEqual(Snapshot()), "Inspection preserves installed files");
        await Mode(1); var records = await Load(source);
        Assert(Find<Button>("RestoreRecord").IsEnabled && Find<TextBox>("RecoveryDiff").Text.Contains("history-added.md") &&
            Find<TextBox>("RecoveryDiff").Text.Contains("history-deleted.md") && Find<TextBox>("RecoveryDiff").Text.Contains("history-move.md"), "ProjectEdit exposes the full restoration across all change types");
        Assert(Find<TextBox>("RecoveryDiff").Text.Contains("END-OF-GUI-PREVIEW") && Find<TextBox>("RecoveryDiff").Text.Length > 30000,
            "Native multiline text holds the entire large inverse diff");
        if (screenshots is not null)
        {
            Directory.CreateDirectory(screenshots); await Task.Delay(400);
            await VisualCapture.Save(root, Path.Combine(screenshots, "recovery-preview.png"));
        }
        var originalSize = window.AppWindow.Size; var scale = root.XamlRoot.RasterizationScale;
        window.AppWindow.Resize(new((int)(520 * scale), (int)(720 * scale))); await Task.Delay(160);
        Assert(Find<TextBox>("RecoveryDiff").ActualHeight > 80 && Find<TextBox>("RecoveryDiff").Text.Contains("END-OF-GUI-PREVIEW"),
            "Narrow recovery layout keeps the full preview accessible");
        if (screenshots is not null) await VisualCapture.Save(root, Path.Combine(screenshots, "recovery-narrow.png"));
        window.AppWindow.Resize(originalSize); await Task.Delay(160);
        await Confirm(false); Assert(after.SequenceEqual(Snapshot()) && records.Items.Count == 1, "Cancel creates no record or mutation");
        invoke(Find<Button>("RestoreRecord")); await Until(() => openDialog() is not null);
        File.WriteAllText(Path.Combine(fixture.Root, "history-edit.md"), "external\n");
        invoke(Walk(openDialog()!).OfType<Button>().First(button => button.Name == "PrimaryButton"));
        await Until(() => Find<Button>("LoadRecords").IsEnabled);
        Assert(Find<InfoBar>("Error").IsOpen && File.ReadAllText(Path.Combine(fixture.Root, "history-edit.md")) == "external\n" && records.Items.Count == 1,
            "Files changed during confirmation reject the restore");
        File.WriteAllText(Path.Combine(fixture.Root, "history-edit.md"), "changed\n", new UTF8Encoding(false));
        Find<InfoBar>("Error").IsOpen = false;
        records = await Load(source); await Confirm(true);
        Assert(before.SequenceEqual(Snapshot()) && records.Items.Count == 2 && originalRecord.SequenceEqual(File.ReadAllBytes(recordPath)),
            "Native GUI restores raw BOM/newlines, add/delete/move/update and preserves source history");
        var restored = records.Items.OfType<DesktopRecoveryRow>().First(row => row.ChangeId != source).ChangeId;
        await Load(restored); await Confirm(true);
        Assert(after.SequenceEqual(Snapshot()) && records.Items.Count == 3, "GUI can reverse the restoration record");
        await navigate("OverviewNavigation"); invoke(Find<Button>("Stop"));
        await Until(() => runtime.Snapshot.State == RuntimeState.Stopped && Find<Button>("Start").IsEnabled);
        await Load(source); await Confirm(true);
        Assert(before.SequenceEqual(Snapshot()) && runtime.Snapshot.State == RuntimeState.Stopped && !runtime.Snapshot.HasOwnedResources,
            "Stopped GUI maintenance restores without starting a Host or tunnel");
        await navigate("OverviewNavigation"); invoke(Find<Button>("Start"));
        await Until(() => runtime.Snapshot.State == RuntimeState.Running && Find<Button>("Stop").IsEnabled);
        Assert(runtime.DiagnosticSubscriberCounts == (1, 1), "History creates no extra runtime subscriptions");
        Console.WriteLine("PASS UI recovery: complete previews, ReadOnly, cancel, post-confirmation conflict, mixed raw-byte restore, reverse restoration and stopped maintenance");
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    { yield return root; for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        foreach (var child in Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index))) yield return child; }
    private static async Task Until(Func<bool> condition)
    { var deadline = DateTime.UtcNow.AddSeconds(15); while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Recovery UI condition timed out"); await Task.Delay(25); } }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}

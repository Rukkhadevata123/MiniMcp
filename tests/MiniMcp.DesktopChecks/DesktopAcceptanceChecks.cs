using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MiniMcp.Desktop.Controls;
using MiniMcp.Desktop.ViewModels;

namespace MiniMcp.DesktopChecks;

internal static class DesktopAcceptanceChecks
{
    internal static async Task Run(Window window, FrameworkElement root, SettingsFixture fixture,
        Func<string, FrameworkElement> find, Func<string, Task> navigate, Action<Button> invoke)
    {
        T Find<T>(string id) where T : FrameworkElement => (T)find(id);
        var size = window.AppWindow.Size; var position = window.AppWindow.Position; var theme = root.RequestedTheme;
        var monitors = DisplayArea.FindAll(); var scales = new HashSet<uint>();
        try
        {
            for (var monitorIndex = 0; monitorIndex < monitors.Count; monitorIndex++)
            {
                var monitor = monitors[monitorIndex];
                window.AppWindow.Move(new(monitor.WorkArea.X + 16, monitor.WorkArea.Y + 16)); await Task.Delay(120);
                var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)); scales.Add(dpi);
                Assert(Math.Abs(root.XamlRoot.RasterizationScale - dpi / 96d) < 0.02, "XAML and native window agree on monitor DPI");
                window.AppWindow.Resize(new(Math.Min((int)(1400 * root.XamlRoot.RasterizationScale), monitor.WorkArea.Width - 32),
                    Math.Min((int)(850 * root.XamlRoot.RasterizationScale), monitor.WorkArea.Height - 32)));
                foreach (var requested in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    root.RequestedTheme = requested;
                    foreach (var id in new[] { "OverviewNavigation", "SettingsNavigation", "LogsNavigation", "RecoveryNavigation" })
                    {
                        await navigate(id); await Task.Delay(60);
                        var page = Find<Frame>("WorkspaceFrame").Content as Page ?? throw new Exception("Missing page");
                        Assert(page.ActualTheme == root.ActualTheme && page.ActualWidth is > 100 and <= 1200,
                            "All cached pages retain usable width and theme on the available monitor");
                        Assert(Walk(page).OfType<PageHeader>().Single().ActualWidth > 100, "Page header remains visible");
                    }
                }
                Console.WriteLine($"PASS display: work area {monitor.WorkArea.Width}x{monitor.WorkArea.Height}, DPI {dpi}, four pages in light/dark");
            }
        }
        finally { root.RequestedTheme = theme; window.AppWindow.Move(position); window.AppWindow.Resize(size); }
        Console.WriteLine($"Display coverage: {monitors.Count} monitor(s), {scales.Count} distinct DPI value(s)");
        window.AppWindow.Resize(new(Math.Max(size.Width, (int)(1100 * root.XamlRoot.RasterizationScale)), size.Height)); await Task.Delay(160);

        await navigate("SettingsNavigation");
        var project = Find<TextBox>("ProjectRootEditor");
        Walk((Page)Find<Frame>("WorkspaceFrame").Content).OfType<ScrollViewer>().First().ChangeView(null, 0, null, true);
        await Task.Delay(160);
        using (var pointer = new PointerInput(window))
        {
            pointer.MoveTo(Find<TextBox>("SettingsFilePath"), root, window);
            Assert(project.Focus(FocusState.Keyboard), "Project editor accepts keyboard focus");
            pointer.PressTab(window);
            await Until(() => Find<Button>("ChooseProject").FocusState == FocusState.Keyboard);
        }
        foreach (var id in new[] { "LoadSettings", "ChooseSettings", "ChooseProject", "SaveSettings" })
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Find<Button>(id));
            Assert(peer is not null && !string.IsNullOrWhiteSpace(peer.GetName()), "Native button accessible name: " + id);
        }
        await Pick(Find<Button>("ChooseSettings"), fixture.Source);
        await Until(() => fixture.Editor.CanLoad && !fixture.Editor.IsBusy);
        Assert(fixture.Editor.SettingsPath == fixture.Source && !fixture.Editor.HasUnsavedChanges, "Native file picker reloads the selected fixture configuration");
        await Pick(Find<Button>("ChooseProject"), fixture.Root);
        await Until(() => fixture.Editor.CanLoad && !fixture.Editor.IsBusy);
        Assert(fixture.Editor.ProjectRoot == fixture.Root && !fixture.Editor.HasUnsavedChanges, "Native folder picker selects the fixture project");

        await navigate("LogsNavigation");
        var list = Find<ListView>("LogList");
        Assert(list.Items.Count > 0, "Runtime logs available for actual copy/export");
        var export = Path.Combine(fixture.DirectoryPath, "acceptance-export.log");
        var all = string.Join(Environment.NewLine, list.Items.OfType<DesktopLogRow>().Select(row => row.Text));
        var exporter = Find<AppBarButton>("ExportLogs");
        await Pick(exporter, export);
        await Until(() => File.Exists(export) && Find<TextBlock>("LogActionMessage").Text.StartsWith("筛选结果已导出：", StringComparison.Ordinal));
        Assert(await File.ReadAllTextAsync(export) == all, "Native save picker and exporter preserve the complete frozen filtered range");
        Console.WriteLine("PASS desktop acceptance: native file/folder/save pickers, exact export, focus navigation and accessible names");
        window.AppWindow.Resize(size);

        async Task Pick(Button button, string path)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "PickerAutomation.ps1"),
                "-OwnerWindow", WinRT.Interop.WindowNative.GetWindowHandle(window).ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-ValuePath", path, "-FixtureRoot", fixture.DirectoryPath }) start.ArgumentList.Add(value);
            using var helper = Process.Start(start) ?? throw new Exception("Cannot start native picker automation");
            var output = helper.StandardOutput.ReadToEndAsync(); var error = helper.StandardError.ReadToEndAsync();
            invoke(button);
            try { await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { if (!helper.HasExited) helper.Kill(entireProcessTree: true); throw; }
            Assert(helper.ExitCode == 0, "Native picker automation: " + await output + "\n" + await error);
            Console.WriteLine((await output).Trim());
        }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    { yield return root; for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        foreach (var item in Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index))) yield return item; }
    private static async Task Until(Func<bool> condition)
    { var deadline = DateTime.UtcNow.AddSeconds(15); while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Desktop acceptance timed out"); await Task.Delay(30); } }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);

}

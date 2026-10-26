using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MiniMcp.Configuration;
using MiniMcp.Desktop;
using MiniMcp.Desktop.ViewModels;
using MiniMcp.Desktop.Views;
using MiniMcp.Desktop.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace MiniMcp.DesktopChecks;

// Separate, explicit UI run. It hosts the real App/Window with a real Store rooted in the
// fixture, so neither the user's settings nor policy can be saved by UI automation.
internal static class UiSmoke
{
    internal static int Run(string? screenshotDirectory = null)
    {
        var result = 1;
        var thread = new Thread(() =>
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                var fixture = new SettingsFixture(seed: false);
                ILogger? logger = null;
                var runtime = new McpRuntime(0, configureEndpoints: endpoints => endpoints.MapGet("/ui-logger", (ILoggerFactory factory) =>
                { logger = factory.CreateLogger("Desktop.UI"); return Results.Ok(); }));
                var app = new App(runtime, fixture.Editor);
                var bindingErrors = new List<string>();
                app.DebugSettings.BindingFailed += (_, args) => bindingErrors.Add(args.Message);
                app.UnhandledException += (_, args) => { Console.WriteLine("FAIL UI unhandled: " + args.Exception); args.Handled = true; app.Exit(); };
                DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
                {
                    try
                    {
                        await Until(() => app.MainWindow is not null && fixture.Editor.IsLoaded && !fixture.Editor.IsBusy);
                        var window = app.MainWindow!;
                        var root = (FrameworkElement)window.Content;
                        await Task.Delay(100);
                        T Find<T>(string id) where T : FrameworkElement => Walk(root).OfType<T>()
                            .FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == id)
                            ?? throw new Exception("Missing UI element: " + id);
                        var navigation = Walk(root).OfType<NavigationView>().Single();
                        var frame = Find<Frame>("WorkspaceFrame");
                        var transitions = new List<NavigationTransitionInfo>();
                        frame.Navigating += (_, args) => transitions.Add(args.NavigationTransitionInfo);
                        async Task Navigate(string id)
                        {
                            var type = id switch { "OverviewNavigation" => typeof(OverviewView), "SettingsNavigation" => typeof(SettingsView), "LogsNavigation" => typeof(LogsView), _ => throw new Exception("Unknown page") };
                            navigation.SelectedItem = Find<NavigationViewItem>(id);
                            await Until(() => frame.Content is Page { IsLoaded: true } page && page.GetType() == type);
                            await Task.Delay(25);
                        }
                        Assert(navigation.MenuItems.Count == 3, "Only implemented pages in navigation");
                        Find<TextBox>("SettingsFilePath").Text = fixture.Source;
                        Invoke(Find<Button>("LoadSettings"));
                        await Until(() => fixture.Editor.SettingsPath == fixture.Source && !fixture.Editor.IsBusy);
                        var project = Find<TextBox>("ProjectRootEditor");
                        Assert(project.IsEnabled && !fixture.Editor.HasSavedSettings, "First-run form is editable");
                        project.Text = fixture.Root;
                        Find<ComboBox>("AuthenticationEditor").SelectedIndex = 1;
                        Find<TextBox>("GitPathEditor").Text = "missing-git";
                        await Until(() => fixture.Editor.ProjectRoot == fixture.Root && fixture.Editor.AuthenticationIndex == 1 && fixture.Editor.IsSettingsDirty);
                        Find<ComboBox>("AuthenticationEditor").SelectedIndex = 0;
                        var tunnel = Find<CheckBox>("TunnelEditor");
                        await Until(() => tunnel.IsEnabled);
                        tunnel.IsChecked = true;
                        await Until(() => fixture.Editor.TunnelEnabled);
                        Find<ComboBox>("AuthenticationEditor").SelectedIndex = 1;
                        await Until(() => !fixture.Editor.TunnelEnabled && tunnel.IsChecked == false);
                        await Navigate("OverviewNavigation");
                        await Navigate("SettingsNavigation");
                        Assert(Find<TextBox>("GitPathEditor").Text == "missing-git" && ReferenceEquals(project, Find<TextBox>("ProjectRootEditor")), "Navigation retains controls and draft");
                        Find<TextBox>("PortEditor").Text = "invalid";
                        Invoke(Find<Button>("SaveSettings"));
                        await Until(() => fixture.Editor.HasError && !fixture.Editor.IsBusy && fixture.Editor.CanLoad);
                        Assert(Find<InfoBar>("SettingsError").IsOpen && Find<TextBox>("PortEditor").Text == "invalid" && !File.Exists(fixture.Source), "Validation shown, no invalid save");
                        Find<TextBox>("PortEditor").Text = "8000";
                        Invoke(Find<Button>("SaveSettings"));
                        await Until(() => fixture.Editor.HasSavedSettings && !fixture.Editor.IsBusy && fixture.Editor.CanLoad);
                        Assert(!fixture.Editor.HasError && !fixture.Editor.HasUnsavedChanges, "UI saves real settings");
                        Invoke(Find<Button>("SavePolicy"));
                        await Until(() => File.Exists(fixture.Paths.PolicyFile) && !fixture.Editor.IsBusy && fixture.Editor.CanLoad);
                        await Navigate("OverviewNavigation");
                        Invoke(Find<Button>("Start"));
                        await Until(() => runtime.Snapshot.State == RuntimeState.Running && Find<Button>("Stop").IsEnabled);
                        var firstRun = runtime.Snapshot.RunId;
                        Assert(Find<TextBlock>("Configuration").Text.Contains("ReadOnly"), "Overview shows actual mode");
                        Assert(runtime.DiagnosticSubscriberCounts == (1, 1), "One subscription pair for the whole window");
                        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                        (await http.GetAsync(new Uri(runtime.Address!, "ui-logger"))).EnsureSuccessStatusCode();
                        var uiLogger = logger ?? throw new Exception("Logger was not captured");
                        uiLogger.LogWarning("UI needle\t\u001b[31m"); uiLogger.LogInformation("Authorization: Bearer ui-log-secret");
                        await Navigate("LogsNavigation");
                        var logList = Find<ListView>("LogList");
                        Find<TextBox>("LogSearch").Text = "UI needle";
                        await Until(() => logList.Items.OfType<DesktopLogRow>().Any(row => row.Entry.Message.StartsWith("UI needle")));
                        logList.SelectedIndex = 0;
                        Assert(Find<TextBox>("LogDetails").Text.Contains("\\t") && !Find<TextBox>("LogDetails").Text.Contains('\u001b'), "Log detail uses escaped data");
                        Assert(Walk(root).OfType<LogsView>().Single().SelectionText().Contains("UI needle"), "Copy selection text follows actual UI selection");
                        uiLogger.LogInformation("UI colors: information"); uiLogger.LogWarning("UI colors: warning");
                        uiLogger.LogError("UI colors: error"); uiLogger.LogCritical("UI colors: critical");
                        Find<TextBox>("LogSearch").Text = "UI colors:";
                        await Until(() => logList.Items.Count == 4);
                        logList.SelectedItems.Clear();
                        Windows.UI.Color? previousWarning = null;
                        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
                        {
                            root.RequestedTheme = theme;
                            await Task.Delay(100);
                            var rendered = Walk(logList).OfType<TextBlock>().Where(text => text.Text.Contains("UI colors:"))
                                .ToDictionary(text => text.Text[(text.Text.LastIndexOf(':') + 2)..]);
                            var info = ((SolidColorBrush)rendered["information"].Foreground).Color;
                            var warning = ((SolidColorBrush)rendered["warning"].Foreground).Color;
                            var error = ((SolidColorBrush)rendered["error"].Foreground).Color;
                            if (!new AccessibilitySettings().HighContrast)
                            {
                                Assert(info != warning && info != error && warning != error, "Realized log text has distinct readable severity colors");
                                Assert(previousWarning is null || previousWarning != warning, "Existing log rows re-resolve colors on theme changes");
                            }
                            Assert(rendered["critical"].FontWeight.Weight > rendered["error"].FontWeight.Weight,
                                "Critical emphasis reaches the rendered text");
                            using (var pointer = new PointerInput(window))
                            {
                                foreach (var row in logList.Items.OfType<DesktopLogRow>())
                                {
                                    var container = (ListViewItem)logList.ContainerFromItem(row);
                                    var text = Walk(container).OfType<TextBlock>().Single(block => block.Text == row.Text);
                                    var originalColor = ((SolidColorBrush)text.Foreground).Color;
                                    bool entered = false, exited = false;
                                    Microsoft.UI.Xaml.Input.PointerEventHandler onEntered = (_, _) => entered = true;
                                    Microsoft.UI.Xaml.Input.PointerEventHandler onExited = (_, _) => exited = true;
                                    container.AddHandler(UIElement.PointerEnteredEvent, onEntered, true);
                                    container.AddHandler(UIElement.PointerExitedEvent, onExited, true);
                                    try
                                    {
                                        pointer.MoveTo(Find<TextBox>("LogSearch"), root, window);
                                        await Task.Delay(50);
                                        pointer.MoveTo(container, root, window);
                                        await Until(() => entered); await Task.Delay(50);
                                        if (!new AccessibilitySettings().HighContrast)
                                            Assert(((SolidColorBrush)text.Foreground).Color == originalColor, "Log severity color survives real pointer hover: " + row.Entry.Level);
                                        pointer.MoveTo(Find<TextBox>("LogSearch"), root, window);
                                        await Until(() => exited); await Task.Delay(50);
                                        Assert(((SolidColorBrush)text.Foreground).Color == originalColor, "Log color returns after pointer exit: " + row.Entry.Level);
                                        logList.SelectedItem = row;
                                        await Task.Delay(50);
                                        if (!new AccessibilitySettings().HighContrast)
                                            Assert(((SolidColorBrush)text.Foreground).Color == originalColor, "Selection retains log severity color: " + row.Entry.Level);
                                        logList.SelectedItems.Clear();
                                    }
                                    finally { container.RemoveHandler(UIElement.PointerEnteredEvent, onEntered); container.RemoveHandler(UIElement.PointerExitedEvent, onExited); }
                                }
                            }
                            previousWarning = warning;
                            if (screenshotDirectory is not null)
                            {
                                Directory.CreateDirectory(screenshotDirectory);
                                await VisualCapture.Save(root, Path.Combine(screenshotDirectory, "logs-colors-" + theme + ".png"));
                            }
                        }
                        Find<TextBox>("LogSearch").Text = "";
                        for (var index = 0; index < 500; index++) uiLogger.LogInformation("UI flood {Index}", index);
                        await Until(() => logList.Items.Count >= 80);
                        await Task.Delay(100);
                        Assert(logList.ItemsPanelRoot is ItemsStackPanel panel && panel.Children.Count < logList.Items.Count, "Log rows retain native virtualization");
                        Find<CheckBox>("LogAutoScroll").IsChecked = false;
                        var scroll = Walk(logList).OfType<ScrollViewer>().First();
                        scroll.ChangeView(null, 0, null, true); await Task.Delay(80);
                        uiLogger.LogInformation("UI paused-scroll");
                        await Until(() => logList.Items.OfType<DesktopLogRow>().Any(row => row.Entry.Message == "UI paused-scroll"));
                        Assert(scroll.VerticalOffset < 1, "Auto-scroll can be disabled");
                        uiLogger.LogError("UI error without lifecycle fault");
                        Find<ComboBox>("LogLevel").SelectedIndex = 3;
                        await Until(() => logList.Items.OfType<DesktopLogRow>().Any(row => row.Entry.Level == LogLevel.Error));
                        Assert(logList.Items.OfType<DesktopLogRow>().All(row => row.Entry.Level >= LogLevel.Error) && runtime.Snapshot.FaultCount == 0, "Level filtering cannot fabricate service failure");
                        Find<ComboBox>("LogLevel").SelectedIndex = 0;
                        Find<CheckBox>("LogCurrentRun").IsChecked = true;
                        Assert(!logList.Items.OfType<DesktopLogRow>().Any(row => row.Text.Contains("ui-log-secret")), "Raw secrets never enter rows");
                        await Navigate("SettingsNavigation");
                        Find<ComboBox>("ModeEditor").SelectedIndex = 1;
                        await Until(() => fixture.Editor.ModeIndex == 1 && fixture.Editor.IsPolicyDirty);
                        Invoke(Find<Button>("SavePolicy"));
                        await Until(() => fixture.Editor.NeedsRestart && !fixture.Editor.IsBusy && fixture.Editor.CanLoad);
                        Assert(Find<InfoBar>("RestartRequired").IsOpen && runtime.Snapshot.RunId == firstRun &&
                            runtime.Snapshot.Configuration!.Policy.Mode == ExecutionMode.ReadOnly, "Save requires restart, old run/mode retained");
                        await Navigate("OverviewNavigation");
                        Invoke(Find<Button>("Restart"));
                        await Until(() => runtime.Snapshot.State == RuntimeState.Running && runtime.Snapshot.RunId != firstRun && Find<Button>("Stop").IsEnabled);
                        Assert(runtime.Snapshot.Configuration!.Policy.Mode == ExecutionMode.ProjectEdit && !Find<InfoBar>("RestartRequired").IsOpen, "Explicit restart applies saved mode");
                        await Navigate("LogsNavigation");
                        await Until(() => logList.Items.Count > 0 && logList.Items.OfType<DesktopLogRow>().All(row => row.Entry.RunId == runtime.Snapshot.RunId));
                        Assert(runtime.DiagnosticSubscriberCounts == (1, 1), "Navigation/restart do not duplicate subscriptions");
                        await Navigate("SettingsNavigation");
                        root.RequestedTheme = ElementTheme.Dark;
                        var scale = root.XamlRoot.RasterizationScale;
                        window.AppWindow.Resize(new((int)(520 * scale), (int)(720 * scale)));
                        await Task.Delay(120);
                        Assert(navigation.DisplayMode != NavigationViewDisplayMode.Expanded && project.ActualWidth > 80, "Narrow layout collapses navigation and retains form");
                        project.Focus(FocusState.Keyboard);
                        Assert(project.FocusState != FocusState.Unfocused, "Native keyboard focus");
                        root.RequestedTheme = ElementTheme.Light;

                        // A real revision conflict through the bound form, followed by the actual discard dialog.
                        var external = await fixture.Store.ReadSettingsAsync(fixture.Source, fixture.DirectoryPath);
                        await File.WriteAllTextAsync(fixture.Source, ConfigurationJson.SerializeSettings(external.Value with { Port = 8099 }));
                        Find<TextBox>("PortEditor").Text = "8111";
                        Invoke(Find<Button>("SaveSettings"));
                        await Until(() => fixture.Editor.HasError && !fixture.Editor.IsBusy && fixture.Editor.CanLoad);
                        Assert(fixture.Editor.PortText == "8111" && fixture.Editor.ErrorMessage.Contains("外部修改"), "UI conflict preserves draft");
                        Invoke(Find<Button>("LoadSettings"));
                        ContentDialog? dialog = null;
                        await Until(() => (dialog = OpenDialog(root)) is not null);
                        dialog!.Hide();
                        await Until(() => fixture.Editor.CanLoad);
                        Assert(fixture.Editor.PortText == "8111", "Cancel discard preserves draft");
                        Invoke(Find<Button>("LoadSettings"));
                        await Until(() => (dialog = OpenDialog(root)) is not null);
                        var primary = Walk(dialog!).OfType<Button>().First(button => button.Name == "PrimaryButton");
                        Invoke(primary);
                        await Until(() => !fixture.Editor.IsBusy && fixture.Editor.PortText == "8099");
                        Assert(!fixture.Editor.HasUnsavedChanges && !fixture.Editor.HasError, "Confirmed reload loads external settings");
                        var designScale = root.XamlRoot.RasterizationScale;
                        window.AppWindow.Resize(new((int)(1440 * designScale), (int)(900 * designScale)));
                        await Task.Delay(160);
                        var settingsScroll = Walk((Page)frame.Content).OfType<ScrollViewer>().First();
                        settingsScroll.ChangeView(null, Math.Min(240, settingsScroll.ScrollableHeight), null, true);
                        await Task.Delay(80);
                        var retainedOffset = settingsScroll.VerticalOffset;
                        await Navigate("OverviewNavigation"); await Navigate("SettingsNavigation");
                        Assert(Math.Abs(settingsScroll.VerticalOffset - retainedOffset) < 1 && ReferenceEquals(project, Find<TextBox>("ProjectRootEditor")),
                            "Native cached navigation preserves the form instance and scroll position");
                        for (var index = 0; index < 6; index++) navigation.SelectedItem = Find<NavigationViewItem>(index % 2 == 0 ? "LogsNavigation" : "OverviewNavigation");
                        await Navigate("SettingsNavigation");
                        double? commonX = null, commonWidth = null;
                        foreach (var (id, name) in new[] { ("OverviewNavigation", "overview"), ("SettingsNavigation", "settings"), ("LogsNavigation", "logs") })
                        {
                            navigation.SelectedItem = Find<NavigationViewItem>(id);
                            await Until(() => frame.Content is Page { IsLoaded: true });
                            await Task.Delay(400);
                            var page = (Page)frame.Content;
                            var header = Walk(page).OfType<PageHeader>().Single();
                            var x = header.TransformToVisual(root).TransformPoint(new Point()).X;
                            commonX ??= x; commonWidth ??= page.ActualWidth;
                            Assert(Math.Abs(x - commonX.Value) < 1 && Math.Abs(page.ActualWidth - commonWidth.Value) < 1 && page.ActualWidth <= 1200,
                                "Shared page gutter/header/width: " + name);
                            Assert(page.ActualTheme == root.ActualTheme, "Cached page follows workspace theme: " + name);
                            if (page is LogsView && root.ActualTheme == ElementTheme.Light)
                            {
                                var foreground = ((SolidColorBrush)Find<TextBox>("LogSearch").Foreground).Color;
                                Assert(foreground.R + foreground.G + foreground.B < 384, "Inactive log page does not keep dark foreground resources");
                            }
                            if (screenshotDirectory is not null)
                            {
                                Directory.CreateDirectory(screenshotDirectory);
                                if (page is SettingsView or OverviewView)
                                {
                                    var pageScroll = Walk(page).OfType<ScrollViewer>().First();
                                    pageScroll.ChangeView(null, 0, null, true);
                                    await Task.Delay(80);
                                }
                                await VisualCapture.Save(root, Path.Combine(screenshotDirectory, name + ".png"));
                            }
                        }
                        Assert(frame.BackStack.Count == 0 && transitions.Count > 0 && transitions.All(info =>
                            new UISettings().AnimationsEnabled ? info is EntranceNavigationTransitionInfo : info is SuppressNavigationTransitionInfo),
                            "Native entrance transitions respect OS animation preference without growing history");
                        if (screenshotDirectory is not null)
                        {
                            root.RequestedTheme = ElementTheme.Dark;
                            await Navigate("OverviewNavigation"); await Task.Delay(400);
                            await VisualCapture.Save(root, Path.Combine(screenshotDirectory, "overview-dark.png"));
                            root.RequestedTheme = ElementTheme.Light;
                        }
                        await Navigate("SettingsNavigation");
                        var tunnelFile = Path.Combine(fixture.DirectoryPath, "desktop-tunnel.token");
                        await File.WriteAllTextAsync(tunnelFile, JsonSerializer.Serialize(new DesktopTunnelScenario(fixture.DirectoryPath)));
                        Find<ComboBox>("AuthenticationEditor").SelectedIndex = 0;
                        Find<TextBox>("PublicOriginEditor").Text = "https://fixture.invalid";
                        Find<TextBox>("TeamEditor").Text = "fixture";
                        Find<TextBox>("AudienceEditor").Text = "fixture-aud";
                        Find<TextBox>("CloudflaredEditor").Text = Environment.ProcessPath!;
                        Find<TextBox>("TokenFileEditor").Text = tunnelFile;
                        Find<CheckBox>("TunnelEditor").IsChecked = true;
                        Invoke(Find<Button>("SaveSettings"));
                        await Until(() => !fixture.Editor.HasUnsavedChanges && fixture.Editor.CanLoad);
                        await Navigate("OverviewNavigation");
                        Invoke(Find<Button>("Restart"));
                        await Until(() => runtime.Snapshot.Tunnel.State == TunnelState.Running && Find<Button>("StopTunnel").IsEnabled);
                        var tunnelRun = runtime.Snapshot.RunId;
                        Invoke(Find<Button>("StopTunnel"));
                        await Until(() => runtime.Snapshot.Tunnel.State == TunnelState.Stopped && Find<Button>("StartTunnel").IsEnabled);
                        Assert(runtime.Snapshot.State == RuntimeState.Running && runtime.Snapshot.RunId == tunnelRun, "Independent tunnel stop retains MCP");
                        Invoke(Find<Button>("StartTunnel"));
                        await Until(() => runtime.Snapshot.Tunnel.State == TunnelState.Running && Find<Button>("StopTunnel").IsEnabled);
                        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryPath, "tunnel-exit.request"), "exit");
                        await Until(() => Find<TextBlock>("TunnelStatus").Text.Contains("Faulted") && Find<Button>("StartTunnel").IsEnabled);
                        Assert(runtime.Snapshot.State == RuntimeState.Running && runtime.Snapshot.FaultCount > 0 && Find<TextBlock>("TunnelError").Text.Length > 0, "Unexpected exit updates the UI without interaction or fake MCP shutdown");
                        (await http.GetAsync(new Uri(runtime.Address!, "healthz"))).EnsureSuccessStatusCode();
                        Invoke(Find<Button>("StartTunnel"));
                        await Until(() => runtime.Snapshot.Tunnel.State == TunnelState.Running && Find<Button>("StopTunnel").IsEnabled);
                        Assert(runtime.Snapshot.RunId == tunnelRun && runtime.Snapshot.FaultCount > 0, "Explicit tunnel retry retains actual run fault history");
                        var closingTunnelPid = runtime.Snapshot.Tunnel.ProcessId!.Value;
                        await Navigate("LogsNavigation");
                        await Until(() => logList.Items.OfType<DesktopLogRow>().Any(row => row.Entry.Message.Contains("Desktop tunnel fixture")));
                        Assert(!logList.Items.OfType<DesktopLogRow>().Any(row => row.Text.Contains("desktop-tunnel-secret")), "Child process secrets remain redacted in the log UI");
                        Assert(bindingErrors.Count == 0, "No XAML binding failures: " + string.Join("; ", bindingErrors));
                        Console.WriteLine("PASS UI: bindings/settings/navigation/restart, themed log colors across real pointer hover/exit and selection, virtualized filtered logs and controllable scrolling, live tunnel controls and unexpected-exit diagnosis");

                        window.Closed += (_, _) =>
                        {
                            try
                            {
                                Assert(runtime.Snapshot.State == RuntimeState.Disposed && !runtime.Snapshot.HasOwnedResources && runtime.DiagnosticSubscriberCounts == (0, 0), "Window close actually released Runtime/subscriptions");
                                try
                                {
                                    using var child = System.Diagnostics.Process.GetProcessById(closingTunnelPid);
                                    Assert(child.HasExited, "Active tunnel child ended before closing");
                                }
                                catch (ArgumentException) { } // Already reaped.
                                fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
                                Console.WriteLine("PASS UI: system close disposes active Runtime and temporary fixture"); result = 0;
                            }
                            catch (Exception error) { Console.WriteLine("FAIL UI close: " + error); }
                            app.Exit();
                        };
                        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                        if (!PostMessage(handle, 0x112, (IntPtr)0xf060, IntPtr.Zero)) throw new Exception("Cannot request system close");
                    }
                    catch (Exception error)
                    {
                        Console.WriteLine("FAIL UI: " + error);
                        try { await runtime.DisposeAsync(); await fixture.DisposeAsync(); } catch (Exception cleanup) { Console.WriteLine("FAIL UI cleanup: " + cleanup); }
                        app.Exit();
                    }
                });
            });
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        return result;
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static ContentDialog? OpenDialog(FrameworkElement root) => VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
        .SelectMany(popup => Walk(popup.Child)).OfType<ContentDialog>().FirstOrDefault();
    private static void Invoke(Button button)
    {
        Assert(button.IsEnabled, "Button enabled: " + AutomationProperties.GetAutomationId(button));
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("UI condition did not complete"); await Task.Delay(25); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

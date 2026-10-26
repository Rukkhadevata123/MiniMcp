using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MiniMcp.Configuration;
using MiniMcp.Desktop;

namespace MiniMcp.DesktopChecks;

// Separate, explicit UI run. It hosts the real App/Window with a real Store rooted in the
// fixture, so neither the user's settings nor policy can be saved by UI automation.
internal static class UiSmoke
{
    internal static int Run()
    {
        var result = 1;
        var thread = new Thread(() =>
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                var fixture = new SettingsFixture(seed: false);
                var runtime = new McpRuntime(0);
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
                        Assert(navigation.MenuItems.Count == 2, "Only implemented pages in navigation");
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
                        navigation.SelectedItem = Find<NavigationViewItem>("OverviewNavigation");
                        navigation.SelectedItem = Find<NavigationViewItem>("SettingsNavigation");
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
                        navigation.SelectedItem = Find<NavigationViewItem>("OverviewNavigation");
                        Invoke(Find<Button>("Start"));
                        await Until(() => runtime.Snapshot.State == RuntimeState.Running && Find<Button>("Stop").IsEnabled);
                        var firstRun = runtime.Snapshot.RunId;
                        Assert(Find<TextBlock>("Configuration").Text.Contains("ReadOnly"), "Overview shows actual mode");
                        navigation.SelectedItem = Find<NavigationViewItem>("SettingsNavigation");
                        Find<ComboBox>("ModeEditor").SelectedIndex = 1;
                        await Until(() => fixture.Editor.ModeIndex == 1 && fixture.Editor.IsPolicyDirty);
                        Invoke(Find<Button>("SavePolicy"));
                        await Until(() => fixture.Editor.NeedsRestart && !fixture.Editor.IsBusy && fixture.Editor.CanLoad);
                        Assert(Find<InfoBar>("RestartRequired").IsOpen && runtime.Snapshot.RunId == firstRun &&
                            runtime.Snapshot.Configuration!.Policy.Mode == ExecutionMode.ReadOnly, "Save requires restart, old run/mode retained");
                        navigation.SelectedItem = Find<NavigationViewItem>("OverviewNavigation");
                        Invoke(Find<Button>("Restart"));
                        await Until(() => runtime.Snapshot.State == RuntimeState.Running && runtime.Snapshot.RunId != firstRun && Find<Button>("Stop").IsEnabled);
                        Assert(runtime.Snapshot.Configuration!.Policy.Mode == ExecutionMode.ProjectEdit && !Find<InfoBar>("RestartRequired").IsOpen, "Explicit restart applies saved mode");
                        navigation.SelectedItem = Find<NavigationViewItem>("SettingsNavigation");
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
                        Assert(bindingErrors.Count == 0, "No XAML binding failures: " + string.Join("; ", bindingErrors));
                        Console.WriteLine("PASS UI: real bindings/navigation/first-run saves/validation/conflict/discard/dialogs/theme/narrow layout/keyboard/restart");

                        window.Closed += (_, _) =>
                        {
                            try
                            {
                                Assert(runtime.Snapshot.State == RuntimeState.Disposed && !runtime.Snapshot.HasOwnedResources, "Window close actually released Runtime");
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

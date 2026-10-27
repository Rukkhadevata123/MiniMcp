using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
using MiniMcp.Recovery;
using MiniMcp.Services.Workspace;

namespace MiniMcp.DesktopChecks;

internal static class ReadmeScreenshots
{
    internal static int Run(string outputDirectory, string artifactDirectory)
    {
        var output = Path.GetFullPath(outputDirectory); var artifacts = Path.GetFullPath(artifactDirectory);
        Directory.CreateDirectory(output); Directory.CreateDirectory(artifacts);
        var result = 1;
        var thread = new Thread(() =>
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                var index = 1;
                while (Directory.Exists(Path.Combine(artifacts, "demo-" + index))) index++;
                var directory = Directory.CreateDirectory(Path.Combine(artifacts, "demo-" + index)).FullName;
                var project = Directory.CreateDirectory(Path.Combine(directory, "project")).FullName;
                var paths = new ConfigurationPaths(Path.Combine(directory, "config"), Path.Combine(directory, "state"));
                Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
                var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
                var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
                var settings = new AppSettings
                {
                    ProjectRoot = project, Port = port,
                    Authentication = new() { Mode = AuthenticationMode.CloudflareAccess, PublicOrigin = "https://mcp.example.com", Team = "example", Audience = "demo-audience" }
                };
                var policy = new ExecutionPolicy { Mode = ExecutionMode.ProjectEdit };
                WriteNew(paths.SettingsFile, ConfigurationJson.SerializeSettings(settings));
                WriteNew(paths.PolicyFile, ConfigurationJson.SerializePolicy(policy));
                WriteNew(Path.Combine(project, "README.md"), "# 示例项目\n\n使用 MiniMcp 读取和搜索项目文件。\n");
                var editor = new SettingsEditor(new ConfigurationStore(paths), paths, new ConfigurationValidator(paths).Validate);
                ILogger? logger = null; var ephemeralProtection = false;
                var runtime = new McpRuntime(port, services =>
                {
                    services.AddDataProtection().UseEphemeralDataProtectionProvider();
                    services.AddSingleton<IHostedService>(provider =>
                    { logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("MiniMcp.Demo");
                        ephemeralProtection = provider.GetRequiredService<IDataProtectionProvider>() is EphemeralDataProtectionProvider;
                        return new LoggerOwner(); });
                });
                var app = new App(runtime, editor);
                app.UnhandledException += (_, args) => { Console.WriteLine("FAIL screenshot window: " + args.Exception); args.Handled = true; app.Exit(); };
                DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
                {
                    try
                    {
                        await Until(() => app.MainWindow is not null && editor.IsLoaded && editor.CanLoad);
                        var window = app.MainWindow!; var root = (FrameworkElement)window.Content;
                        T Find<T>(string id) where T : FrameworkElement => Walk(root).OfType<T>().First(element => AutomationProperties.GetAutomationId(element) == id);
                        var navigation = Walk(root).OfType<NavigationView>().Single();
                        var frame = Find<Frame>("WorkspaceFrame");
                        async Task Navigate(string id)
                        {
                            navigation.SelectedItem = Find<NavigationViewItem>(id);
                            await Until(() => frame.Content is Page { IsLoaded: true }); await Task.Delay(400);
                        }
                        async Task Capture(string name)
                        {
                            if (navigation.SelectedItem is Control selected) selected.Focus(FocusState.Pointer);
                            await Task.Delay(100);
                            await VisualCapture.Save(root, Path.Combine(output, name + ".png"), minimumScale: 2);
                        }
                        root.RequestedTheme = ElementTheme.Light;
                        var scale = root.XamlRoot.RasterizationScale;
                        window.AppWindow.Resize(new((int)(1200 * scale), (int)(1000 * scale)));
                        await Task.Delay(200); navigation.IsPaneOpen = true;
                        await Navigate("OverviewNavigation"); Invoke(Find<Button>("Start"));
                        await Until(() => runtime.Snapshot.State == RuntimeState.Running && Find<Button>("Stop").IsEnabled);
                        if (!ephemeralProtection) throw new InvalidOperationException("Screenshot data protection must use an ephemeral provider.");
                        var source = (await runtime.Execution!.ApplyPatchAsync("*** Begin Patch\n*** Update File: README.md\n@@\n-使用 MiniMcp 读取和搜索项目文件。\n+使用 MiniMcp 读取、搜索和修改项目文件，按记录恢复改动。\n" +
                            "*** Add File: Greeting.cs\n+internal static class Greeting\n+{\n+    public static string Hello() => \"Hello, MiniMcp!\";\n+}\n*** End Patch")).Commit!.ChangeId;
                        var demoLogger = logger ?? throw new InvalidOperationException("Demo logger was not initialized.");
                        demoLogger.LogInformation("项目文件已更新，原始内容和修改记录已保存。");
                        await using (var history = runtime.OpenRecoverySession())
                        {
                            var prepared = await history.PrepareAsync(source);
                            var file = Path.Combine(project, "README.md"); var current = File.ReadAllBytes(file);
                            await File.AppendAllTextAsync(file, "\n外部编辑示例。\n");
                            try { await history.RestoreAsync(prepared); throw new Exception("Conflict demo unexpectedly restored files"); }
                            catch (WorkspaceException) { demoLogger.LogWarning("检测到文件在预览后发生变化，已取消本次恢复。"); }
                            await File.WriteAllBytesAsync(file, current);
                        }
                        demoLogger.LogInformation("项目文件已重新加载，可重新查看完整恢复预览。");
                        await Task.Delay(150);
                        await Capture("overview");
                        root.RequestedTheme = ElementTheme.Dark; await Task.Delay(250);
                        await Capture("overview-dark");
                        root.RequestedTheme = ElementTheme.Light;
                        await Navigate("RecoveryNavigation"); Invoke(Find<Button>("LoadRecords"));
                        var records = Find<ListView>("RecoveryList");
                        await Until(() => Find<Button>("LoadRecords").IsEnabled && records.Items.Count > 0);
                        records.SelectedItem = records.Items.OfType<DesktopRecoveryRow>().Single(row => row.ChangeId == source);
                        await Until(() => Find<Button>("RestoreRecord").IsEnabled);
                        await Task.Delay(150);
                        await Capture("recovery");
                        await Navigate("LogsNavigation");
                        Find<CheckBox>("LogAutoScroll").IsChecked = false;
                        var logs = Find<ListView>("LogList");
                        Find<TextBox>("LogSearch").Text = "MiniMcp.";
                        await Until(() => logs.Items.OfType<DesktopLogRow>().Any(row => row.Entry.Message.Contains("检测到文件")));
                        logs.SelectedItem = logs.Items.OfType<DesktopLogRow>().First(row => row.Entry.Message.Contains("检测到文件"));
                        logs.ScrollIntoView(logs.SelectedItem); await Task.Delay(700);
                        await Capture("logs");
                        await Navigate("SettingsNavigation");
                        var scroll = Walk((Page)frame.Content).OfType<ScrollViewer>().First(); scroll.ChangeView(null, 0, null, true);
                        await Task.Delay(150);
                        await Capture("settings");
                        var mode = Find<ComboBox>("ModeEditor");
                        var targetY = mode.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y;
                        scroll.ChangeView(null, scroll.VerticalOffset + targetY - 200, null, true); await Task.Delay(150);
                        await Capture("permissions");
                        await Navigate("OverviewNavigation"); Invoke(Find<Button>("Stop"));
                        await Until(() => runtime.Snapshot.State == RuntimeState.Stopped && Find<Button>("Start").IsEnabled);
                        window.Closed += (_, _) => { result = 0; Console.WriteLine("PASS README screenshots: real UI, isolated sample configuration, fresh high-resolution rendering"); app.Exit(); };
                        window.Close();
                    }
                    catch (Exception error)
                    { Console.WriteLine("FAIL README screenshots: " + error); try { await runtime.DisposeAsync(); } finally { app.Exit(); } }
                });
            });
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
    }
    private static void WriteNew(string path, string text)
    { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write); file.Write(new UTF8Encoding(false).GetBytes(text)); }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    { yield return root; for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in Walk(VisualTreeHelper.GetChild(root, index))) yield return child; }
    private static void Invoke(Button button)
    { var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button); ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke(); }
    private static async Task Until(Func<bool> condition)
    { var deadline = DateTime.UtcNow.AddSeconds(15); while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Screenshot UI timed out"); await Task.Delay(30); } }
    private sealed class LoggerOwner : IHostedService
    { public Task StartAsync(CancellationToken token) => Task.CompletedTask; public Task StopAsync(CancellationToken token) => Task.CompletedTask; }
}

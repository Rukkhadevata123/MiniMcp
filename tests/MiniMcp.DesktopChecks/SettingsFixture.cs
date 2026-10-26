using System.Text;
using MiniMcp.Configuration;
using MiniMcp.Desktop.ViewModels;

namespace MiniMcp.DesktopChecks;

internal sealed class SettingsFixture : IAsyncDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "minimcp-desktop-" + Guid.NewGuid().ToString("N"));
    public string Root { get; }
    public string Source { get; }
    public ConfigurationPaths Paths { get; }
    public ConfigurationStore Store { get; }
    public AppSettings Settings { get; }
    public SettingsEditor Editor { get; }
    public SettingsFixture(bool seed = true, Func<Task>? beforeCommit = null)
    {
        Root = Path.Combine(DirectoryPath, "project"); Directory.CreateDirectory(Root);
        Source = Path.Combine(DirectoryPath, "settings.json");
        Paths = new(Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state"));
        Store = new(Paths, beforeCommit); Settings = new() { ProjectRoot = Root, Authentication = new() { Mode = AuthenticationMode.Local } };
        if (seed) File.WriteAllText(Source, ConfigurationJson.SerializeSettings(Settings), new UTF8Encoding(false));
        Editor = new(Store, Paths, new ConfigurationValidator(Paths).Validate);
    }
    public async Task Load() { await Editor.LoadAsync(Source); if (Editor.HasError) throw new Exception(Editor.ErrorMessage); }
    public async ValueTask DisposeAsync()
    {
        await Editor.CancelPendingAsync(); await Editor.PendingOperation;
        var resolved = Path.GetFullPath(DirectoryPath);
        if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
            !Path.GetFileName(resolved).StartsWith("minimcp-desktop-", StringComparison.Ordinal)) throw new Exception("Unexpected desktop fixture root");
        Directory.Delete(resolved, true);
    }
}

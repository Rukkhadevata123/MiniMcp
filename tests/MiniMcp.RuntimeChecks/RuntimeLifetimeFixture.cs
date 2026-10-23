using MiniMcp.Configuration;

internal sealed class RuntimeLifetimeFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"minimcp-lifetime-{Guid.NewGuid():N}");

    public RuntimeLifetimeFixture()
    {
        var root = Path.Combine(_directory, "project");
        Directory.CreateDirectory(root);
        var paths = new ConfigurationPaths(Path.Combine(_directory, "user"), Path.Combine(_directory, "state"));
        Configuration = new ConfigurationValidator(paths).Validate(new AppSettings
        {
            ProjectRoot = root,
            Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local }
        }, Path.Combine(_directory, "settings.json"), new ExecutionPolicy());
    }

    public RuntimeConfiguration Configuration { get; }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

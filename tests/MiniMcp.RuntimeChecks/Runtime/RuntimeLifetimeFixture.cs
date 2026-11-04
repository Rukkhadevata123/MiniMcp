using MiniMcp.Configuration;

internal sealed class RuntimeLifetimeFixture : IDisposable
{
    private readonly string _directory = TemporaryTestDirectory.Reserve("lifetime");

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

    public void Dispose() => TemporaryTestDirectory.Delete(_directory);
}

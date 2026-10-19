using System.ComponentModel;
using ModelContextProtocol.Server;
using MiniMcp.Services;

namespace MiniMcp.Tools;

[McpServerToolType]
public sealed class BuildTools(BuildRunner runner)
{
    [McpServerTool(Name = "build_project", ReadOnly = false, Destructive = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Run dotnet build for a project or solution inside the configured project. Builds can execute project-defined code. Returns exit code, timeout status, and at most 16000 output characters.")]
    public Task<BuildResult> BuildProject(CancellationToken cancellationToken,
        [Description("Relative project/solution path, or a directory containing one project or solution.")] string path = ".") =>
        ToolErrors.RunAsync(() => runner.BuildAsync(path, cancellationToken));
}

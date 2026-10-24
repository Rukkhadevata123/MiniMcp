using System.Text;
using MiniMcp.Configuration;
using MiniMcp.Runtime;

namespace MiniMcp.Cli;

internal static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken, ConfigurationStore? store = null, string? workingDirectory = null)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"] or ["run", "--help"] or ["run", "-h"])
        {
            output.WriteLine("""
                MiniMcp

                用法：minimcp run [--config <path>] [--mode ReadOnly|ProjectEdit|FullAccess]
                      minimcp --help

                选项：
                  -h, --help  显示帮助
                  --config    settings.json 路径，省略时读取默认用户配置
                  --mode      仅覆盖本次会话的执行模式，省略时使用本机 policy.json

                端口取自 JSON，仅监听本机。/mcp 提供 stateless Streamable HTTP，/healthz 用于健康检查。
                所有模式允许读取和预演；补丁写入关闭，Bash 仅 FullAccess 可用。
                按 Ctrl+C 停止；Linux 支持 SIGTERM。
                """);
            return 0;
        }
        if (!TryParse(args, out var config, out var mode, out var argumentError))
        {
            error.WriteLine($"参数错误：{Visible(argumentError)} 请使用 --help 查看帮助。");
            return 2;
        }

        RuntimeConfiguration configuration;
        try
        {
            configuration = await (store ?? new ConfigurationStore()).LoadAsync(config,
                workingDirectory ?? Environment.CurrentDirectory, mode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("启动已取消。");
            return 130;
        }
        catch (Exception exception) when (exception is ConfigurationLoadException or ConfigurationValidationException
            or ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            error.WriteLine($"配置错误：{Visible(exception.Message)}");
            return 2;
        }
        catch (Exception exception)
        {
            error.WriteLine($"配置加载失败：{Visible(exception.Message)}");
            return 1;
        }

        var started = false;
        try
        {
            await using (var runtime = new McpRuntime())
            {
                await runtime.StartAsync(configuration, cancellationToken);
                started = true;
                var snapshot = runtime.Snapshot;
                output.WriteLine($"本机健康检查：{new Uri(snapshot.Address!, "healthz").AbsoluteUri}");
                output.WriteLine($"MCP：{new Uri(snapshot.Address!, "mcp").AbsoluteUri}");
                output.WriteLine($"认证模式：{configuration.Settings.Authentication.Mode}");
                output.WriteLine($"项目：{Visible(configuration.Settings.ProjectRoot)}");
                output.WriteLine($"配置执行模式：{configuration.Policy.Mode}");
                output.WriteLine("按 Ctrl+C 停止。");
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }
            output.WriteLine("已停止。");
            return 0;
        }
        catch (OperationCanceledException) when (!started && cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("启动已取消。");
            return 130;
        }
        catch (Exception exception)
        {
            error.WriteLine($"启动或停止失败：{Visible(exception.Message)}");
            return 1;
        }
    }

    private static bool TryParse(string[] args, out string? config, out ExecutionMode? mode, out string error)
    {
        config = null;
        mode = null;
        error = "";
        if (args[0] != "run") { error = "无法识别命令。"; return false; }
        for (var index = 1; index < args.Length; index += 2)
        {
            var option = args[index];
            if (option is not ("--config" or "--mode")) { error = $"无法识别选项 {option}。"; return false; }
            if (index + 1 == args.Length || string.IsNullOrWhiteSpace(args[index + 1])
                || args[index + 1].StartsWith("--", StringComparison.Ordinal) || args[index + 1] == "-h")
            { error = $"选项 {option} 缺少有效值。"; return false; }
            var value = args[index + 1];
            if (option == "--config")
            {
                if (config is not null) { error = "--config 不能重复。"; return false; }
                config = value;
            }
            else
            {
                if (mode is not null) { error = "--mode 不能重复。"; return false; }
                if (!Enum.TryParse<ExecutionMode>(value, out var selected) || Enum.GetName(selected) != value)
                { error = "--mode 必须为 ReadOnly、ProjectEdit 或 FullAccess。"; return false; }
                mode = selected;
            }
        }
        return true;
    }

    private static string Visible(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            if (char.IsControl(character)) result.Append($"\\u{(int)character:x4}");
            else result.Append(character);
        return result.ToString();
    }
}

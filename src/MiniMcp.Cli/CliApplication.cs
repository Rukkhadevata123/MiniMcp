using System.Text;
using MiniMcp.Configuration;
using MiniMcp.Runtime;

namespace MiniMcp.Cli;

internal static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken, ConfigurationStore? store = null, string? workingDirectory = null,
        Func<McpRuntime>? runtimeFactory = null)
    {
        try
        {
            return await ExecuteAsync(args, output, error, cancellationToken, store, workingDirectory, runtimeFactory).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Preserve the failure code even if a closed output pipe also prevents reporting it.
            try { error.WriteLine($"CLI 执行失败：{Visible(exception.Message)}"); }
            catch { }
            return CliExitCodes.Failure;
        }
    }

    private static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken, ConfigurationStore? store, string? workingDirectory, Func<McpRuntime>? runtimeFactory)
    {
        if (!TryParse(args, out var options, out var argumentError))
        {
            error.WriteLine($"参数错误：{Visible(argumentError)} 请使用 --help 查看帮助。");
            return CliExitCodes.InvalidInput;
        }
        if (options.Help)
        {
            ShowHelp(options.Command, output);
            return CliExitCodes.Success;
        }

        RuntimeConfiguration configuration;
        ConfigurationCheckResult? check = null;
        try
        {
            store ??= new ConfigurationStore();
            workingDirectory ??= Environment.CurrentDirectory;
            if (options.Command == Command.Check)
            {
                check = await store.CheckAsync(options.Configuration, workingDirectory, cancellationToken).ConfigureAwait(false);
                configuration = check.Configuration;
            }
            else configuration = await store.LoadAsync(options.Configuration, workingDirectory,
                options.Mode, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine(options.Command == Command.Check ? "检查已取消。" : "启动已取消。");
            return CliExitCodes.Cancelled;
        }
        catch (Exception exception) when (exception is ConfigurationLoadException or ConfigurationValidationException
            or ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            error.WriteLine($"配置错误：{Visible(exception.Message)}");
            return CliExitCodes.InvalidInput;
        }
        catch (Exception exception)
        {
            error.WriteLine($"配置加载失败：{Visible(exception.Message)}");
            return CliExitCodes.Failure;
        }

        if (check is not null)
        {
            WriteCheck(check, output, error);
            return CliExitCodes.Success;
        }
        return await RunSessionAsync(configuration, output, error, cancellationToken, runtimeFactory).ConfigureAwait(false);
    }

    private static void WriteCheck(ConfigurationCheckResult check, TextWriter output, TextWriter error)
    {
        var configuration = check.Configuration;
        output.WriteLine("配置与已启用依赖校验通过。");
        output.WriteLine($"配置：{Visible(configuration.SettingsFilePath)}");
        output.WriteLine($"项目：{Visible(configuration.Settings.ProjectRoot)}");
        output.WriteLine($"认证模式：{configuration.Settings.Authentication.Mode}（仅校验配置字段）");
        output.WriteLine($"配置执行模式：{configuration.Policy.Mode}");
        output.WriteLine(configuration.Policy.Mode == ExecutionMode.ReadOnly ? "Bash：当前模式不启用。" :
            $"Bash：程序文件已检查；{Visible(configuration.Settings.BashPath)}");
        output.WriteLine(configuration.Settings.Tunnel.Enabled ? "受管隧道：程序文件存在；token 文件可读且非空（未启动）。" : "受管隧道：关闭。");
        var endpoint = $"127.0.0.1:{configuration.Settings.Port}";
        if (check.PortStatus == PortCheckStatus.Bindable)
            output.WriteLine($"端口：{endpoint} 当前可绑定；本次探测已释放，下次启动需重新绑定。");
        else
            error.WriteLine($"端口提示：{endpoint} " +
                (check.PortStatus == PortCheckStatus.InUse ? "当前被占用" : "当前不可绑定") +
                $"（{check.PortError}）；配置校验已通过，启动前需解决端口问题。");
        output.WriteLine("仅检查本机配置与依赖；公网路由、Access 登录和 OAuth 未验证。");
    }

    private static async Task<int> RunSessionAsync(RuntimeConfiguration configuration, TextWriter output, TextWriter error,
        CancellationToken cancellationToken, Func<McpRuntime>? runtimeFactory)
    {
        var started = false;
        McpRuntime? runtime = null;
        try
        {
            await using (runtime = runtimeFactory is null ? new McpRuntime() : runtimeFactory())
            {
                await runtime.StartAsync(configuration, cancellationToken).ConfigureAwait(false);
                started = true;
                var snapshot = runtime.Snapshot;
                output.WriteLine($"本机健康检查：{new Uri(snapshot.Address!, "healthz").AbsoluteUri}");
                output.WriteLine($"MCP：{new Uri(snapshot.Address!, "mcp").AbsoluteUri}");
                output.WriteLine($"认证模式：{configuration.Settings.Authentication.Mode}");
                output.WriteLine($"项目：{Visible(configuration.Settings.ProjectRoot)}");
                output.WriteLine($"配置执行模式：{configuration.Policy.Mode}");
                output.WriteLine("按 Ctrl+C 停止。");
                try { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }
            output.WriteLine("已停止。");
            return CliExitCodes.Success;
        }
        catch (OperationCanceledException) when (!started && cancellationToken.IsCancellationRequested &&
            runtime?.Snapshot.State == RuntimeState.Disposed)
        {
            // A failed cleanup can also throw cancellation. It must remain a failure, not 130.
            error.WriteLine("启动已取消。");
            return CliExitCodes.Cancelled;
        }
        catch (Exception exception)
        {
            error.WriteLine($"启动或停止失败：{Visible(exception.Message)}");
            return CliExitCodes.Failure;
        }
    }

    private enum Command { Root, Run, Check }
    private sealed record Options(Command Command, string? Configuration = null, ExecutionMode? Mode = null, bool Help = false);

    private static bool TryParse(string[] args, out Options options, out string error)
    {
        options = new(Command.Root);
        error = "";
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            options = options with { Help = true };
            return true;
        }
        var command = args[0] switch { "run" => Command.Run, "check" => Command.Check, _ => Command.Root };
        if (command == Command.Root) { error = "无法识别命令。"; return false; }
        string? config = null;
        ExecutionMode? mode = null;
        var help = false;
        for (var index = 1; index < args.Length; index++)
        {
            var option = args[index];
            if (option is "--help" or "-h")
            {
                if (help) { error = "--help 不能重复。"; return false; }
                help = true;
                continue;
            }
            if (option != "--config" && !(option == "--mode" && command == Command.Run))
            { error = $"无法识别选项 {option}。"; return false; }
            if (index + 1 == args.Length || string.IsNullOrWhiteSpace(args[index + 1]) ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal) || args[index + 1] == "-h")
            { error = $"选项 {option} 缺少有效值。"; return false; }
            var value = args[++index];
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
        options = new(command, config, mode, help);
        return true;
    }

    private static void ShowHelp(Command command, TextWriter output)
    {
        output.WriteLine("MiniMcp");
        output.WriteLine();
        if (command != Command.Check) output.WriteLine("用法：minimcp run [--config <path>] [--mode ReadOnly|ProjectEdit|FullAccess]");
        if (command != Command.Run) output.WriteLine("用法：minimcp check [--config <path>]");
        output.WriteLine("      minimcp --help");
        output.WriteLine();
        output.WriteLine("选项：");
        output.WriteLine("  -h, --help  显示帮助，不加载配置");
        output.WriteLine("  --config    settings.json 路径，省略时读取默认用户配置；policy.json 固定来自用户目录");
        if (command != Command.Check) output.WriteLine("  --mode      仅覆盖 run 本次会话的执行模式，省略时使用本机 policy.json");
        output.WriteLine();
        if (command != Command.Run)
            output.WriteLine("check 只校验保存的配置、策略及已启用依赖，并给出端口当时的绑定提示；不启动 MCP/隧道、不写文件。");
        if (command != Command.Check)
        {
            output.WriteLine("run 端口取自 JSON，仅监听本机。/mcp 提供 stateless Streamable HTTP，/healthz 用于本机健康检查。");
            output.WriteLine("所有模式允许读取和预演；ProjectEdit 允许普通补丁，FullAccess 允许通过基础检查的补丁和 Bash；实际补丁强制保存恢复记录。");
            output.WriteLine("配置启用 cloudflared 时，在本机 MCP 就绪后启动隧道；按 Ctrl+C 停止，Linux 支持 SIGTERM。");
        }
        output.WriteLine("配置字段和本机进程状态不验证公网路由、Access 登录或 OAuth。");
        output.WriteLine("退出码：0 成功/正常结束，1 运行/清理故障，2 参数/配置/依赖错误，130 加载/启动取消且清理完成。");
    }

    private static string Visible(string value)
    {
        value = RuntimeDiagnosticText.Sanitize(value);
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            if (char.IsControl(character)) result.Append($"\\u{(int)character:x4}");
            else result.Append(character);
        return result.ToString();
    }
}

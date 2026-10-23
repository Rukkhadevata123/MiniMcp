using System.Globalization;
using System.Runtime.InteropServices;
using MiniMcp.Runtime;

if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine("""
        MiniMcp

        用法：minimcp run [--port <1-65535>]
              minimcp --help

        选项：
          -h, --help  显示帮助
          --port      HTTP 原型端口，默认 8000

        当前仅提供 /healthz，尚未启用 /mcp。按 Ctrl+C 停止。
        """);
    return 0;
}

var port = 8000;
if (args[0] != "run" || args.Length != 1 &&
    (args.Length != 3 || args[1] != "--port" ||
     !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
{
    Console.Error.WriteLine("命令或端口无效。请使用 --help 查看帮助。");
    return 2;
}

using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
Console.CancelKeyPress += cancelHandler;
using var terminate = OperatingSystem.IsLinux()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; shutdown.Cancel(); })
    : null;

try
{
    await using (var runtime = new McpRuntime(port))
    {
        await runtime.StartAsync(shutdown.Token);
        Console.WriteLine($"HTTP 原型已启动：{runtime.Address}healthz（尚未启用 /mcp）");
        Console.WriteLine("按 Ctrl+C 停止。");
        try { await Task.Delay(Timeout.Infinite, shutdown.Token); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }
    Console.WriteLine("已停止。");
    return 0;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    Console.Error.WriteLine("启动已取消。");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"启动或停止失败：{exception.Message}");
    return 1;
}
finally { Console.CancelKeyPress -= cancelHandler; }

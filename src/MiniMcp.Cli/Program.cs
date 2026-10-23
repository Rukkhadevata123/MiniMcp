using System.Runtime.InteropServices;
using MiniMcp.Cli;

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
    return await CliApplication.RunAsync(args, Console.Out, Console.Error, shutdown.Token);
}
finally { Console.CancelKeyPress -= cancelHandler; }

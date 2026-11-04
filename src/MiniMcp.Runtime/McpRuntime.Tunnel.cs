using Microsoft.Extensions.Logging;
using MiniMcp.Diagnostics;
using MiniMcp.Services.Processes;

namespace MiniMcp;

// Managed cloudflared tunnel owned by the running host.
public sealed partial class McpRuntime
{
    private async Task StartTunnelCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish(Snapshot with { Tunnel = new(TunnelState.Starting) });
        try
        {
            _tunnel = TunnelProcess.Start(Snapshot.Configuration!.Settings.Tunnel, _logProvider!, _tunnelChecks);
            Publish(Snapshot with { Tunnel = new(TunnelState.Starting, _tunnel.InstanceId, _tunnel.ProcessId) });
            await _tunnel.CheckStartedAsync(cancellationToken).ConfigureAwait(false);
            Publish(Snapshot with { Tunnel = Snapshot.Tunnel with { State = TunnelState.Running } });
        }
        catch (Exception error)
        {
            Publish(Snapshot with { Tunnel = Snapshot.Tunnel with { State = TunnelState.Faulted, ExitCode = _tunnel?.ExitCode, Error = error.Message } });
            throw;
        }
    }

    private async Task StopTunnelCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tunnel = _tunnel;
        if (tunnel is null)
        {
            if (Snapshot.Tunnel.State is not (TunnelState.Stopped or TunnelState.Disabled))
                Publish(Snapshot with { Tunnel = new(Snapshot.Configuration?.Settings.Tunnel.Enabled == true ? TunnelState.Stopped : TunnelState.Disabled) });
            return;
        }
        Publish(Snapshot with { Tunnel = Snapshot.Tunnel with { State = TunnelState.Stopping, Error = null } });
        try
        {
            await tunnel.StopAsync(cancellationToken).ConfigureAwait(false);
            _tunnel = null;
            Publish(Snapshot with { Tunnel = new(TunnelState.Stopped, tunnel.InstanceId, ExitCode: tunnel.ExitCode) });
        }
        catch (Exception error)
        {
            Publish(Snapshot with { Tunnel = Snapshot.Tunnel with { State = TunnelState.Faulted, ExitCode = tunnel.ExitCode, Error = error.Message } });
            throw;
        }
    }

    private void ObserveTunnel()
    {
        if (_tunnel is not { } tunnel || Snapshot.RunId is not { } runId)
            return;
        var logger = _logProvider!.CreateLogger("MiniMcp.Tunnel");
        tunnel.Observe((exitCode, error) =>
        {
            var safeError = LogSanitizer.Sanitize(error, RuntimeDiagnostics.MaxEntryCharacters, out _);
            if (_diagnostics.PublishTunnelFault(runId, tunnel.InstanceId, exitCode, safeError))
                logger.LogError("Tunnel fault: {Error}", safeError);
        });
    }
}

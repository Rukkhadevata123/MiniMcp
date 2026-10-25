using System.Net;
using System.Net.Sockets;

namespace MiniMcp.Configuration;

/// <summary>One local bind observation, not a reservation or a guarantee that a later start succeeds.</summary>
public enum PortCheckStatus { Bindable, InUse, Unavailable }

public sealed record ConfigurationCheckResult(RuntimeConfiguration Configuration, PortCheckStatus PortStatus,
    SocketError? PortError = null);

public sealed partial class ConfigurationStore
{
    /// <summary>
    /// Validates the saved configuration/policy and probes its loopback port without listening,
    /// starting programs, contacting authentication servers or writing configuration/state files.
    /// </summary>
    public async Task<ConfigurationCheckResult> CheckAsync(string? path, string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var configuration = await LoadAsync(path, workingDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        PortCheckStatus status;
        SocketError? error = null;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true
            };
            socket.Bind(new IPEndPoint(IPAddress.Loopback, configuration.Settings.Port));
            status = PortCheckStatus.Bindable;
        }
        catch (SocketException failure)
        {
            error = failure.SocketErrorCode;
            status = error == SocketError.AddressAlreadyInUse ? PortCheckStatus.InUse : PortCheckStatus.Unavailable;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(configuration, status, error);
    }
}

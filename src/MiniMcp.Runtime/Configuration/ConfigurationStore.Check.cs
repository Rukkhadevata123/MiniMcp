using System.Net;
using System.Net.Sockets;

namespace MiniMcp.Configuration;

/// <summary>One local bind observation, not a reservation or a guarantee that a later start succeeds.</summary>
public enum PortCheckStatus { Bindable, InUse, Unavailable }

public sealed record ConfigurationCheckResult(RuntimeConfiguration Configuration, PortCheckStatus PortStatus,
    SocketError? PortError = null, string? RipgrepPath = null, string? RipgrepError = null,
    string? GitPath = null, string? GitError = null);

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
        string? rgPath = null, rgError = null;
        try { rgPath = ExternalProgramLocator.FindRipgrep(configuration.Settings.RgPath, configuration.SettingsFilePath, configuration.Settings.ProjectRoot); }
        catch (Exception failure) when (failure is IOException or ArgumentException or UnauthorizedAccessException)
        { rgError = failure.Message; }
        string? gitPath = null, gitError = null;
        try { gitPath = ExternalProgramLocator.FindGit(configuration.Settings.GitPath, configuration.SettingsFilePath, configuration.Settings.ProjectRoot); }
        catch (Exception failure) when (failure is IOException or ArgumentException or UnauthorizedAccessException)
        { gitError = failure.Message; }
        return new(configuration, status, error, rgPath, rgError, gitPath, gitError);
    }
}

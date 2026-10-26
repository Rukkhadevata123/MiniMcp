using MiniMcp.Configuration;

namespace MiniMcp;

public enum RuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted,
    Disposed
}

/// <summary>
/// A coherent view of the current or most recent startup attempt. Configuration is absent
/// before the first start and is retained with the last run ID after stopping.
/// A fault may retain host ownership and its last bound address until cleanup succeeds.
/// </summary>
public sealed record RuntimeSnapshot(RuntimeState State, Guid? RunId = null,
    RuntimeConfiguration? Configuration = null, Uri? Address = null, string? Error = null)
{
    /// <summary>Retained host/session/process ownership; false does not cancel a pending startup.</summary>
    public bool HasOwnedResources { get; init; }

    /// <summary>Monotonic within a run; survives coalesced notifications and successful cleanup.</summary>
    public long FaultCount { get; init; }

    /// <summary>Local process status only; Running does not assert public routing or authentication.</summary>
    public TunnelSnapshot Tunnel { get; init; } = new(TunnelState.Stopped);
}

public enum TunnelState { Disabled, Stopped, Starting, Running, Stopping, Faulted }

/// <summary>InstanceId distinguishes independent tunnel attempts within the same Runtime run.</summary>
public sealed record TunnelSnapshot(TunnelState State, Guid? InstanceId = null,
    int? ProcessId = null, int? ExitCode = null, string? Error = null);

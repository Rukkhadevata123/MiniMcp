using System.Collections.Immutable;

namespace MiniMcp.Services;

internal enum PatchCommitState { Committed, RolledBack, Incomplete }
internal enum PatchCommitStage { Staged, BeforeInstall, Installed }

// An observed engine result, not an approval or a persisted recovery record.
// RolledBack includes failures before target installation when staging cleanup completed.
// After successful installation, leftover rollback backups are cleanup errors, not a failed commit.
internal sealed record PatchCommitOutcome(PatchCommitState State, Exception? Failure,
    ImmutableArray<Exception> RollbackErrors, ImmutableArray<Exception> CleanupErrors);

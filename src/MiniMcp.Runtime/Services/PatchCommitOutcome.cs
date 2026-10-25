using System.Collections.Immutable;

namespace MiniMcp.Services;

internal enum PatchCommitState { Committed, RolledBack, Incomplete }
internal enum PatchCommitStage { Staged, BeforeInstall, Installed, BeforeVerification }

// One applying record gets one context. This is an in-process binding, not approval or a saved token.
internal sealed class PatchExecutionContext(PatchPlan plan)
{
    private int _started;
    private int _invalidated;
    public Guid AttemptId { get; } = Guid.NewGuid();
    internal void Start(PatchPlan candidate)
    {
        if (!ReferenceEquals(plan, candidate) || Volatile.Read(ref _invalidated) != 0)
            throw new InvalidOperationException("The patch execution context is invalid or belongs to another plan.");
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("The patch execution context has already been used.");
    }
    internal void Invalidate() => Interlocked.Exchange(ref _invalidated, 1);
}

internal sealed record VerifiedPatchPath(string Path, TextFile? File);

// Read-only engine facts bound to an exact service, plan, attempt and optional applying context.
// The issuer is private to PatchService and is never exposed or serialized as an authorization token.
internal sealed class PatchCommitOutcome
{
    private readonly object _issuer;
    private readonly PatchExecutionContext? _context;
    private readonly bool _verified;
    internal PatchCommitOutcome(PatchPlan plan, object issuer, PatchExecutionContext? context, PatchCommitState state,
        Exception? failure, ImmutableArray<Exception> rollbackErrors, ImmutableArray<Exception> cleanupErrors)
        : this(plan, issuer, context, context?.AttemptId ?? Guid.NewGuid(), state, failure, rollbackErrors, cleanupErrors, false, [], null) { }
    private PatchCommitOutcome(PatchPlan plan, object issuer, PatchExecutionContext? context, Guid attemptId, PatchCommitState state,
        Exception? failure, ImmutableArray<Exception> rollbackErrors, ImmutableArray<Exception> cleanupErrors,
        bool verified, ImmutableArray<VerifiedPatchPath> paths, Exception? verificationFailure)
    {
        Plan = plan; _issuer = issuer; _context = context; AttemptId = attemptId; State = state; Failure = failure;
        RollbackErrors = rollbackErrors; CleanupErrors = cleanupErrors; _verified = verified;
        Paths = paths; VerificationFailure = verificationFailure;
    }
    internal PatchPlan Plan { get; }
    public Guid AttemptId { get; }
    public PatchCommitState State { get; }
    public Exception? Failure { get; }
    public ImmutableArray<Exception> RollbackErrors { get; }
    public ImmutableArray<Exception> CleanupErrors { get; }
    public ImmutableArray<VerifiedPatchPath> Paths { get; }
    public Exception? VerificationFailure { get; }
    public PatchCommitState VerifiedState => !_verified ? throw new InvalidOperationException("Patch outcome verification has not completed.") :
        VerificationFailure is null ? State : PatchCommitState.Incomplete;
    internal bool IsIssuedFor(PatchPlan plan, object issuer) => ReferenceEquals(Plan, plan) && ReferenceEquals(_issuer, issuer) && _verified;
    internal bool HasContext(PatchExecutionContext context) => ReferenceEquals(_context, context);
    internal PatchCommitOutcome WithVerification(object issuer, ImmutableArray<VerifiedPatchPath> paths, Exception? failure)
    {
        if (!ReferenceEquals(_issuer, issuer) || _verified) throw new InvalidOperationException("Patch outcome cannot be verified by this caller or twice.");
        return new(Plan, _issuer, _context, AttemptId, State, Failure, RollbackErrors, CleanupErrors, true, paths, failure);
    }
}

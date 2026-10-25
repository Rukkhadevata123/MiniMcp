using System.Text;
using MiniMcp.Configuration;
using MiniMcp.Services;

internal static class PatchWritePolicyChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("ordinary patch policy distinguishes read-only denial from attempts requiring real recovery preparation", async () =>
        {
            using var fixture = new Fixture();
            var before = fixture.Snapshot();
            var risk = await fixture.Assess("*** Add File: nested/new.md\n+new\n*** Update File: source.cs\n@@\n-old\n+new");
            Assert(!risk.RequiresApproval, "Ordinary batch");
            foreach (var mode in Enum.GetValues<ExecutionMode>())
            {
                var decision = PatchWritePolicy.Decide(risk, mode);
                Assert(ReferenceEquals(decision.Assessment, risk) && decision.Mode == mode, "Exact assessment and local mode");
                AssertDecision(decision, mode == ExecutionMode.ReadOnly ? PatchWriteDisposition.Denied : PatchWriteDisposition.Automatic,
                    mode == ExecutionMode.ReadOnly ? PatchWriteBlocker.ReadOnlyMode : PatchWriteBlocker.None);
                Assert(decision.RequiresRecoveryRecord == (mode != ExecutionMode.ReadOnly), "Every eligible write needs recovery");
            }
            Assert(before.SequenceEqual(fixture.Snapshot()), "No ordinary member executed");
        });

        await check("every high-risk patch trigger requires ProjectEdit approval while FullAccess still requires recovery", async () =>
        {
            using var fixture = new Fixture();
            var cases = new (string Body, PatchApprovalLimits Limits, PatchRiskReason Reason)[]
            {
                ("*** Add File: .github/workflows/check.cs\n+new", new(), PatchRiskReason.CriticalFile),
                ("*** Add File: custom.data\n+new", new(), PatchRiskReason.UnknownFileType),
                ("*** Delete File: source.cs", new(), PatchRiskReason.Delete),
                ("*** Update File: source.cs\n*** Move to: renamed.cs\n@@\n-old\n+new", new(), PatchRiskReason.Move),
                ("*** Add File: ordinary.md\n+new", new() { MaxPaths = 0 }, PatchRiskReason.PathLimitExceeded),
                ("*** Add File: ordinary.md\n+new", new() { MaxChangedLines = 0 }, PatchRiskReason.ChangedLineLimitExceeded),
                ("*** Add File: ordinary.md\n+new", new() { MaxChangedBytes = 3 }, PatchRiskReason.ChangedByteLimitExceeded)
            };
            var before = fixture.Snapshot();
            foreach (var (body, limits, reason) in cases)
            {
                var risk = await fixture.Assess(body, limits);
                Assert(risk.Reasons == reason && risk.RequiresApproval, "Expected trigger: " + reason);
                var edit = PatchWritePolicy.Decide(risk, ExecutionMode.ProjectEdit);
                AssertDecision(edit, PatchWriteDisposition.RequiresApproval, PatchWriteBlocker.ApprovalUnavailable);
                var full = PatchWritePolicy.Decide(risk, ExecutionMode.FullAccess);
                AssertDecision(full, PatchWriteDisposition.Automatic, PatchWriteBlocker.None);
                Assert(edit.RequiresRecoveryRecord && full.RequiresRecoveryRecord && full.Assessment.Reasons == reason, "Mode does not erase risk or recovery");
                AssertDecision(PatchWritePolicy.Decide(risk, ExecutionMode.ReadOnly), PatchWriteDisposition.Denied, PatchWriteBlocker.ReadOnlyMode);
            }
            Assert(before.SequenceEqual(fixture.Snapshot()), "No file, directory or state writes");
        });

        await check("mixed patch policy applies the highest risk to the whole preview without approving ordinary members", async () =>
        {
            using var fixture = new Fixture();
            var before = fixture.Snapshot();
            var risk = await fixture.Assess("*** Add File: nested/ordinary.md\n+new\n*** Add File: Directory.Build.cs\n+build\n" +
                "*** Add File: custom.data\n+data\n*** Delete File: source.cs\n*** Update File: move.cs\n*** Move to: renamed.cs",
                new() { MaxPaths = 0, MaxChangedLines = 0, MaxChangedBytes = 0 });
            var expectedReasons = PatchRiskReason.CriticalFile | PatchRiskReason.UnknownFileType | PatchRiskReason.Delete | PatchRiskReason.Move |
                PatchRiskReason.PathLimitExceeded | PatchRiskReason.ChangedLineLimitExceeded | PatchRiskReason.ChangedByteLimitExceeded;
            Assert(risk.Reasons == expectedReasons && risk.Changes[0].Reasons == PatchRiskReason.None, "All review causes retained");
            var decision = PatchWritePolicy.Decide(risk, ExecutionMode.ProjectEdit);
            AssertDecision(decision, PatchWriteDisposition.RequiresApproval, PatchWriteBlocker.ApprovalUnavailable);
            Assert(ReferenceEquals(decision.Assessment.Preview, risk.Preview) && ReferenceEquals(decision.Assessment.Plan, risk.Plan), "Exact complete preview");
            Assert(decision.Assessment.Reasons == expectedReasons && before.SequenceEqual(fixture.Snapshot()), "Whole batch remains unchanged");
        });

        await check("ProjectEdit policy honors exact batch boundaries and independently lowered limits", async () =>
        {
            using var fixture = new Fixture();
            const string body = "*** Add File: new.md\n+extra\n*** Update File: source.cs\n@@\n-old\n+new";
            var exact = new PatchApprovalLimits { MaxPaths = 2, MaxChangedLines = 3, MaxChangedBytes = 14 };
            var risk = await fixture.Assess(body, exact);
            Assert(risk.Size == new PatchBatchSize(2, 3, 14), "Known change size");
            AssertDecision(PatchWritePolicy.Decide(risk, ExecutionMode.ProjectEdit), PatchWriteDisposition.Automatic, PatchWriteBlocker.None);
            foreach (var (limits, reason) in new[]
            {
                (exact with { MaxPaths = 1 }, PatchRiskReason.PathLimitExceeded),
                (exact with { MaxChangedLines = 2 }, PatchRiskReason.ChangedLineLimitExceeded),
                (exact with { MaxChangedBytes = 13 }, PatchRiskReason.ChangedByteLimitExceeded)
            })
            {
                var exceeded = PatchRiskClassifier.Classify(risk.Preview, fixture.SettingsPath, limits);
                Assert(exceeded.Reasons == reason, "Single lowered dimension");
                AssertDecision(PatchWritePolicy.Decide(exceeded, ExecutionMode.ProjectEdit), PatchWriteDisposition.RequiresApproval,
                    PatchWriteBlocker.ApprovalUnavailable);
                AssertDecision(PatchWritePolicy.Decide(exceeded, ExecutionMode.FullAccess), PatchWriteDisposition.Automatic, PatchWriteBlocker.None);
            }
            var empty = await fixture.Assess("*** Add File: empty.md", new() { MaxPaths = 1, MaxChangedLines = 0, MaxChangedBytes = 0 });
            AssertDecision(PatchWritePolicy.Decide(empty, ExecutionMode.ProjectEdit), PatchWriteDisposition.Automatic, PatchWriteBlocker.None);
        });

        await check("unknown local execution modes fail closed for ordinary and high-risk patch assessments", async () =>
        {
            using var fixture = new Fixture();
            var ordinary = await fixture.Assess("*** Add File: ordinary.md\n+new");
            var critical = await fixture.Assess("*** Add File: settings.json\n+{}");
            foreach (var mode in new[] { (ExecutionMode)(-1), (ExecutionMode)3, (ExecutionMode)999, (ExecutionMode)int.MaxValue })
                foreach (var assessment in new[] { ordinary, critical })
                {
                    var decision = PatchWritePolicy.Decide(assessment, mode);
                    AssertDecision(decision, PatchWriteDisposition.Denied, PatchWriteBlocker.UnknownMode);
                    Assert(!decision.RequiresRecoveryRecord && ReferenceEquals(decision.Assessment, assessment), "Unknown modes cannot request execution");
                }
        });

        await check("patch write decisions use only their immutable assessment, observe cancellation and perform no I/O", async () =>
        {
            using var fixture = new Fixture();
            var risk = await fixture.Assess("*** Add File: nested/new.md\n+new");
            var expected = PatchWritePolicy.Decide(risk, ExecutionMode.ProjectEdit);
            Directory.Delete(fixture.Root, recursive: true);
            foreach (var mode in Enum.GetValues<ExecutionMode>())
            {
                var result = PatchWritePolicy.Decide(risk, mode);
                Assert(result.CanCommit == (mode != ExecutionMode.ReadOnly) && ReferenceEquals(result.Assessment, risk), "No live workspace or backup readiness lookup");
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                try { PatchWritePolicy.Decide(risk, mode, cancelled.Token); throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException) { }
            }
            Assert(PatchWritePolicy.Decide(risk, ExecutionMode.ProjectEdit) == expected && !Directory.Exists(fixture.Root), "No retained state or directory creation");
        });
    }

    private static void AssertDecision(PatchWriteDecision decision, PatchWriteDisposition disposition, PatchWriteBlocker blockers)
    {
        Assert(decision.Disposition == disposition && decision.Blockers == blockers, "Disposition and blocking causes");
        Assert(decision.CanCommit == (blockers == PatchWriteBlocker.None), "Only unblocked policy allows a recorded attempt");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Patch write policy: " + message); }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"minimcp-write-policy-{Guid.NewGuid():N}");
        public string Root { get; }
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        private readonly ProjectWorkspace _workspace;
        private readonly PatchService _service;
        public Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(_directory, "project")).FullName;
            File.WriteAllText(Path.Combine(Root, "source.cs"), "old\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Root, "move.cs"), "same\n", new UTF8Encoding(false));
            _workspace = new ProjectWorkspace(Root);
            _service = new PatchService(_workspace);
        }
        public async Task<PatchRiskAssessment> Assess(string body, PatchApprovalLimits? limits = null)
        {
            var plan = await _service.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch");
            return PatchRiskClassifier.Classify(PatchDiffBuilder.Create(plan), SettingsPath, limits ?? new());
        }
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(_directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(_directory, path) + (Directory.Exists(path) ? "/" : ":" + Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() { _workspace.Mutations.Dispose(); Directory.Delete(_directory, recursive: true); }
    }
}

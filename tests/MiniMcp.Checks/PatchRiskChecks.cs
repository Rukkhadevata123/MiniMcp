using System.Text;
using MiniMcp.Services;

internal static class PatchRiskChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("risk classification recognizes source and documentation without granting commit permission", async () =>
        {
            using var fixture = new Fixture();
            foreach (var path in new[] { "src/App.cs", "ui/Page.XAML", "src/示例.cs", "src/lib.rs", "src/main.py", "web/app.tsx", "README", "docs/README.md", "notes.txt" })
            {
                var risk = fixture.Classify(await fixture.Prepare($"*** Add File: {path}\n+new"));
                Assert(risk.Changes[0].Kind == PatchFileKind.Ordinary && !risk.RequiresApproval && risk.Reasons == PatchRiskReason.None, path);
            }
            fixture.Put("existing.cs", "old\n");
            var plan = await fixture.Prepare("*** Update File: existing.cs\n@@\n-old\n+new");
            var update = fixture.Classify(plan);
            Assert(!update.RequiresApproval && ReferenceEquals(update.Plan, plan), "Snapshot binding");
            Assert(File.ReadAllText(fixture.Path("existing.cs")) == "old\n" && !Directory.Exists(fixture.Path("src")), "No writes");
        });

        await check("critical rules override source and documentation suffixes across casing and nested paths", async () =>
        {
            using var fixture = new Fixture();
            foreach (var path in new[]
            {
                "scripts/start.SH", "tools/build.ps1", "app/Example.csproj", "build/Custom.targets", "build/Custom.props", "MiniMcp.slnx",
                "nested/dIrEcToRy.BuIlD.cs", "Directory.Packages.props", "global.json", "src/NUGET.CONFIG", "native/CMakeLists.txt",
                "nested/.GiThUb/workflows/build.cs", ".gitlab/jobs/readme.md", ".vscode/tasks.cs", ".devcontainer/README.md",
                "settings.json", "config/SETTINGS.local.JSON", "policy.json", "policy.local.json", "appsettings.Production.json",
                "package.json", "setup.py", "build.rs", "requirements-dev.txt", ".bashrc", "Dockerfile.local"
            })
            {
                var risk = fixture.Classify(await fixture.Prepare($"*** Add File: {path}\n+new"));
                Assert(risk.Changes[0].Kind == PatchFileKind.Critical && risk.Reasons == PatchRiskReason.CriticalFile && risk.RequiresApproval, path);
            }
        });

        await check("unknown file types and ambiguous trailing names require review without expanding the ordinary set", async () =>
        {
            using var fixture = new Fixture();
            foreach (var path in new[] { "data.json", "values.yaml", "custom.config", "payload.bin", "custom-entrypoint", "archive.cs.backup" })
            {
                var risk = fixture.Classify(await fixture.Prepare($"*** Add File: {path}\n+text"));
                Assert(risk.Changes[0].Kind == PatchFileKind.Unknown && risk.Reasons == PatchRiskReason.UnknownFileType && risk.RequiresApproval, path);
            }
            foreach (var path in new[] { "src./code.cs", "doc.md." })
            {
                var plan = await fixture.Prepare($"*** Add File: {path}\n+text");
                // Native path resolution may remove aliases before the classifier sees the plan.
                var expected = plan.Changes[0].Summary.Path == path ? PatchFileKind.Unknown : PatchFileKind.Ordinary;
                Assert(fixture.Classify(plan).Changes[0].Kind == expected, path);
            }
            var alias = fixture.Classify(await fixture.Prepare("*** Add File: .github./workflows/script.cs\n+text"));
            Assert(alias.Changes[0].Kind == PatchFileKind.Critical, "Reserved directory alias");
        });

        await check("deletes and moves require review including both the source and destination rules", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("source.cs", "old\n");
            fixture.Put("Directory.Build.cs", "old\n");
            fixture.Put("unknown.data", "old\n");
            var cases = new[]
            {
                ("*** Delete File: source.cs", PatchFileKind.Ordinary, PatchRiskReason.Delete),
                ("*** Update File: source.cs\n*** Move to: renamed.cs", PatchFileKind.Ordinary, PatchRiskReason.Move),
                ("*** Update File: source.cs\n*** Move to: .github/workflows/code.cs", PatchFileKind.Critical, PatchRiskReason.Move | PatchRiskReason.CriticalFile),
                ("*** Update File: Directory.Build.cs\n*** Move to: safe.cs", PatchFileKind.Critical, PatchRiskReason.Move | PatchRiskReason.CriticalFile),
                ("*** Update File: source.cs\n*** Move to: other.data", PatchFileKind.Unknown, PatchRiskReason.Move | PatchRiskReason.UnknownFileType),
                ("*** Update File: unknown.data\n*** Move to: safe.cs", PatchFileKind.Unknown, PatchRiskReason.Move | PatchRiskReason.UnknownFileType),
                ("*** Update File: Directory.Build.cs\n*** Move to: future.data", PatchFileKind.Critical, PatchRiskReason.Move | PatchRiskReason.CriticalFile | PatchRiskReason.UnknownFileType)
            };
            foreach (var (body, kind, reasons) in cases)
            {
                var risk = fixture.Classify(await fixture.Prepare(body));
                Assert(risk.Changes[0].Kind == kind && risk.Reasons == reasons && risk.RequiresApproval, body);
            }
            Assert(File.Exists(fixture.Path("source.cs")) && File.Exists(fixture.Path("Directory.Build.cs")), "No partial operation");
        });

        await check("the actual selected settings file is critical regardless of filename or move direction", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("custom.md", "old\n");
            fixture.Put("source.cs", "old\n");
            foreach (var body in new[]
            {
                "*** Update File: custom.md\n@@\n-old\n+new",
                "*** Update File: custom.md\n*** Move to: renamed.md",
                "*** Delete File: custom.md"
            })
                Assert((fixture.Classify(await fixture.Prepare(body), fixture.Path("custom.md")).Reasons & PatchRiskReason.CriticalFile) != 0, body);
            var destination = await fixture.Prepare("*** Update File: source.cs\n*** Move to: custom-next.cs");
            Assert(fixture.Classify(destination, fixture.Path("custom-next.cs")).Changes[0].Kind == PatchFileKind.Critical, "Selected target");
            var add = await fixture.Prepare("*** Add File: custom-new.md\n+new");
            Assert(fixture.Classify(add, fixture.Path("custom-new.md")).Changes[0].Kind == PatchFileKind.Critical, "Selected new path");
        });

        await check("shebang detection uses before and after snapshots including a UTF-8 BOM", async () =>
        {
            using var fixture = new Fixture();
            foreach (var text in new[] { "#!/bin/sh", "\ufeff#!/usr/bin/env python" })
            {
                var add = fixture.Classify(await fixture.Prepare($"*** Add File: script.txt\n+{text}\n+echo hello"));
                Assert(add.Changes[0].Kind == PatchFileKind.Critical && add.Reasons == PatchRiskReason.CriticalFile, "After shebang");
                fixture.Put("old.txt", text + "\n");
                var removed = fixture.Classify(await fixture.Prepare($"*** Update File: old.txt\n@@\n-{text.TrimStart('\ufeff')}\n+ordinary"));
                Assert(removed.Changes[0].Kind == PatchFileKind.Critical, "Before shebang");
            }
            fixture.Put("without.txt", "ordinary\n");
            Assert(fixture.Classify(await fixture.Prepare("*** Update File: without.txt\n@@\n-ordinary\n+#!/bin/sh")).Changes[0].Kind == PatchFileKind.Critical, "Introduced shebang");
            var doc = fixture.Classify(await fixture.Prepare("*** Add File: example.md\n+example\n+#!/bin/sh"));
            Assert(!doc.RequiresApproval, "Embedded documentation example");
        });

        await check("retained Unix executable metadata makes ordinary filenames critical without reading host permissions", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("source.cs", "old\n");
            var plan = await fixture.Prepare("*** Update File: source.cs\n@@\n-old\n+new");
            foreach (var execute in new[] { UnixFileMode.UserExecute, UnixFileMode.GroupExecute, UnixFileMode.OtherExecute })
            {
                // Exercise portable snapshot interpretation even when this check runs on Windows.
                var change = plan.Changes[0];
                var snapshot = new PatchPlan(plan.Owner, [change with { Before = change.Before! with { Mode = UnixFileMode.UserRead | execute } }]);
                Assert(fixture.Classify(snapshot).Changes[0].Kind == PatchFileKind.Critical, execute.ToString());
            }
            var nonExecutable = plan.Changes[0];
            var safe = new PatchPlan(plan.Owner, [nonExecutable with { Before = nonExecutable.Before! with { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite } }]);
            Assert(!fixture.Classify(safe).RequiresApproval, "Read/write bits alone");
        });

        await check("mixed patch classification aggregates all review reasons and never executes ordinary members", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("delete.md", "old\n");
            fixture.Put("move.cs", "old\n");
            var plan = await fixture.Prepare("*** Add File: ordinary/new.cs\n+new\n*** Add File: Directory.Build.cs\n+build\n" +
                "*** Add File: custom.data\n+data\n*** Delete File: delete.md\n*** Update File: move.cs\n*** Move to: renamed.cs");
            var before = fixture.Snapshot();
            var risk = fixture.Classify(plan);
            Assert(risk.RequiresApproval && risk.Reasons == (PatchRiskReason.CriticalFile | PatchRiskReason.UnknownFileType | PatchRiskReason.Delete | PatchRiskReason.Move), "Whole batch");
            Assert(risk.Changes.Length == 5 && risk.Changes[0].Reasons == PatchRiskReason.None && risk.Changes[3].Reasons == PatchRiskReason.Delete, "Individual reasons");
            Assert(risk.Changes.Select(change => change.Summary).SequenceEqual(plan.Changes.Select(change => change.Summary)), "Order and exact summaries");
            Assert(before.SequenceEqual(fixture.Snapshot()) && !Directory.Exists(fixture.Path("ordinary")), "No files or directories written");
        });

        await check("classification is snapshot-only, cancellable and cannot replace base path or private-file validation", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("existing.md", "ordinary\n");
            var plan = await fixture.Prepare("*** Update File: existing.md\n@@\n-ordinary\n+new");
            var expected = fixture.Classify(plan);
            fixture.Put("existing.md", "#!/bin/sh\n");
            Directory.Delete(fixture.Root, recursive: true);
            var actual = fixture.Classify(plan);
            Assert(actual.Reasons == expected.Reasons && actual.Changes.SequenceEqual(expected.Changes) && !Directory.Exists(fixture.Root), "No filesystem reads or writes");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { PatchRiskClassifier.Classify(PatchDiffBuilder.Create(plan), fixture.SettingsPath, new(), cancelled.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }

            Directory.CreateDirectory(fixture.Root);
            foreach (var path in new[] { "../outside.cs", ".git/config.cs", ".env.cs", ".codex/policy.md", "private.key" })
            {
                try { await fixture.Prepare($"*** Add File: {path}\n+new"); throw new Exception("Expected base rejection: " + path); }
                catch (WorkspaceException) { }
            }
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Patch risk: " + message); }

    private sealed class Fixture : IDisposable
    {
        private string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"minimcp-risk-{Guid.NewGuid():N}");
        public string Root { get; }
        public string SettingsPath => System.IO.Path.Combine(DirectoryPath, "settings.json");
        private ProjectWorkspace Workspace { get; }
        private PatchService Service { get; }
        public Fixture()
        {
            Root = System.IO.Path.Combine(DirectoryPath, "project");
            Directory.CreateDirectory(Root);
            Workspace = new ProjectWorkspace(Root);
            Service = new PatchService(Workspace);
        }
        public string Path(string relative) => System.IO.Path.Combine(Root, relative);
        public void Put(string path, string text) => File.WriteAllText(Path(path), text, new UTF8Encoding(false));
        public Task<PatchPlan> Prepare(string body) => Service.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch");
        public PatchRiskAssessment Classify(PatchPlan plan, string? settingsPath = null) =>
            PatchRiskClassifier.Classify(PatchDiffBuilder.Create(plan), settingsPath ?? SettingsPath, new());
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => System.IO.Path.GetRelativePath(DirectoryPath, path) + (Directory.Exists(path) ? "/" : ":" + Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() { Workspace.Mutations.Dispose(); Directory.Delete(DirectoryPath, recursive: true); }
    }
}

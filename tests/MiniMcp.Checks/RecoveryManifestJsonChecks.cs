using MiniMcp.Services.Patching;
using MiniMcp.Services.Workspace;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MiniMcp.Recovery;

internal static class RecoveryManifestJsonChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("recovery manifest JSON roundtrips Unicode, explicit absence and hashes without embedding raw backups or plans", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("示例.md", "\ufeffRAW_BACKUP_MARKER\r\n");
            var draft = RecoveryDraftBuilder.Create(Guid.NewGuid(), await fixture.Prepare(
                "*** Update File: 示例.md\n*** Move to: nested/结果.md\n@@\n-RAW_BACKUP_MARKER\n+new\n*** Add File: empty.md"));
            var bytes = RecoveryManifestJson.Serialize(draft.Manifest);
            var restored = RecoveryManifestJson.Parse(bytes);
            Assert(restored.ChangeId == draft.Manifest.ChangeId && restored.RunId == draft.Manifest.RunId && restored.ProjectRoot == fixture.Root, "Identity");
            Assert(restored.Operations.SequenceEqual(draft.Manifest.Operations) && restored.Paths.SequenceEqual(draft.Manifest.Paths), "All metadata");
            Assert(RecoveryManifestJson.Serialize(restored).SequenceEqual(bytes), "Canonical roundtrip");
            var text = Encoding.UTF8.GetString(bytes);
            Assert(bytes[^1] == '\n' && !bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) && !text.Contains('\r') && text.Contains("示例"), "Readable UTF-8 with LF and no BOM");
            Assert(!text.Contains("RAW_BACKUP_MARKER") && !text.Contains("owner") && !text.Contains("backups") && !text.Contains("plan"), "No raw content or execution objects");
            using var document = JsonDocument.Parse(bytes);
            Assert(document.RootElement.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(
                new[] { "changeId", "createdAtUtc", "operations", "paths", "projectRoot", "runId", "schemaVersion", "state" }), "Exact manifest fields");
            Assert(document.RootElement.GetProperty("paths")[0].GetProperty("after").ValueKind == JsonValueKind.Null &&
                document.RootElement.GetProperty("paths")[1].GetProperty("before").ValueKind == JsonValueKind.Null, "Absence is explicit");
        });

        await check("recovery JSON requires every manifest, operation, path and image field including nullable absence fields", async () =>
        {
            using var fixture = new Fixture();
            var manifest = await fixture.Manifest();
            foreach (var name in new[] { "changeId", "runId", "projectRoot", "createdAtUtc", "operations", "paths", "schemaVersion", "state" })
            {
                var json = Document(manifest);
                json.Remove(name);
                Reject(json);
            }
            foreach (var (location, names) in new[]
            {
                ("operation", new[] { "action", "path", "moveTo" }),
                ("path", new[] { "path", "before", "after", "backupFile" }),
                ("image", new[] { "byteLength", "sha256", "unixMode", "unixModeIsCreateRequest" })
            })
                foreach (var name in names)
                {
                    var json = Document(manifest);
                    var target = location == "operation" ? json["operations"]![0]!.AsObject() : location == "path"
                        ? json["paths"]![0]!.AsObject() : json["paths"]![0]!["before"]!.AsObject();
                    target.Remove(name);
                    Reject(json);
                }
        });

        await check("recovery JSON rejects unknown, duplicate and wrongly cased keys and invalid nested types with diagnostics", async () =>
        {
            using var fixture = new Fixture();
            var manifest = await fixture.Manifest();
            foreach (var location in new[] { "root", "operation", "path", "image" })
            {
                var json = Document(manifest);
                var target = location == "root" ? json : location == "operation" ? json["operations"]![0]!.AsObject() :
                    location == "path" ? json["paths"]![0]!.AsObject() : json["paths"]![0]!["before"]!.AsObject();
                target["approved"] = true;
                Reject(json);
            }
            var wrongCase = Document(manifest);
            wrongCase["State"] = wrongCase["state"]!.DeepClone();
            wrongCase.Remove("state");
            Reject(wrongCase);
            var text = Encoding.UTF8.GetString(RecoveryManifestJson.Serialize(manifest));
            Reject(Encoding.UTF8.GetBytes(text.Replace("\"state\": \"Prepared\"", "\"state\": \"Prepared\", \"st\\u0061te\": \"Prepared\"", StringComparison.Ordinal)));
            Reject(Encoding.UTF8.GetBytes(text.Replace("\"path\": \"sample.md\"", "\"path\": \"sample.md\", \"path\": \"sample.md\"", StringComparison.Ordinal)));
            foreach (var value in new JsonNode?[] { null, JsonValue.Create("4"), JsonValue.Create(true), JsonValue.Create(1.5) })
            {
                var json = Document(manifest);
                json["paths"]![0]!["before"]!["byteLength"] = value;
                var error = Reject(json);
                Assert(error.Path == "$.paths[0].before.byteLength" && error.LineNumber is not null && error.BytePositionInLine is not null, "Nested type diagnostic");
            }
        });

        await check("recovery states use exact individual names and reject integers, aliases and undefined values", async () =>
        {
            using var fixture = new Fixture();
            var manifest = await fixture.Manifest();
            foreach (var state in Enum.GetValues<RecoveryRecordState>())
            {
                var bytes = RecoveryManifestJson.Serialize(manifest with { State = state });
                Assert(RecoveryManifestJson.Parse(bytes).State == state && Encoding.UTF8.GetString(bytes).Contains("\"state\": \"" + state + "\""), "Named state roundtrip");
            }
            foreach (var value in new JsonNode?[] { null, JsonValue.Create(0), JsonValue.Create(999), JsonValue.Create("0"),
                JsonValue.Create("prepared"), JsonValue.Create("Unknown"), JsonValue.Create(" Prepared "), JsonValue.Create("Prepared, Committed") })
            {
                var json = Document(manifest);
                json["state"] = value;
                Assert(Reject(json).Path == "$.state", "State error path");
            }
            Assert(Reject(() => RecoveryManifestJson.Serialize(manifest with { State = (RecoveryRecordState)999 })).Path == "$.state", "Invalid draft state");
        });

        await check("recovery envelope and image primitives reject invalid IDs, timestamps, versions, sizes, hashes and modes", async () =>
        {
            using var fixture = new Fixture();
            var manifest = await fixture.Manifest();
            foreach (var (name, value) in new (string, JsonNode)[]
            {
                ("schemaVersion", JsonValue.Create(2)!), ("changeId", JsonValue.Create(Guid.Empty)!), ("runId", JsonValue.Create(Guid.Empty)!),
                ("projectRoot", JsonValue.Create("relative/project")!), ("createdAtUtc", JsonValue.Create(default(DateTimeOffset))!),
                ("createdAtUtc", JsonValue.Create(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)))!)
            })
            {
                var json = Document(manifest);
                json[name] = value;
                Assert(Reject(json).Path == "$." + name, "Envelope field: " + name);
            }
            foreach (var (name, value) in new (string, JsonNode)[]
            {
                ("byteLength", JsonValue.Create(-1)!), ("byteLength", JsonValue.Create(ProjectWorkspace.MaxFileBytes + 1)!),
                ("sha256", JsonValue.Create("ABC")!), ("sha256", JsonValue.Create(new string('g', 64))!),
                ("sha256", JsonValue.Create(new string('a', 64))!), ("unixMode", JsonValue.Create(-1)!), ("unixMode", JsonValue.Create(0x1000)!)
            })
            {
                var json = Document(manifest);
                json["paths"]![0]!["before"]![name] = value;
                Assert(Reject(json).Path == "$.paths[0].before." + name, "Image field: " + name);
            }
            var badRequest = Document(manifest);
            badRequest["paths"]![0]!["after"]!["unixMode"] = null;
            badRequest["paths"]![0]!["after"]!["unixModeIsCreateRequest"] = true;
            Assert(Reject(badRequest).Path == "$.paths[0].after.unixModeIsCreateRequest", "A request needs a mode");
            Assert(Reject(() => RecoveryManifestJson.Serialize(manifest with { ChangeId = Guid.Empty })).Path == "$.changeId", "Serialization validates the same primitives");
        });

        await check("recovery JSON bounds operation and path arrays and rejects missing collections or null elements", async () =>
        {
            using var fixture = new Fixture();
            for (var i = 0; i < 64; i++) fixture.Put($"file{i}.md", "old\n");
            var plan = await fixture.Prepare(string.Join('\n', Enumerable.Range(0, 64).Select(i => $"*** Update File: file{i}.md\n*** Move to: moved/file{i}.md")));
            var manifest = RecoveryDraftBuilder.Create(Guid.NewGuid(), plan).Manifest;
            var parsed = RecoveryManifestJson.Parse(RecoveryManifestJson.Serialize(manifest));
            Assert(parsed.Operations.Length == 64 && parsed.Paths.Length == 128, "Exact structural bounds");
            Assert(Reject(() => RecoveryManifestJson.Serialize(manifest with { Operations = manifest.Operations.Add(manifest.Operations[0]) })).Path == "$.operations", "Operation overage");
            Assert(Reject(() => RecoveryManifestJson.Serialize(manifest with { Paths = manifest.Paths.Add(manifest.Paths[0]) })).Path == "$.paths", "Path overage");
            Assert(Reject(() => RecoveryManifestJson.Serialize(manifest with { Paths = default })).Path == "$.paths", "Uninitialized collection");
            foreach (var field in new[] { "operations", "paths" })
            {
                foreach (var value in new JsonNode?[] { null, new JsonArray(), JsonValue.Create("array") })
                {
                    var json = Document(manifest);
                    json[field] = value;
                    Reject(json);
                }
                var withNull = Document(manifest);
                withNull[field]![0] = null;
                Reject(withNull);
            }
        });

        await check("recovery JSON enforces its complete UTF-8 byte boundary including escapes and rejects invalid encodings", async () =>
        {
            using var fixture = new Fixture();
            var manifest = await fixture.Manifest();
            var initial = RecoveryManifestJson.Serialize(manifest);
            // Exercise byte framing independently of the later topology/path validation layer.
            var exact = manifest with { Operations = [manifest.Operations[0] with
                { Path = manifest.Operations[0].Path + new string('a', RecoveryManifestJson.MaxManifestBytes - initial.Length) }] };
            var bytes = RecoveryManifestJson.Serialize(exact);
            Assert(bytes.Length == RecoveryManifestJson.MaxManifestBytes && RecoveryManifestJson.Parse(bytes).Operations.Length == 1, "Exact encoded boundary");
            Reject(() => RecoveryManifestJson.Serialize(exact with { Operations = [exact.Operations[0] with { Path = exact.Operations[0].Path + "a" }] }));
            foreach (var text in new[] { new string('中', 100_000), new string('\u001b', 50_000) })
                Reject(() => RecoveryManifestJson.Serialize(manifest with { Operations = [manifest.Operations[0] with { Path = text }] }));
            Reject([.. bytes, (byte)' ']);
            var invalidUtf8 = Reject([0xff]);
            Assert(invalidUtf8.InnerException is DecoderFallbackException, "Strict UTF-8 decoding");
            Reject([.. Encoding.UTF8.Preamble, .. initial]);
            foreach (var text in new[] { "", "null", "[]", "{} {}", "{/* comment */}", "{\"state\":\"Prepared\",}" })
                Reject(Encoding.UTF8.GetBytes(text));
        });

        await check("manifest encoding and parsing perform no filesystem I/O and do not validate commit readiness", async () =>
        {
            using var fixture = new Fixture();
            var manifest = await fixture.Manifest();
            var bytes = RecoveryManifestJson.Serialize(manifest);
            Directory.Delete(fixture.Root, recursive: true);
            var parsed = RecoveryManifestJson.Parse(bytes);
            Assert(parsed.ProjectRoot == fixture.Root && RecoveryManifestJson.Serialize(parsed).SequenceEqual(bytes), "Formatting works with an absent workspace");
            Assert(!Directory.Exists(fixture.Root) && !Directory.EnumerateFileSystemEntries(fixture.DirectoryPath).Any(), "No records or workspace recreated");
        });
    }

    private static JsonObject Document(RecoveryManifest manifest) => JsonNode.Parse(RecoveryManifestJson.Serialize(manifest))!.AsObject();
    private static JsonException Reject(JsonNode json) => Reject(Encoding.UTF8.GetBytes(json.ToJsonString()));
    private static JsonException Reject(byte[] bytes) => Reject(() => RecoveryManifestJson.Parse(bytes));
    private static JsonException Reject(Action action)
    {
        try { action(); } catch (JsonException error) { return error; }
        throw new Exception("Expected invalid recovery manifest rejection.");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Recovery JSON: " + message); }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-recovery-json-{Guid.NewGuid():N}");
        public string Root { get; }
        private readonly ProjectWorkspace _workspace;
        private readonly PatchService _service;
        public Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(DirectoryPath, "project")).FullName;
            Put("sample.md", "old\n");
            _workspace = new ProjectWorkspace(Root);
            _service = new PatchService(_workspace);
        }
        public void Put(string path, string text) => File.WriteAllText(Path.Combine(Root, path), text, new UTF8Encoding(false));
        public Task<PatchPlan> Prepare(string body) => _service.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch");
        public async Task<RecoveryManifest> Manifest() => RecoveryDraftBuilder.Create(Guid.NewGuid(),
            await Prepare("*** Update File: sample.md\n@@\n-old\n+new")).Manifest;
        public void Dispose() { _workspace.Mutations.Dispose(); Directory.Delete(DirectoryPath, recursive: true); }
    }
}

using System.Text.Json;
using MiniMcp.Configuration;

internal static class ConfigurationJsonChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        void Assert(bool condition)
        {
            if (!condition) throw new Exception("Configuration JSON assertion failed.");
        }
        JsonException Reject(Action action)
        {
            try { action(); }
            catch (JsonException exception) { return exception; }
            throw new Exception("Invalid configuration JSON was accepted.");
        }

        await check("configuration examples parse through the production JSON reader", async () =>
        {
            var examples = Path.Combine(AppContext.BaseDirectory, "examples");
            var settings = ConfigurationJson.ParseSettings(await File.ReadAllTextAsync(Path.Combine(examples, "settings.local.json")));
            var policy = ConfigurationJson.ParsePolicy(await File.ReadAllTextAsync(Path.Combine(examples, "policy.readonly.json")));
            Assert(settings.ProjectRoot == "./project" && settings.Authentication.Mode == AuthenticationMode.Local);
            Assert(!settings.Tunnel.Enabled && policy.Mode == ExecutionMode.ReadOnly);
        });

        await check("omitted JSON fields retain restrictive model defaults", () =>
        {
            var settings = ConfigurationJson.ParseSettings("{}");
            var policy = ConfigurationJson.ParsePolicy("{}");
            Assert(settings.Authentication.Mode == AuthenticationMode.CloudflareAccess && !settings.Tunnel.Enabled);
            Assert(settings.Port == 8000 && policy.Mode == ExecutionMode.ReadOnly);
            Assert(settings.SchemaVersion == 1 && policy.SchemaVersion == 1);
            return Task.CompletedTask;
        });

        await check("JSON rejects unknown fields, wrong key casing and policy fields inside settings", () =>
        {
            foreach (var json in new[]
            {
                """{"mode":"FullAccess"}""", """{"policy":{"mode":"FullAccess"}}""",
                """{"Port":8000}""", """{"recoveryDirectory":"./backup"}""",
                """{"authentication":{"unknown":true}}""", """{"tunnel":{"mode":"FullAccess"}}"""
            }) Reject(() => ConfigurationJson.ParseSettings(json));
            Reject(() => ConfigurationJson.ParsePolicy("""{"projectRoot":"./project"}"""));
            Reject(() => ConfigurationJson.ParsePolicy("""{"Mode":"FullAccess"}"""));
            return Task.CompletedTask;
        });

        await check("JSON rejects duplicate keys at every level including escaped names", () =>
        {
            foreach (var json in new[]
            {
                """{"port":8000,"port":9000}""",
                """{"port":8000,"po\u0072t":9000}""",
                """{"authentication":{"mode":"Local","mode":"CloudflareAccess"}}""",
                """{"tunnel":{"enabled":false,"enabled":true}}"""
            }) Reject(() => ConfigurationJson.ParseSettings(json));
            Reject(() => ConfigurationJson.ParsePolicy("""{"mode":"ReadOnly","mode":"FullAccess"}"""));
            return Task.CompletedTask;
        });

        await check("JSON rejects wrong types and null for nonnullable configuration fields", () =>
        {
            foreach (var json in new[]
            {
                "null", "[]", "42", """{"port":"8000"}""", """{"port":8000.5}""",
                """{"projectRoot":null}""", """{"bashPath":null}""", """{"authentication":null}""",
                """{"tunnel":null}""", """{"tunnel":{"enabled":"false"}}""",
                """{"tunnel":{"executablePath":null}}""", """{"tunnel":{"tokenFilePath":null}}"""
            }) Reject(() => ConfigurationJson.ParseSettings(json));
            Reject(() => ConfigurationJson.ParsePolicy("null"));
            Reject(() => ConfigurationJson.ParsePolicy("""{"recoveryDirectory":null}"""));
            Assert(ConfigurationJson.ParseSettings("""{"authentication":{"team":null,"audience":null,"publicOrigin":null}}""").Authentication.Team is null);
            return Task.CompletedTask;
        });

        await check("JSON execution and authentication modes accept exact individual names only", () =>
        {
            foreach (var mode in Enum.GetValues<ExecutionMode>())
                Assert(ConfigurationJson.ParsePolicy("{\"mode\":\"" + mode + "\"}").Mode == mode);
            foreach (var mode in Enum.GetValues<AuthenticationMode>())
                Assert(ConfigurationJson.ParseSettings("{\"authentication\":{\"mode\":\"" + mode + "\"}}").Authentication.Mode == mode);
            foreach (var value in new[] { "2", "999", "null", "\"2\"", "\"fullaccess\"", "\"Unknown\"", "\" FullAccess \"", "\"ReadOnly, FullAccess\"" })
                Reject(() => ConfigurationJson.ParsePolicy("{\"mode\":" + value + "}"));
            foreach (var value in new[] { "1", "\"1\"", "\"local\"", "\"Unknown\"", "\"CloudflareAccess, Local\"" })
                Reject(() => ConfigurationJson.ParseSettings("{\"authentication\":{\"mode\":" + value + "}}"));
            return Task.CompletedTask;
        });

        await check("JSON requires supported versions and standard JSON syntax", () =>
        {
            foreach (var json in new[] { """{"schemaVersion":2}""", """{"schemaVersion":0}""", """{"schemaVersion":"1"}""" })
            {
                Reject(() => ConfigurationJson.ParseSettings(json));
                Reject(() => ConfigurationJson.ParsePolicy(json));
            }
            foreach (var json in new[] { "", "{", "{} {}", "{ /* comment */ }", "{\"port\":8000,}" })
                Reject(() => ConfigurationJson.ParseSettings(json));
            return Task.CompletedTask;
        });

        await check("JSON errors preserve field paths and available line and byte positions", () =>
        {
            var wrongType = Reject(() => ConfigurationJson.ParseSettings("{\n  \"port\": \"8000\"\n}"));
            Assert(wrongType.Path == "$.port" && wrongType.LineNumber == 1 && wrongType.BytePositionInLine > 0);
            var syntax = Reject(() => ConfigurationJson.ParseSettings("{\n  \"port\":\n}"));
            Assert(syntax.LineNumber == 2 && syntax.BytePositionInLine is not null);
            var mode = Reject(() => ConfigurationJson.ParsePolicy("""{"mode":2}"""));
            Assert(mode.Path == "$.mode");
            var version = Reject(() => ConfigurationJson.ParsePolicy("""{"schemaVersion":99}"""));
            Assert(version.Path == "$.schemaVersion");
            return Task.CompletedTask;
        });
    }
}

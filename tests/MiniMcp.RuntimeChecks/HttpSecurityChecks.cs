using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using MiniMcp.Configuration;
using MiniMcp.Runtime;

internal static class HttpSecurityChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("explicit Local mode allows anonymous probes without accessing Cloudflare signing keys", async () =>
        {
            await using var fixture = new HttpSecurityFixture(AuthenticationMode.Local);
            await fixture.Runtime.StartAsync(fixture.Configuration);
            using var request = fixture.Request();
            request.Headers.TryAddWithoutValidation("Cf-Access-Jwt-Assertion", "invalid");
            using var response = await fixture.Client.SendAsync(request);
            Assert(response.StatusCode == HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert(!body.RootElement.GetProperty("authenticated").GetBoolean() && fixture.Documents.Calls == 0);
        });

        await check("Cloudflare requires one assertion header and ignores Bearer tokens and cookies", async () =>
        {
            await using var fixture = new HttpSecurityFixture();
            await fixture.Runtime.StartAsync(fixture.Configuration);
            var valid = fixture.Token();
            using (var health = await fixture.Client.GetAsync(new Uri(fixture.Runtime.Address!, "healthz")))
                Assert(health.StatusCode == HttpStatusCode.OK);
            using (var request = fixture.Request()) await Unauthorized(fixture, request);
            using (var request = fixture.Request())
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", valid);
                await Unauthorized(fixture, request);
            }
            using (var request = fixture.Request())
            {
                request.Headers.TryAddWithoutValidation("Cookie", "CF_Authorization=" + valid);
                await Unauthorized(fixture, request);
            }
            using (var request = fixture.Request(token: " "))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", valid);
                await Unauthorized(fixture, request);
            }
            Assert(await fixture.RawStatusAsync($"GET /protected HTTP/1.1\r\nHost: localhost\r\nCf-Access-Jwt-Assertion: {valid}\r\nCf-Access-Jwt-Assertion: {valid}\r\nConnection: close\r\n\r\n") == 401);
            Assert(fixture.Documents.Calls == 0);
        });

        await check("valid Access JWTs authenticate but cannot grant writes or Bash beyond ReadOnly", async () =>
        {
            await using var fixture = new HttpSecurityFixture();
            await fixture.Runtime.StartAsync(fixture.Configuration);
            var token = fixture.Token(); // Includes signed mode=FullAccess and approved=true claims.
            using (var request = fixture.Request(token: token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "opaque-client-token");
                using var response = await fixture.Client.SendAsync(request);
                Assert(response.StatusCode == HttpStatusCode.OK);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert(body.RootElement.GetProperty("authenticated").GetBoolean());
                Assert(body.RootElement.GetProperty("name").GetString() == "tester@example.com");
            }
            using (var request = fixture.Request("read", token))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert(response.StatusCode == HttpStatusCode.OK);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert(body.RootElement.GetProperty("content").GetString() == "read-only fixture\n");
            }
            foreach (var path in new[] { "patch", "bash" })
            {
                using var request = fixture.Request(path, token, method: HttpMethod.Post);
                using var response = await fixture.Client.SendAsync(request);
                Assert(response.StatusCode == HttpStatusCode.Forbidden && (await response.Content.ReadAsStringAsync()).Contains("ReadOnly"));
            }
            Assert(Directory.EnumerateFileSystemEntries(fixture.Root).Count() == 1);
            Assert(fixture.Runtime.Snapshot.State == RuntimeState.Running && fixture.Runtime.Snapshot.Error is null);
            Assert(fixture.Runtime.Execution!.ActiveCalls == 0 && fixture.Runtime.Execution.Configuration.Policy.Mode == ExecutionMode.ReadOnly);
            Assert(fixture.Documents.Calls == 1);
        });

        await check("Access JWT validation rejects signature, issuer, audience, lifetime and algorithm failures", async () =>
        {
            await using var fixture = new HttpSecurityFixture();
            await fixture.Runtime.StartAsync(fixture.Configuration);
            using var other = RSA.Create(2048);
            string[] invalid =
            [
                fixture.Token(signingKey: other), fixture.Token(issuer: "https://other.cloudflareaccess.com"),
                fixture.Token(audience: "other-application"), fixture.Token(expires: DateTime.UtcNow.AddMinutes(-2)),
                fixture.Token(notBefore: DateTime.UtcNow.AddMinutes(2)), fixture.Token(noExpiration: true),
                fixture.Token(unsigned: true), fixture.Token(algorithm: SecurityAlgorithms.RsaSha384), "not.a.jwt"
            ];
            foreach (var token in invalid)
            {
                using var request = fixture.Request(token: token);
                await Unauthorized(fixture, request);
            }
            using var validRequest = fixture.Request(token: fixture.Token());
            using var validResponse = await fixture.Client.SendAsync(validRequest);
            Assert(validResponse.StatusCode == HttpStatusCode.OK);
        });

        await check("signing keys are cached and unknown-key refresh requests remain bounded", async () =>
        {
            await using var fixture = new HttpSecurityFixture();
            await fixture.Runtime.StartAsync(fixture.Configuration);
            var valid = fixture.Token();
            for (var i = 0; i < 4; i++)
            {
                using var request = fixture.Request(token: valid);
                using var response = await fixture.Client.SendAsync(request);
                Assert(response.StatusCode == HttpStatusCode.OK);
            }
            Assert(fixture.Documents.Calls == 1);
            using var other = RSA.Create(2048);
            for (var i = 0; i < 12; i++)
            {
                using var request = fixture.Request(token: fixture.Token(signingKey: other, keyId: "unknown-" + i));
                await Unauthorized(fixture, request);
            }
            using var final = fixture.Request(token: valid);
            using var result = await fixture.Client.SendAsync(final);
            Assert(result.StatusCode == HttpStatusCode.OK && fixture.Documents.Calls <= 2);
        });

        await check("host whitelist ignores ambient configuration and untrusted forwarded hosts", async () =>
        {
            var previousHosts = Environment.GetEnvironmentVariable("AllowedHosts");
            var previousForwarding = Environment.GetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED");
            try
            {
                Environment.SetEnvironmentVariable("AllowedHosts", "*");
                Environment.SetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true");
                foreach (var mode in Enum.GetValues<AuthenticationMode>())
                {
                    await using var fixture = new HttpSecurityFixture(mode);
                    await fixture.Runtime.StartAsync(fixture.Configuration);
                    foreach (var host in new[] { "localhost", "127.0.0.1:8000", "[::1]" })
                    {
                        using var request = fixture.Request("healthz", host: host);
                        using var response = await fixture.Client.SendAsync(request);
                        Assert(response.StatusCode == HttpStatusCode.OK);
                    }
                    using (var request = fixture.Request("healthz", host: "MCP.EXAMPLE.COM:443"))
                    using (var response = await fixture.Client.SendAsync(request))
                        Assert(response.StatusCode == (mode == AuthenticationMode.CloudflareAccess ? HttpStatusCode.OK : HttpStatusCode.BadRequest));
                    foreach (var host in new[] { "attacker.test", "mcp.example.com.attacker.test", "sub.mcp.example.com" })
                    {
                        using var request = fixture.Request("healthz", host: host);
                        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "mcp.example.com");
                        using var response = await fixture.Client.SendAsync(request);
                        Assert(response.StatusCode == HttpStatusCode.BadRequest);
                    }
                    using (var request = fixture.Request(token: mode == AuthenticationMode.CloudflareAccess ? fixture.Token() : null, host: "localhost"))
                    {
                        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "attacker.test");
                        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
                        using var response = await fixture.Client.SendAsync(request);
                        Assert(response.StatusCode == HttpStatusCode.OK);
                        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        Assert(body.RootElement.GetProperty("host").GetString() == "localhost" && body.RootElement.GetProperty("scheme").GetString() == "http");
                    }
                    Assert(await fixture.RawStatusAsync("GET /healthz HTTP/1.0\r\nConnection: close\r\n\r\n") == 400);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("AllowedHosts", previousHosts);
                Environment.SetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED", previousForwarding);
            }
        });

        await check("two-MiB request limit covers declared and chunked bodies in both authentication modes", async () =>
        {
            const int limit = 2 * 1024 * 1024;
            foreach (var mode in Enum.GetValues<AuthenticationMode>())
            {
                await using var fixture = new HttpSecurityFixture(mode);
                await fixture.Runtime.StartAsync(fixture.Configuration);
                var token = mode == AuthenticationMode.CloudflareAccess ? fixture.Token() : null;
                foreach (var chunked in new[] { false, true })
                foreach (var size in new[] { limit - 1024, limit, limit + 1 })
                {
                    using var request = fixture.Request("body", token, method: HttpMethod.Post);
                    request.Version = HttpVersion.Version11;
                    request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                    request.Content = chunked ? new UnknownLengthContent(new byte[size]) : new ByteArrayContent(new byte[size]);
                    using var response = await fixture.Client.SendAsync(request);
                    // Kestrel includes chunk prefixes/suffixes in the limit, unlike Content-Length payloads.
                    var fits = size < limit || (!chunked && size == limit);
                    var expected = fits ? HttpStatusCode.NoContent : HttpStatusCode.RequestEntityTooLarge;
                    if (response.StatusCode != expected)
                        throw new Exception($"{mode}, chunked={chunked}, bytes={size}: expected {(int)expected}, got {(int)response.StatusCode}; {await response.Content.ReadAsStringAsync()}");
                }
            }
        });

        await check("signing-key failure denies protected access without changing mode or exposing test endpoints", async () =>
        {
            await using var fixture = new HttpSecurityFixture();
            fixture.Documents.Fail = true;
            await fixture.Runtime.StartAsync(fixture.Configuration);
            using (var request = fixture.Request(token: fixture.Token()))
            using (var response = await fixture.Client.SendAsync(request))
                Assert(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.InternalServerError);
            Assert(fixture.Runtime.Snapshot.Configuration!.Settings.Authentication.Mode == AuthenticationMode.CloudflareAccess);
            await using var production = new McpRuntime(0);
            await production.StartAsync(fixture.Configuration);
            foreach (var path in new[] { "protected", "read", "patch", "bash", "body" })
            {
                using var response = await fixture.Client.GetAsync(new Uri(production.Address!, path));
                Assert(response.StatusCode == HttpStatusCode.NotFound);
            }
            using var health = await fixture.Client.GetAsync(new Uri(production.Address!, "healthz"));
            Assert(health.StatusCode == HttpStatusCode.OK);
            using var mcpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(production.Address!, "mcp"))
            { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            mcpRequest.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            using var mcp = await fixture.Client.SendAsync(mcpRequest);
            if (mcp.StatusCode != HttpStatusCode.Unauthorized) throw new Exception($"Anonymous MCP request returned {(int)mcp.StatusCode}, expected 401.");
        });
    }

    private static async Task Unauthorized(HttpSecurityFixture fixture, HttpRequestMessage request)
    {
        using var response = await fixture.Client.SendAsync(request);
        Assert(response.StatusCode == HttpStatusCode.Unauthorized);
        Assert(response.Headers.WwwAuthenticate.Any(value => value.Scheme == "Bearer" && string.IsNullOrEmpty(value.Parameter)));
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new Exception("HTTP authentication or request boundary assertion failed.");
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}

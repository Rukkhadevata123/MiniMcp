using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using MiniMcp.Configuration;
using MiniMcp.Runtime;
using MiniMcp.Services;
using MiniMcp.Tools;
using ModelContextProtocol;

internal sealed class HttpSecurityFixture : IAsyncDisposable
{
    public const string Issuer = "https://fixture-team.cloudflareaccess.com";
    public const string Audience = "fixture-application-aud";
    private const string KeyId = "fixture-key";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"minimcp-http-security-{Guid.NewGuid():N}");
    private readonly RSA _rsa = RSA.Create(2048);
    public HttpSecurityFixture(AuthenticationMode mode = AuthenticationMode.CloudflareAccess,
        ExecutionMode executionMode = ExecutionMode.ReadOnly, bool mapProbes = true,
        PatchApprovalLimits? patchLimits = null, string? settingsRelativePath = null)
    {
        Root = Path.Combine(_directory, "project");
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "sample.txt"), "read-only fixture\n");
        var paths = new ConfigurationPaths(Path.Combine(_directory, "user"), Path.Combine(_directory, "state"));
        var bash = executionMode == ExecutionMode.ReadOnly ? "" : ExternalProgramLocator.FindBash(
            Environment.GetEnvironmentVariable("MINIMCP_TEST_BASH_PATH") ?? "", Path.Combine(Environment.CurrentDirectory, "settings.json"))
            ?? throw new Exception("Bash is required for HTTP editing-mode checks.");
        Configuration = new ConfigurationValidator(paths).Validate(new AppSettings
        {
            ProjectRoot = Root,
            BashPath = bash,
            Authentication = new AuthenticationSettings
            {
                Mode = mode, PublicOrigin = "https://mcp.example.com", Team = "fixture-team", Audience = Audience
            }
        }, settingsRelativePath is null ? Path.Combine(_directory, "settings.json") : Path.Combine(Root, settingsRelativePath),
            new ExecutionPolicy { Mode = executionMode, PatchApprovalLimits = patchLimits ?? new() });
        var key = _rsa.ExportParameters(false);
        Documents = new SigningDocuments(JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "RSA", kid = KeyId, alg = "RS256", use = "sig",
                n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) } }
        }));
        Runtime = new McpRuntime(0, services =>
        {
            if (mode == AuthenticationMode.CloudflareAccess)
                services.Replace(ServiceDescriptor.Singleton<IDocumentRetriever>(Documents));
        }, mapProbes ? routes => MapProbes(routes, mode) : null);
    }

    public string Root { get; }
    public RuntimeConfiguration Configuration { get; }
    public SigningDocuments Documents { get; }
    public McpRuntime Runtime { get; }
    public HttpClient Client { get; } = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(5) };

    public string Token(string? issuer = null, string? audience = null, DateTime? expires = null,
        DateTime? notBefore = null, bool noExpiration = false, bool unsigned = false,
        string algorithm = SecurityAlgorithms.RsaSha256, RSA? signingKey = null, string? keyId = null)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer, Audience = audience ?? Audience,
            NotBefore = notBefore ?? now.AddMinutes(-10), Expires = noExpiration ? null : expires ?? now.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["email"] = "tester@example.com", ["mode"] = "FullAccess", ["approved"] = true },
            SigningCredentials = unsigned ? null : new SigningCredentials(
                new RsaSecurityKey(signingKey ?? _rsa) { KeyId = keyId ?? KeyId }, algorithm)
        });
    }

    public HttpRequestMessage Request(string path = "protected", string? token = null, string? host = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, new Uri(Runtime.Address!, path));
        if (token is not null) request.Headers.TryAddWithoutValidation("Cf-Access-Jwt-Assertion", token);
        if (host is not null) request.Headers.Host = host;
        return request;
    }

    public async Task<int> RawStatusAsync(string request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Runtime.Address!.Port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var status = await reader.ReadLineAsync(timeout.Token) ?? throw new Exception("Missing HTTP response status.");
        return int.Parse(status.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void MapProbes(IEndpointRouteBuilder routes, AuthenticationMode mode)
    {
        var identity = routes.MapGet("/protected", (HttpContext context) => Results.Json(new
        {
            authenticated = context.User.Identity?.IsAuthenticated == true,
            name = context.User.Identity?.Name, host = context.Request.Host.Value, scheme = context.Request.Scheme
        }));
        var read = routes.MapGet("/read", async (ToolExecutionService execution, CancellationToken token) =>
            Results.Json(await execution.ReadFileAsync("sample.txt", cancellationToken: token)));
        var patch = routes.MapPost("/patch", async (ToolExecutionService execution, CancellationToken token) =>
        {
            try
            {
                await new WorkspaceTools(execution).ApplyPatch("*** Begin Patch\n*** Add File: forbidden.txt\n+unexpected\n*** End Patch", token);
                return Results.NoContent();
            }
            catch (McpException error) { return Results.Json(new { error = error.Message }, statusCode: 403); }
        });
        var bash = routes.MapPost("/bash", async (ToolExecutionService execution, CancellationToken token) =>
        {
            try { await new WorkspaceTools(execution).Bash("printf unexpected > forbidden.txt", token); return Results.NoContent(); }
            catch (McpException error) { return Results.Json(new { error = error.Message }, statusCode: 403); }
        });
        var body = routes.MapPost("/body", async (HttpContext context) =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            return Results.NoContent();
        });
        if (mode == AuthenticationMode.CloudflareAccess)
        {
            identity.RequireAuthorization(); read.RequireAuthorization(); patch.RequireAuthorization();
            bash.RequireAuthorization(); body.RequireAuthorization();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Runtime.DisposeAsync();
        Client.Dispose();
        _rsa.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    internal sealed class SigningDocuments(string jwks) : IDocumentRetriever
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool Fail { get; set; }
        public Task<string> GetDocumentAsync(string address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            if (address != Issuer + "/cdn-cgi/access/certs") throw new Exception("Unexpected signing-key address.");
            return Fail ? Task.FromException<string>(new IOException("Fixture signing keys unavailable.")) : Task.FromResult(jwks);
        }
    }
}

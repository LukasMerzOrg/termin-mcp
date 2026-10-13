using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Termin.Storage;

namespace Termin.Unit;

/// <summary>
/// Contract tests against the real ASP.NET pipeline: OAuth 2.1 (DCR, PKCE,
/// refresh rotation, revocation) and the MCP endpoint (auth, tools/list).
/// </summary>
public sealed class ContractTests : IClassFixture<ContractTests.Server>
{
    public const string Password = "test-admin-password";
    public const string Redirect = "https://claude.ai/api/mcp/auth_callback";

    public sealed class Server : WebApplicationFactory<Program>
    {
        public readonly string DataDir = Path.Combine(Path.GetTempPath(), "termin-contract-" + Guid.NewGuid().ToString("N"));

        public Server()
        {
            Environment.SetEnvironmentVariable("TERMIN_PUBLIC_URL", "https://termin.test");
            Environment.SetEnvironmentVariable("TERMIN_SECRET_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            Environment.SetEnvironmentVariable("TERMIN_ADMIN_PASSWORD", Password);
            Environment.SetEnvironmentVariable("TERMIN_DATA_DIR", DataDir);
            Environment.SetEnvironmentVariable("TERMIN_DEFAULT_TZ", "Europe/Zurich");
            Environment.SetEnvironmentVariable("TERMIN_STATIC_TOKEN", "static-test-token");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(DataDir, true); } catch { }
        }
    }

    private readonly Server _server;

    public ContractTests(Server server) => _server = server;

    private HttpClient Client() => _server.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://termin.test"),
        HandleCookies = true,
    });

    [Fact]
    public async Task Mcp_without_token_challenges_with_resource_metadata()
    {
        var http = Client();
        var res = await http.PostAsync("/mcp", JsonRpc("initialize", Init()));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var challenge = res.Headers.WwwAuthenticate.ToString();
        Assert.Contains("resource_metadata=\"https://termin.test/.well-known/oauth-protected-resource\"", challenge);

        var prm = await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");
        Assert.Equal("https://termin.test/mcp", prm.GetProperty("resource").GetString());
        var asm = await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        Assert.Equal("S256", asm.GetProperty("code_challenge_methods_supported")[0].GetString());
        Assert.Equal("https://termin.test/register", asm.GetProperty("registration_endpoint").GetString());
    }

    [Fact]
    public async Task Foreign_origin_is_rejected()
    {
        var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonRpc("initialize", Init()) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "static-test-token");
        req.Headers.Add("Origin", "https://evil.example");
        var res = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Registration_enforces_redirect_allowlist()
    {
        var http = Client();
        var bad = await http.PostAsJsonAsync("/register", new { redirect_uris = new[] { "https://evil.example/cb" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var good = await http.PostAsJsonAsync("/register", new { redirect_uris = new[] { Redirect, "http://localhost:33418/callback" }, client_name = "Claude" });
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
    }

    [Fact]
    public async Task Full_oauth_flow_then_mcp_tools()
    {
        var http = Client();
        var clientId = await RegisterAsync(http);
        var verifier = Tokens.Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Tokens.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        // Wrong password is refused.
        var (page, form) = await AuthorizePageAsync(http, clientId, challenge, "calendar.read calendar.write");
        Assert.Contains("Connect to Termin", page);
        form["password"] = "nope";
        var denied = await http.PostAsync("/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        // Right password redirects back with a code, state and iss.
        (_, form) = await AuthorizePageAsync(http, clientId, challenge, "calendar.read calendar.write");
        form["password"] = Password;
        form["allow_write"] = "on";
        var ok = await http.PostAsync("/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var location = ok.Headers.Location!.ToString();
        Assert.StartsWith(Redirect, location);
        Assert.Contains("state=xyz", location);
        Assert.Contains("iss=https", location);
        var code = Regex.Match(location, "code=([^&]+)").Groups[1].Value;

        // PKCE mismatch fails and burns nothing else; the right verifier works once.
        var badPkce = await TokenAsync(http, new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = Uri.UnescapeDataString(code), ["code_verifier"] = new string('a', 50), ["redirect_uri"] = Redirect });
        Assert.Equal(HttpStatusCode.BadRequest, badPkce.StatusCode);

        // A failed PKCE attempt consumed the code (one-time use), so authorize again.
        (_, form) = await AuthorizePageAsync(http, clientId, challenge, "calendar.read calendar.write");
        form["password"] = Password;
        form["allow_write"] = "on";
        ok = await http.PostAsync("/authorize", new FormUrlEncodedContent(form));
        code = Uri.UnescapeDataString(Regex.Match(ok.Headers.Location!.ToString(), "code=([^&]+)").Groups[1].Value);

        var tokenRes = await TokenAsync(http, new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = Redirect, ["resource"] = "https://termin.test/mcp" });
        Assert.Equal(HttpStatusCode.OK, tokenRes.StatusCode);
        var tokens = await tokenRes.Content.ReadFromJsonAsync<JsonElement>();
        var access = tokens.GetProperty("access_token").GetString()!;
        var refresh = tokens.GetProperty("refresh_token").GetString()!;
        Assert.Equal(3600, tokens.GetProperty("expires_in").GetInt32());
        Assert.Equal("calendar.read calendar.write", tokens.GetProperty("scope").GetString());

        var replay = await TokenAsync(http, new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["code_verifier"] = verifier });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        // MCP with the access token: 10 tools, descriptions carry today's date.
        var tools = await ListToolsAsync(http, access);
        Assert.Equal(10, tools.Count);
        Assert.Contains("create_event", tools.Keys);
        Assert.Contains("Today is", tools["get_events"]);
        Assert.Contains("Europe/Zurich", tools["get_events"]);

        // Refresh rotates; the old refresh token is dead and its reuse revokes the family.
        var r1 = await TokenAsync(http, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refresh });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var rotated = await r1.Content.ReadFromJsonAsync<JsonElement>();
        var access2 = rotated.GetProperty("access_token").GetString()!;
        Assert.NotEqual(refresh, rotated.GetProperty("refresh_token").GetString());

        var reuse = await TokenAsync(http, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refresh });
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        var afterReuse = await McpAsync(http, access2, "tools/list", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }

    [Fact]
    public async Task Read_only_grant_hides_write_tools_and_revocation_works()
    {
        var http = Client();
        var clientId = await RegisterAsync(http);
        var verifier = Tokens.Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Tokens.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var (_, form) = await AuthorizePageAsync(http, clientId, challenge, "calendar.read calendar.write");
        form["password"] = Password; // allow_write not checked
        var ok = await http.PostAsync("/authorize", new FormUrlEncodedContent(form));
        var code = Uri.UnescapeDataString(Regex.Match(ok.Headers.Location!.ToString(), "code=([^&]+)").Groups[1].Value);
        var tokenRes = await TokenAsync(http, new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["code_verifier"] = verifier });
        var tokens = await tokenRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("calendar.read", tokens.GetProperty("scope").GetString());
        var access = tokens.GetProperty("access_token").GetString()!;

        var tools = await ListToolsAsync(http, access);
        Assert.DoesNotContain("create_event", tools.Keys);
        Assert.DoesNotContain("delete_event", tools.Keys);
        Assert.Contains("get_events", tools.Keys);

        // A write call is refused with a structured error.
        var call = await McpAsync(http, access, "tools/call", new { name = "create_event", arguments = new { calendar = "x", summary = "y", start = "2026-10-14T09:00:00+02:00" } });
        var result = await ReadRpcAsync(call);
        Assert.True(result.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("\"error\":\"forbidden\"", result.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        await http.PostAsync("/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = access }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(http, access, "tools/list", new { })).StatusCode);
    }

    [Fact]
    public async Task Tool_errors_are_structured()
    {
        var http = Client();
        var call = await McpAsync(http, "static-test-token", "tools/call", new { name = "get_events", arguments = new { start = "2026-10-14T09:00:00", end = "2026-10-15T09:00:00+02:00" } });
        var result = await ReadRpcAsync(call);
        var text = result.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var error = JsonDocument.Parse(text).RootElement;
        // No account configured is checked first; either way the shape is { error, message }.
        Assert.True(error.TryGetProperty("error", out var code));
        Assert.Contains(code.GetString(), new[] { "not_found", "invalid_time" });
        Assert.True(error.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task Health_endpoints_respond()
    {
        var http = Client();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/healthz")).StatusCode);
        var ready = await http.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode); // no accounts: DB only
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<string> RegisterAsync(HttpClient http)
    {
        var res = await http.PostAsJsonAsync("/register", new { redirect_uris = new[] { Redirect }, client_name = "Claude", token_endpoint_auth_method = "none" });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    private static async Task<(string Page, Dictionary<string, string> Form)> AuthorizePageAsync(HttpClient http, string clientId, string challenge, string scope)
    {
        var url = $"/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&code_challenge={challenge}&code_challenge_method=S256&scope={Uri.EscapeDataString(scope)}&state=xyz&resource={Uri.EscapeDataString("https://termin.test/mcp")}";
        var res = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var page = await res.Content.ReadAsStringAsync();
        var form = Regex.Matches(page, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")
            .ToDictionary(m => m.Groups[1].Value, m => System.Net.WebUtility.HtmlDecode(m.Groups[2].Value));
        form["action"] = "allow";
        return (page, form);
    }

    private static Task<HttpResponseMessage> TokenAsync(HttpClient http, Dictionary<string, string> form) =>
        http.PostAsync("/token", new FormUrlEncodedContent(form));

    private static object Init() => new
    {
        protocolVersion = "2025-06-18",
        capabilities = new { },
        clientInfo = new { name = "contract-test", version = "1.0" },
    };

    private static StringContent JsonRpc(string method, object @params) =>
        new(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params }), Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> McpAsync(HttpClient http, string token, string method, object @params)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonRpc(method, @params) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        req.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        return await http.SendAsync(req);
    }

    private static async Task<JsonElement> ReadRpcAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        var json = body.TrimStart().StartsWith('{')
            ? body
            : string.Join("", body.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()));
        return JsonDocument.Parse(json).RootElement;
    }

    private static async Task<Dictionary<string, string>> ListToolsAsync(HttpClient http, string token)
    {
        var rpc = await ReadRpcAsync(await McpAsync(http, token, "tools/list", new { }));
        return rpc.GetProperty("result").GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("description").GetString()!);
    }
}

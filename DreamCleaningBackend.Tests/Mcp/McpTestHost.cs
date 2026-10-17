using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.AdminAgent;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Services.Mcp;
using DreamCleaningBackend.Services.Reporting;
using DreamCleaningBackend.Tests.Billing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace DreamCleaningBackend.Tests.Mcp;

/// <summary>
/// A real Kestrel host running the Claude connector exactly as Program.cs wires it — the
/// McpBearer scheme, the McpConnector policy, McpServerSetup's MCP server, the OAuth controller —
/// over a throwaway MariaDB database (the OAuth service claims codes and refresh tokens with
/// conditional bulk UPDATEs, which the in-memory provider cannot run). The site's own sign-in is
/// a JWT in the Authorization header, as the dev SPA sends it.
/// </summary>
public sealed class McpTestHost : IAsyncDisposable
{
    public const string BaseUrl = "https://dc.test";
    public const string Redirect = "https://claude.ai/api/mcp/auth_callback";
    private const string SigningKey = "mcp-connector-tests-signing-key-0123456789-abcdefghijklmnopqrstuvwxyz";

    private readonly WebApplication _app;
    public HttpClient Client { get; }
    public IServiceProvider Services => _app.Services;

    private McpTestHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public static async Task<McpTestHost> StartAsync(MariaDbDatabase db)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppSettings:Token"] = SigningKey,
            ["Frontend:Url"] = BaseUrl,
            ["FinancialRates:Enabled"] = "false"
        });

        builder.Services.AddControllers()
            .ConfigureApplicationPartManager(apm =>
            {
                apm.ApplicationParts.Clear();
                apm.ApplicationParts.Add(new AssemblyPart(typeof(ApplicationDbContext).Assembly));
                foreach (var p in apm.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                    apm.FeatureProviders.Remove(p);
                apm.FeatureProviders.Add(new OnlyTheseControllers(new[]
                {
                    typeof(McpOAuthController), typeof(AdminMcpSessionsController), typeof(ExpensesController),
                    typeof(AdminCatalogController), typeof(AdminAuditController), typeof(AdminPollsController)
                }));
            });

        builder.Services.AddDbContext<ApplicationDbContext>(o =>
            o.UseMySql(db.ConnectionString, new MariaDbServerVersion(new Version(10, 9, 8))));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IPermissionService, PermissionService>();
        builder.Services.AddScoped<IPageAccessService, PageAccessService>();
        builder.Services.AddScoped<IAuditService, AuditService>();
        builder.Services.AddScoped<IAdminBonusService, AdminBonusService>();
        builder.Services.AddScoped<IStripeFeeEstimator, StripeFeeEstimator>();
        builder.Services.AddScoped<IFinancialRateService, FinancialRateService>();
        builder.Services.AddScoped<IExpenseService, ExpenseService>();
        builder.Services.AddScoped<ICleanerPayrollEditService, CleanerPayrollEditService>();
        builder.Services.AddScoped<IOutgoingPaymentService, OutgoingPaymentService>();
        builder.Services.AddScoped<IAdminPerformanceService, AdminPerformanceService>();
        builder.Services.AddScoped<IStatisticsReportService, StatisticsReportService>();
        builder.Services.AddScoped<IAgentSqlRunner, AgentSqlRunner>();
        builder.Services.AddScoped<AdminAgentTools>();
        builder.Services.AddHttpClient();

        builder.Services.AddSingleton<McpRateLimiter>();
        builder.Services.AddScoped<IMcpOAuthService, McpOAuthService>();
        builder.Services.AddScoped<McpExpenseTools>();
        builder.Services.AddScoped<McpToolExecutor>();
        builder.Services.AddScoped<IPricingConfigurationService, PricingConfigurationService>();
        DreamCleaningBackend.Services.ChangeSets.ChangeSetSetup.AddChangeSets(builder.Services);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            })
            .AddScheme<AuthenticationSchemeOptions, McpBearerHandler>(McpBearerHandler.SchemeName, null);
        builder.Services.AddAuthorization(AdminAgentController.AddPolicy);
        builder.Services.AddAuthorization(options => options.AddPolicy(McpServerSetup.Policy, policy => policy
            .AddAuthenticationSchemes(McpBearerHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireClaim(McpBearerHandler.GrantIdClaim)));
        builder.Services.AddCors(o => o.AddPolicy(McpOAuthController.CorsPolicy, p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        McpServerSetup.AddConnectorMcpServer(builder.Services);

        var app = builder.Build();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        McpServerSetup.MapConnector(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
        return new McpTestHost(app, client);
    }

    // ── Seeding ─────────────────────────────────────────────────────────────────────────────────

    public async Task<User> AddUserAsync(UserRole role)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = new User
        {
            FirstName = role.ToString(),
            LastName = "Tester",
            Email = $"mcp.{Guid.NewGuid():N}@example.com",
            Phone = "212" + Random.Shared.Next(1000000, 9999999),
            Role = role,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task WithDbAsync(Func<ApplicationDbContext, Task> action)
    {
        using var scope = _app.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    public async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> action)
    {
        using var scope = _app.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    public static string SiteJwt(User user) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim("Role", user.Role.ToString()),
            new Claim("UserId", user.Id.ToString()),
            new Claim("FirstName", user.FirstName)
        }),
        Expires = DateTime.UtcNow.AddHours(1),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha512)
    });

    // ── OAuth flow ──────────────────────────────────────────────────────────────────────────────

    public sealed record Pending(string ClientId, string Query, string Verifier);

    public sealed record Tokens(string Access, string Refresh, string ClientId);

    public async Task<Pending> RegisterAndBuildRequestAsync()
    {
        var reg = await Client.PostAsync("/api/oauth/register", JsonBody(new { client_name = "Claude", redirect_uris = new[] { Redirect } }));
        reg.EnsureSuccessStatusCode();
        var clientId = JsonNode.Parse(await reg.Content.ReadAsStringAsync())!["client_id"]!.GetValue<string>();
        var verifier = McpOAuthPolicy.Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = McpOAuthPolicy.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = Redirect,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = "s1",
            ["scope"] = "mcp offline_access", ["resource"] = BaseUrl + "/api/mcp"
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return new Pending(clientId, query, verifier);
    }

    public async Task<HttpResponseMessage> DecideAsync(Pending pending, User user, bool approve = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/oauth/authorize/decision")
        {
            Content = JsonBody(new { query = pending.Query, approve })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SiteJwt(user));
        return await Client.SendAsync(request);
    }

    public static string CodeFrom(string redirectUrl) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(redirectUrl).Query)["code"].ToString();

    public Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form) =>
        Client.PostAsync("/api/oauth/token", new FormUrlEncodedContent(form));

    public async Task<Tokens> ConnectAsync(User user)
    {
        var pending = await RegisterAndBuildRequestAsync();
        var decision = await DecideAsync(pending, user);
        decision.EnsureSuccessStatusCode();
        var url = JsonNode.Parse(await decision.Content.ReadAsStringAsync())!["redirectUrl"]!.GetValue<string>();
        var token = await TokenAsync(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = CodeFrom(url), ["client_id"] = pending.ClientId,
            ["redirect_uri"] = Redirect, ["code_verifier"] = pending.Verifier
        });
        var json = JsonNode.Parse(await token.Content.ReadAsStringAsync())!;
        Assert.True(token.IsSuccessStatusCode, json.ToJsonString());
        return new Tokens(json["access_token"]!.GetValue<string>(), json["refresh_token"]!.GetValue<string>(), pending.ClientId);
    }

    // ── MCP ─────────────────────────────────────────────────────────────────────────────────────

    public sealed record McpReply(int Status, string? WwwAuthenticate, JsonNode? Body)
    {
        public JsonNode? Result => Body?["result"];
        public bool IsToolError => Result?["isError"]?.GetValue<bool>() == true;
        public string Text => Result?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
        public JsonNode? Json => Text.StartsWith('{') ? JsonNode.Parse(Text) : null;
    }

    public async Task<McpReply> McpAsync(string? accessToken, string method, object? @params = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp")
        {
            Content = JsonBody(new { jsonrpc = "2.0", id = 1, method, @params = @params ?? new { } })
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        if (accessToken != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var data = string.Concat(text.Split('\n').Where(l => l.StartsWith("data: ")).Select(l => l[6..]));
        JsonNode? body = null;
        try { body = JsonNode.Parse(data.Length > 0 ? data : text); } catch (JsonException) { }
        return new McpReply((int)response.StatusCode, response.Headers.WwwAuthenticate.ToString(), body);
    }

    public Task<McpReply> CallAsync(string accessToken, string tool, object args) =>
        McpAsync(accessToken, "tools/call", new { name = tool, arguments = args });

    public static StringContent JsonBody(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class OnlyTheseControllers : ControllerFeatureProvider
    {
        private readonly HashSet<Type> _allowed;
        public OnlyTheseControllers(IEnumerable<Type> allowed) => _allowed = allowed.ToHashSet();
        protected override bool IsController(System.Reflection.TypeInfo typeInfo)
            => base.IsController(typeInfo) && _allowed.Contains(typeInfo.AsType());
    }
}

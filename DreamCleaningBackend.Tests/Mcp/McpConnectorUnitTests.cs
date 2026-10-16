using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.AdminAgent;
using DreamCleaningBackend.Services.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamCleaningBackend.Tests.Mcp;

/// <summary>The Claude connector's rules that need no database or HTTP host.</summary>
public class McpConnectorUnitTests
{
    // ── OAuth policy ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PkceAcceptsOnlyTheMatchingVerifier()
    {
        var verifier = McpOAuthPolicy.Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = McpOAuthPolicy.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        Assert.True(McpOAuthPolicy.VerifyPkce(verifier, challenge));
        Assert.False(McpOAuthPolicy.VerifyPkce(verifier + "x", challenge));
        Assert.False(McpOAuthPolicy.VerifyPkce(null, challenge));
        Assert.False(McpOAuthPolicy.VerifyPkce("short", challenge));
        Assert.False(McpOAuthPolicy.VerifyPkce(new string('a', 42) + "!", challenge));
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", false, true)]
    [InlineData("https://claude.com/api/mcp/auth_callback", false, true)]
    [InlineData("https://claude.ai/api/mcp/auth_callback/", false, false)]
    [InlineData("https://evil.example/api/mcp/auth_callback", false, false)]
    [InlineData("http://claude.ai/api/mcp/auth_callback", false, false)]
    [InlineData("http://localhost:6274/oauth/callback", false, false)]
    [InlineData("http://localhost:6274/oauth/callback", true, true)]
    [InlineData("http://127.0.0.1:33418/callback", true, true)]
    [InlineData("https://localhost/callback", true, false)]
    public void RedirectUrisAreAllowListed(string uri, bool development, bool allowed) =>
        Assert.Equal(allowed, McpOAuthPolicy.IsAllowedRedirectUri(uri, development));

    [Fact]
    public void LoopbackRedirectsMatchWithTheAnyPortButNothingElseLoosened()
    {
        Assert.True(McpOAuthPolicy.RedirectMatches("http://localhost/callback", "http://localhost:51234/callback"));
        Assert.False(McpOAuthPolicy.RedirectMatches("http://localhost/callback", "http://localhost:51234/other"));
        Assert.False(McpOAuthPolicy.RedirectMatches("http://localhost/callback", "http://127.0.0.1:51234/callback"));
        Assert.True(McpOAuthPolicy.RedirectMatches(McpTestHost.Redirect, McpTestHost.Redirect));
        Assert.False(McpOAuthPolicy.RedirectMatches(McpTestHost.Redirect, McpTestHost.Redirect + "?x=1"));
    }

    [Theory]
    [InlineData(null, "mcp")]
    [InlineData("mcp", "mcp")]
    [InlineData("mcp offline_access", "mcp offline_access")]
    [InlineData("offline_access", "mcp offline_access")]
    [InlineData("mcp admin", null)]
    public void OnlyKnownScopesAreGranted(string? requested, string? granted) =>
        Assert.Equal(granted, McpOAuthPolicy.NormalizeScope(requested));

    [Fact]
    public void SecretsAreRandomPrefixedAndOnlyEverStoredHashed()
    {
        var a = McpOAuthPolicy.NewSecret("at");
        var b = McpOAuthPolicy.NewSecret("at");
        Assert.NotEqual(a, b);
        Assert.StartsWith(McpOAuthPolicy.TokenPrefix + "at_", a);
        Assert.Equal(64, McpOAuthPolicy.Hash(a).Length);
        Assert.DoesNotContain(a, McpOAuthPolicy.Hash(a));
    }

    // ── Tool catalog (what Claude loads into every conversation) ────────────────────────────────

    [Fact]
    public void TheCatalogOffersEveryCanonicalToolAndNoPresentationTool()
    {
        var names = McpToolCatalog.Tools.Select(t => t.Name).ToHashSet();
        Assert.DoesNotContain("render_chart", names);
        Assert.DoesNotContain("render_table", names);
        foreach (var expected in new[]
                 {
                     "get_company_statistics", "get_revenue_breakdown", "get_admin_performance", "get_cleaner_payroll",
                     "get_order_sources", "get_customer_statistics", "lookup", "run_sql", "describe_data",
                     "list_expense_categories", "add_expense", "list_recent_expenses", "delete_expense"
                 })
            Assert.Contains(expected, names);
    }

    [Fact]
    public void AnnotationsMarkReadsReadOnlyAndTheDeleteDestructive()
    {
        foreach (var tool in McpToolCatalog.Tools)
        {
            var isWrite = tool.Name is "add_expense" or "delete_expense";
            Assert.Equal(!isWrite, tool.Annotations?.ReadOnlyHint);
        }
        Assert.True(McpToolCatalog.Tools.Single(t => t.Name == "delete_expense").Annotations!.DestructiveHint);
        Assert.False(McpToolCatalog.Tools.Single(t => t.Name == "add_expense").Annotations!.DestructiveHint);
    }

    [Fact]
    public void InstructionsAndDescriptionsStayShort_AndCarryNoSchema()
    {
        Assert.True(McpToolCatalog.Instructions.Length < 600, $"{McpToolCatalog.Instructions.Length} chars");
        Assert.DoesNotContain("agent_orders", McpToolCatalog.Instructions);
        foreach (var tool in McpToolCatalog.Tools)
            Assert.True(tool.Description!.Length <= 300, $"{tool.Name}: {tool.Description!.Length} chars");

        var total = JsonSerializer.Serialize(McpToolCatalog.Tools).Length;
        // About 2.5k tokens, loaded into every Claude conversation that has the connector on.
        Assert.True(total < 11_000, $"tools/list is {total} chars");
    }

    [Fact]
    public void AmbiguousExpensesAreToldToAskTheUser()
    {
        var add = McpToolCatalog.Tools.Single(t => t.Name == "add_expense");
        Assert.Contains("ask the user", add.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ask the user", McpToolCatalog.Instructions, StringComparison.OrdinalIgnoreCase);
    }

    // ── Compact formatter ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void CompactResultsNameTheDefinitionAndDropEchoesAndEmptyStrings()
    {
        var panel = JsonSerializer.Serialize(new
        {
            definition = "A long paragraph about how Statistics computes Net Income ...",
            date_range = "2026-01-01 to 2026-01-31 (service dates, inclusive)",
            filters = new[] { "x" },
            orders = 3,
            statistics_net_income = 120.5m,
            top = new[] { new { name = "ნინო", email = "" } }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        var compact = AgentToolCompactFormatter.Format(panel, "Statistics Net Income");
        var node = JsonNode.Parse(compact)!.AsObject();

        Assert.Equal("definition", node.First().Key);
        Assert.Equal("Statistics Net Income", node["definition"]!.GetValue<string>());
        Assert.False(node.ContainsKey("date_range"));
        Assert.False(node.ContainsKey("filters"));
        Assert.Equal(120.5m, node["statistics_net_income"]!.GetValue<decimal>());
        Assert.False(node["top"]![0]!.AsObject().ContainsKey("email"));
        Assert.Contains("ნინო", compact); // not \u-escaped
        Assert.True(compact.Length < panel.Length);
    }

    [Fact]
    public void PlainTextResultsPassThroughTheFormatter() =>
        Assert.Equal("Chart shown to the user.", AgentToolCompactFormatter.Format("Chart shown to the user.", "x"));

    // ── run_sql cap ─────────────────────────────────────────────────────────────────────────────

    private sealed class CapturingSql : IAgentSqlRunner
    {
        public int? MaxRowsSeen;
        public bool IsConfigured => true;

        public Task<AgentSqlResult> RunAsync(string sql, int userId, int sessionId, long? messageId, CancellationToken ct, int? maxRows = null)
        {
            MaxRowsSeen = maxRows;
            var rows = Enumerable.Range(1, maxRows ?? AgentSqlRunner.MaxRows).Select(i => new object?[] { i }).ToList();
            return Task.FromResult(new AgentSqlResult { Ok = true, Columns = new() { "i" }, Rows = rows, Truncated = true, ExecutedSql = sql });
        }
    }

    private static AdminAgentTools ToolsWith(IAgentSqlRunner sql)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"mcp-unit-{Guid.NewGuid()}").Options);
        return new AdminAgentTools(new ServiceCollection().BuildServiceProvider(), db, null!, null!, null!, sql);
    }

    [Fact]
    public async Task TheConnectorCapsRunSqlAt200RowsAndFlagsTheTruncation()
    {
        var sql = new CapturingSql();
        var tools = ToolsWith(sql);
        var args = JsonDocument.Parse("""{"sql":"SELECT 1","purpose":"cap"}""").RootElement;

        var result = await tools.ExecuteAsync("run_sql", args, new AgentToolContext
        {
            UserId = 1, Format = AgentToolFormat.Compact, MaxSqlRows = McpToolCatalog.MaxSqlRows
        }, CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        Assert.Equal(200, sql.MaxRowsSeen);
        var node = JsonNode.Parse(result.Content)!;
        Assert.Equal(200, node["rows"]!.AsArray().Count);
        Assert.True(node["truncated"]!.GetValue<bool>());
        Assert.Contains("first 200 rows", node["note"]!.GetValue<string>());
        Assert.Contains("not reconciled", node["definition"]!.GetValue<string>());
    }

    [Fact]
    public async Task ThePanelAgentKeepsItsThousandRowCap()
    {
        var sql = new CapturingSql();
        var result = await ToolsWith(sql).ExecuteAsync("run_sql",
            JsonDocument.Parse("""{"sql":"SELECT 1","purpose":"cap"}""").RootElement,
            new AgentToolContext { UserId = 1 }, CancellationToken.None);

        Assert.Equal(AgentSqlRunner.MaxRows, sql.MaxRowsSeen);
        Assert.Contains($"first {AgentSqlRunner.MaxRows} rows", result.Content);
    }

    // ── describe_data ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DescribeDataServesTheSharedDefinitionsAndSchemasOnDemand()
    {
        static JsonNode Describe(string json) =>
            JsonNode.Parse(JsonSerializer.Serialize(McpToolExecutor.DescribeData(JsonDocument.Parse(json).RootElement)))!;

        var defs = Describe("""{"section":"definitions"}""");
        Assert.Contains("Admins-tab net", defs["definitions"]!.GetValue<string>());
        Assert.Contains("Statistics Net Income", defs["definitions"]!.GetValue<string>());
        Assert.Null(defs["views"]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", defs["today"]!.GetValue<string>());

        var all = Describe("{}")["views"]!.GetValue<string>();
        Assert.Contains("At most 200 rows", all);
        foreach (var view in AgentViewCatalog.ViewNames)
            Assert.Contains("### " + view, all);

        var one = Describe("""{"section":"views","view":"agent_orders"}""")["views"]!.GetValue<string>();
        Assert.StartsWith("### agent_orders", one);
        Assert.DoesNotContain("### agent_staff", one);
    }

    // ── Expense rules ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DeleteIsLimitedToTheUsersOwnClaudeRowsOfTheLastDay()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(McpExpenseTools.DeleteRefusal(ExpenseOrigins.Claude, 7, now.AddHours(-23), 7, now));
        Assert.Contains("not added through Claude", McpExpenseTools.DeleteRefusal(null, 7, now.AddMinutes(-1), 7, now));
        Assert.Contains("another user", McpExpenseTools.DeleteRefusal(ExpenseOrigins.Claude, 8, now.AddMinutes(-1), 7, now));
        Assert.Contains("24 hours", McpExpenseTools.DeleteRefusal(ExpenseOrigins.Claude, 7, now.AddHours(-24).AddSeconds(-1), 7, now));
    }

    // ── Audit summary ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheAuditSummaryNeverKeepsNotesAndCutsLongSql()
    {
        var notes = McpToolExecutor.SummarizeArguments("add_expense",
            JsonDocument.Parse("""{"name":"Mops","notes":"door code 1234"}""").RootElement);
        Assert.DoesNotContain("1234", notes);
        Assert.Contains("[omitted]", notes);

        var longSql = "SELECT " + new string('x', 2000);
        var sql = McpToolExecutor.SummarizeArguments("run_sql", JsonSerializer.SerializeToElement(new { sql = longSql }));
        Assert.True(sql.Length < 600, sql.Length.ToString());
    }

    [Fact]
    public void TheRateLimitIsPerUser()
    {
        using var limiter = new McpRateLimiter();
        for (var i = 0; i < McpRateLimiter.PermitsPerMinute; i++)
            Assert.True(limiter.TryAcquire(1));
        Assert.False(limiter.TryAcquire(1));
        Assert.True(limiter.TryAcquire(2));
    }
}

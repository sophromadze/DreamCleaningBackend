using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Hubs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.AdminAgent;
using DreamCleaningBackend.Services.Reporting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests.AdminAgent;

/// <summary>
/// The analytics agent's tool loop against a SCRIPTED Messages API: what is sent, what is stored,
/// and how the next turn replays the conversation. No network, no real model.
/// </summary>
public class AdminAgentTurnRunnerTests
{
    // ── Fakes ──────────────────────────────────────────────────────────────────────────────────

    private sealed class ScriptedApi : HttpMessageHandler
    {
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();
        public List<JsonObject> Requests { get; } = new();
        public List<string> Betas { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            Betas.Add(request.Headers.TryGetValues("anthropic-beta", out var b) ? string.Join(",", b) : string.Empty);
            var (status, body) = Responses.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class RecordingHub : IHubContext<UserManagementHub>
    {
        public List<(string Group, string Method, object? Payload)> Sent { get; } = new();
        public IHubClients Clients => new HubClients(this);
        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class HubClients(RecordingHub hub) : IHubClients
        {
            public IClientProxy Group(string groupName) => new Proxy(hub, groupName);
            public IClientProxy All => new Proxy(hub, "*");
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new Proxy(hub, "*");
            public IClientProxy Client(string connectionId) => new Proxy(hub, "conn:" + connectionId);
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new Proxy(hub, "conns");
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new Proxy(hub, groupName);
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => new Proxy(hub, string.Join(",", groupNames));
            public IClientProxy User(string userId) => new Proxy(hub, "user:" + userId);
            public IClientProxy Users(IReadOnlyList<string> userIds) => new Proxy(hub, "users");
        }

        private sealed class Proxy(RecordingHub hub, string group) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            {
                hub.Sent.Add((group, method, args.FirstOrDefault()));
                return Task.CompletedTask;
            }
        }
    }

    private sealed class FakeStatistics : IStatisticsReportService
    {
        public Task<OrderStatisticsDto> GetOrderStatisticsAsync(DateTime? from, DateTime? to, bool includeUpcoming = false, DateTime? upcomingTo = null) =>
            Task.FromResult(new OrderStatisticsDto
            {
                TotalOrders = 4, TotalAmount = 1234m, TotalTaxes = 100m, TotalCleanersSalary = 400m,
                TotalCompanyRevenueGross = 834m, TotalExpenses = 300m, TotalCompanyRevenue = 534m
            });
        public Task<List<DailyStatisticsDto>> GetDailyStatisticsAsync(DateTime? from, DateTime? to) => throw new NotSupportedException();
        public Task<StatisticsOrderRows> GetOrderRowsAsync(DateTime? from, DateTime? to, bool includeUpcoming = false) => throw new NotSupportedException();
    }

    private sealed class Unused : IAdminPerformanceService, IAgentSqlRunner
    {
        public Task<AdminPerformanceListDto> GetAsync(DateTime? f, DateTime? t) => throw new NotSupportedException();
        public Task<List<AdminPerformanceOrderRow>> GetOrderRowsAsync(DateTime? f, DateTime? t) => throw new NotSupportedException();
        public bool IsConfigured => false;
        public Task<AgentSqlResult> RunAsync(string sql, int u, int s, long? m, CancellationToken ct) => throw new NotSupportedException();
    }

    // ── Rig ────────────────────────────────────────────────────────────────────────────────────

    private readonly ApplicationDbContext _db = new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase($"agent-runner-{Guid.NewGuid()}", b => b.EnableNullChecks(false))
        .Options);
    private readonly ScriptedApi _api = new();
    private readonly RecordingHub _hub = new();
    private readonly CapturingLogger Log = new();

    /// <summary>Keeps what the runner logged, so a failing assertion can say WHY a turn failed.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<AdminAgentTurnRunner>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
            => Lines.Add(fmt(state, ex) + (ex != null ? " | " + ex : ""));
    }

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminAgent:ApiKey"] = "test-key",
            ["AdminAgent:PriceInputPerMTok"] = "2",
            ["AdminAgent:PriceOutputPerMTok"] = "10",
            ["AdminAgent:PriceCacheWritePerMTok"] = "2.5",
            ["AdminAgent:PriceCacheReadPerMTok"] = "0.2"
        })
        .Build();

    private AdminAgentService Service() =>
        new(_db, Config(), new Unused(), null!, NullLogger<AdminAgentService>.Instance);

    private AdminAgentTurnRunner Runner()
    {
        var client = new AdminAgentAnthropicClient(NullLogger<AdminAgentAnthropicClient>.Instance,
            _ => new HttpClient(_api) { BaseAddress = null });
        var tools = new AdminAgentTools(new ServiceCollection().BuildServiceProvider(), _db, new FakeStatistics(),
            new Unused(), null!, new Unused());
        return new AdminAgentTurnRunner(_db, Config(), client, tools, _hub, Log);
    }

    private static string Response(string stopReason, JsonArray content, int input, int output, int cacheWrite = 0, int cacheRead = 0) =>
        new JsonObject
        {
            ["id"] = "msg_" + Guid.NewGuid().ToString("N")[..8],
            ["type"] = "message",
            ["role"] = "assistant",
            ["stop_reason"] = stopReason,
            ["content"] = content,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = input, ["output_tokens"] = output,
                ["cache_creation_input_tokens"] = cacheWrite, ["cache_read_input_tokens"] = cacheRead
            }
        }.ToJsonString();

    private static JsonArray ToolTurn() => new()
    {
        new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "sig-ABC-123" },
        new JsonObject { ["type"] = "text", ["text"] = "Checking Statistics." },
        new JsonObject
        {
            ["type"] = "tool_use", ["id"] = "tu_1", ["name"] = "get_company_statistics",
            ["input"] = new JsonObject { ["from"] = "2026-10-01", ["to"] = "2026-10-16" }
        },
        new JsonObject
        {
            ["type"] = "tool_use", ["id"] = "tu_2", ["name"] = "render_chart",
            ["input"] = new JsonObject
            {
                ["chart_type"] = "bar", ["title"] = "Revenue", ["format"] = "currency",
                ["labels"] = new JsonArray("Oct"),
                ["series"] = new JsonArray(new JsonObject { ["name"] = "Company Revenue", ["data"] = new JsonArray(1234) })
            }
        }
    };

    // ── Tests ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AToolTurnIsRunStoredAndReplayedVerbatim()
    {
        var service = Service();
        var session = await service.CreateSessionAsync(1, null);

        // Turn 1: a tool round, then the answer.
        _api.Responses.Enqueue((HttpStatusCode.OK, Response("tool_use", ToolTurn(), 1000, 50, cacheWrite: 800)));
        _api.Responses.Enqueue((HttpStatusCode.OK, Response("end_turn",
            new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Company Revenue (Statistics) was **$1,234.00**." }), 200, 30, cacheRead: 900)));
        var t1 = await service.StartTurnAsync(1, session.Id, "Revenue this month?", null);
        await Runner().RunAsync(1, session.Id, t1.AssistantMessage.Id);

        var answer = await _db.AdminAgentMessages.AsNoTracking().FirstAsync(m => m.Id == t1.AssistantMessage.Id);
        Assert.True(answer.Status == AdminAgentMessageStatuses.Complete, string.Join(Environment.NewLine, Log.Lines));
        Assert.Equal("Company Revenue (Statistics) was **$1,234.00**.", answer.Content);
        Assert.Null(answer.ProgressLabel);

        // The chart is a part of the answer; the trail lists what was calculated, not the chart.
        var parts = JsonNode.Parse(answer.PartsJson!)!.AsArray();
        Assert.Equal("chart", (string?)parts.Single()!["type"]);
        var trail = JsonNode.Parse(answer.ToolCallsJson!)!.AsArray();
        var step = Assert.Single(trail);
        Assert.Equal("get_company_statistics", (string?)step!["tool"]);
        Assert.Equal("Statistics Net Income", (string?)step["definition"]);
        Assert.StartsWith("2026-10-01 to 2026-10-16", (string?)step["date_range"]);

        // Usage and cost: (1200 in x $2 + 80 out x $10 + 800 write x $2.5 + 900 read x $0.2) / 1M.
        Assert.Equal(1200, answer.InputTokens);
        Assert.Equal(80, answer.OutputTokens);
        Assert.Equal(800, answer.CacheWriteTokens);
        Assert.Equal(900, answer.CacheReadTokens);
        Assert.Equal(0.00538m, answer.CostUsd);
        var storedSession = await _db.AdminAgentSessions.AsNoTracking().FirstAsync();
        Assert.Equal(0.00538m, storedSession.EstimatedCostUsd);

        // Request shape: cached system prompt, drop_block binding, refusal fallback, both betas.
        var first = _api.Requests[0];
        Assert.Equal("claude-sonnet-5-5", (string?)first["model"]);
        Assert.Equal("ephemeral", (string?)first["system"]![0]!["cache_control"]!["type"]);
        Assert.Equal("drop_block", (string?)first["thinking"]!["block_binding"]!["prefix_mismatch_behavior"]);
        Assert.Equal("default", (string?)first["fallbacks"]);
        Assert.Equal("medium", (string?)first["output_config"]!["effort"]);
        Assert.Contains("thinking-binding-controls-2026-08-01", _api.Betas[0]);
        Assert.Contains("server-side-fallback-2026-07-01", _api.Betas[0]);

        // Second request: the assistant turn went back unchanged (signature included), followed by
        // BOTH tool results in one user message.
        var second = _api.Requests[1]["messages"]!.AsArray();
        var echoed = second[^2]!;
        Assert.Equal("assistant", (string?)echoed["role"]);
        Assert.Equal(ToolTurn().ToJsonString(), echoed["content"]!.ToJsonString());
        var results = second[^1]!["content"]!.AsArray();
        Assert.Equal(new[] { "tu_1", "tu_2" }, results.Select(r => (string?)r!["tool_use_id"]));
        Assert.Contains("statistics_net_income", (string?)results[0]!["content"]);

        // Progress went to the owner's group only, and the completion was announced.
        Assert.All(_hub.Sent, s => Assert.Equal("User_1", s.Group));
        Assert.Contains(_hub.Sent, s => s.Method == AdminAgentTurnRunner.ProgressEvent);
        Assert.Equal(AdminAgentTurnRunner.CompletedEvent, _hub.Sent.Last().Method);

        // Turn 2 (fails at the API) and turn 3: the failed pair is left out, turn 1 is replayed as sent.
        _api.Responses.Enqueue((HttpStatusCode.InternalServerError, "{\"type\":\"error\"}"));
        var t2 = await service.StartTurnAsync(1, session.Id, "And last month?", null);
        await Runner().RunAsync(1, session.Id, t2.AssistantMessage.Id);
        var failed = await _db.AdminAgentMessages.AsNoTracking().FirstAsync(m => m.Id == t2.AssistantMessage.Id);
        Assert.Equal(AdminAgentMessageStatuses.Error, failed.Status);
        Assert.Null(failed.ApiMessagesJson);

        _api.Responses.Enqueue((HttpStatusCode.OK, Response("end_turn",
            new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Which admin do you mean?" }), 10, 5)));
        var t3 = await service.StartTurnAsync(1, session.Id, "How many did Nino book?", null);
        await Runner().RunAsync(1, session.Id, t3.AssistantMessage.Id);

        var replay = _api.Requests.Last()["messages"]!.AsArray();
        var turn1Sent = _api.Requests[1]["messages"]!.AsArray(); // turn 1's last request, minus its final answer
        Assert.Equal(5, replay.Count); // q1, tool turn, tool results, answer 1, q3
        for (var i = 0; i < turn1Sent.Count; i++)
            Assert.Equal(turn1Sent[i]!.ToJsonString(), replay[i]!.ToJsonString());
        Assert.Equal("Company Revenue (Statistics) was **$1,234.00**.", (string?)replay[3]!["content"]![0]!["text"]);
        Assert.Contains("How many did Nino book?", replay[4]!.ToJsonString());
        Assert.DoesNotContain("And last month?", replay.ToJsonString());
    }

    [Fact]
    public async Task ARefusalIsShownAndNotReplayed()
    {
        var service = Service();
        var session = await service.CreateSessionAsync(1, null);
        _api.Responses.Enqueue((HttpStatusCode.OK, Response("refusal", new JsonArray(), 10, 0)));
        var t = await service.StartTurnAsync(1, session.Id, "Something odd", null);
        await Runner().RunAsync(1, session.Id, t.AssistantMessage.Id);

        var m = await _db.AdminAgentMessages.AsNoTracking().FirstAsync(x => x.Id == t.AssistantMessage.Id);
        Assert.Equal(AdminAgentMessageStatuses.Error, m.Status);
        Assert.Contains("declined", m.ErrorMessage);
        Assert.Null(m.ApiMessagesJson);
    }
}

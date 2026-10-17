using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.AdminAgent;
using DreamCleaningBackend.Services.ChangeSets;
using ModelContextProtocol.Protocol;

namespace DreamCleaningBackend.Services.Mcp
{
    /// <summary>
    /// Per-user limit on connector tool calls (2026-10). In memory, process-wide: the API runs as
    /// one process. Partitioned by USER, not IP — every call arrives from Anthropic's cloud, so an
    /// IP limit would be one shared bucket for everybody.
    /// </summary>
    public sealed class McpRateLimiter : IDisposable
    {
        public const int PermitsPerMinute = 60;

        private readonly PartitionedRateLimiter<int> _limiter = PartitionedRateLimiter.Create<int, int>(userId =>
            RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = PermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

        public bool TryAcquire(int userId)
        {
            using var lease = _limiter.AttemptAcquire(userId);
            return lease.IsAcquired;
        }

        public void Dispose() => _limiter.Dispose();
    }

    /// <summary>
    /// Runs one connector tool call (2026-10): re-checks the caller, applies the per-user limit,
    /// dispatches to the shared tool core, and writes one <see cref="McpToolCallLog"/> row whatever
    /// happens.
    ///
    /// The analytics tools are <see cref="AdminAgentTools"/> — the panel agent's own code — run in
    /// <see cref="AgentToolFormat.Compact"/> with a <see cref="McpToolCatalog.MaxSqlRows"/> row cap.
    /// The expense tools are <see cref="McpExpenseTools"/> over the panel's ExpenseService.
    ///
    /// Every write (2026-10) is a <see cref="ChangeSetService"/> preview that returns a token, and
    /// apply_change is the only tool that changes data. The Services tools run the panel's own
    /// <see cref="CatalogAdminOperations"/>; history and revert read the shared audit log.
    /// </summary>
    public sealed class McpToolExecutor
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly AdminAgentTools _tools;
        private readonly McpExpenseTools _expenses;
        private readonly ChangeSetService _changeSets;
        private readonly CatalogSnapshotReader _catalog;
        private readonly ChangeHistoryReader _history;
        private readonly IMcpOAuthService _oauth;
        private readonly McpRateLimiter _rateLimiter;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<McpToolExecutor> _logger;

        public McpToolExecutor(AdminAgentTools tools, McpExpenseTools expenses, IMcpOAuthService oauth,
            McpRateLimiter rateLimiter, ApplicationDbContext context, ILogger<McpToolExecutor> logger,
            ChangeSetService changeSets, CatalogSnapshotReader catalog, ChangeHistoryReader history)
        {
            _tools = tools;
            _expenses = expenses;
            _changeSets = changeSets;
            _catalog = catalog;
            _history = history;
            _oauth = oauth;
            _rateLimiter = rateLimiter;
            _context = context;
            _logger = logger;
        }

        public async Task<CallToolResult> CallAsync(McpCaller? caller, string name, JsonElement args, CancellationToken ct)
        {
            if (caller == null)
                return Fail("Not signed in. Reconnect the connector.");

            var stopwatch = Stopwatch.StartNew();
            string? error = null;
            string? content = null;
            try
            {
                // The bearer handler checked this request already; this is the same rule again at
                // the tool itself, so no wiring change can ever skip it.
                if (!await _oauth.IsGrantStillValidAsync(caller.GrantId, caller.UserId))
                    error = "Access was revoked or this account is no longer a SuperAdmin. Reconnect the connector.";
                else if (!_rateLimiter.TryAcquire(caller.UserId))
                    error = $"Too many tool calls (limit {McpRateLimiter.PermitsPerMinute} a minute). Wait a minute and try again.";
                else
                    content = await DispatchAsync(caller, name, args, ct);
            }
            catch (McpToolException ex)
            {
                error = ex.Message;
            }
            catch (ToolInputException ex)
            {
                error = ex.Message;
            }
            catch (ChangeSetException ex)
            {
                error = ex.Message;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                error = "Cancelled.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Claude connector tool {Tool} failed for user {UserId}", name, caller.UserId);
                error = "The tool failed on the server. Try again, or use the admin panel.";
            }
            stopwatch.Stop();

            await LogAsync(caller, name, args, (int)stopwatch.ElapsedMilliseconds, error);
            return error != null ? Fail(error) : new CallToolResult { Content = [new TextContentBlock { Text = content! }] };
        }

        private async Task<string> DispatchAsync(McpCaller caller, string name, JsonElement args, CancellationToken ct)
        {
            if (McpToolCatalog.AnalyticsTools.Contains(name))
            {
                var ctx = new AgentToolContext
                {
                    UserId = caller.UserId,
                    SessionId = 0,
                    MessageId = 0,
                    Format = AgentToolFormat.Compact,
                    MaxSqlRows = McpToolCatalog.MaxSqlRows
                };
                var result = await _tools.ExecuteAsync(name, args, ctx, ct);
                if (result.IsError) throw new McpToolException(result.Content);
                return result.Content;
            }

            var changeCaller = ChangeSetCaller.Connector(caller.UserId, caller.GrantId);
            object payload = name switch
            {
                "describe_data" => DescribeData(args),
                "list_expense_categories" => await _expenses.ListCategoriesAsync(),
                "add_expense" => await _changeSets.PreviewAsync(changeCaller, ChangeSetKinds.AddExpense, args, ct),
                "list_recent_expenses" => await _expenses.ListRecentAsync(args, caller.UserId),
                "delete_expense" => await _changeSets.PreviewAsync(changeCaller, ChangeSetKinds.DeleteExpense, args, ct),
                "get_services_catalog" => await _catalog.ReadAsync(Bool(args, "include_inactive") ?? true, ct),
                "preview_catalog_changes" => await _changeSets.PreviewAsync(changeCaller, ChangeSetKinds.Catalog, args, ct),
                "preview_revert" => await _changeSets.PreviewAsync(changeCaller, ChangeSetKinds.Revert, args, ct),
                "apply_change" => await _changeSets.ApplyAsync(changeCaller, Text(args, "token"), Text(args, "note"), ct),
                "get_change_history" => await _history.ListAsync(HistoryFilter(args), ct),
                "get_change_detail" => await _history.DetailAsync(Long(args, "entry_id") ?? throw new McpToolException("entry_id is required."), ct),
                _ => throw new McpToolException($"Unknown tool: {name}")
            };
            return JsonSerializer.Serialize(payload, Json);
        }

        private static ChangeHistoryFilter HistoryFilter(JsonElement args) => new()
        {
            EntityType = Text(args, "entity_type"),
            EntityId = Long(args, "entity_id"),
            Action = Text(args, "action"),
            Source = Text(args, "source"),
            Actor = Text(args, "actor"),
            ActorUserId = (int?)Long(args, "actor_user_id"),
            BatchId = Text(args, "batch_id"),
            From = Date(args, "from"),
            To = Date(args, "to"),
            Search = Text(args, "search"),
            Page = (int)(Long(args, "page") ?? 1),
            PageSize = (int)(Long(args, "page_size") ?? 20)
        };

        private static string? Text(JsonElement a, string n) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString() : null;

        private static long? Long(JsonElement a, string n) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)
                ? l : null;

        private static bool? Bool(JsonElement a, string n) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : null;

        private static DateTime? Date(JsonElement a, string n)
        {
            var text = Text(a, n);
            if (text == null) return null;
            if (!DateTime.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                throw new McpToolException($"{n} must be YYYY-MM-DD.");
            return d;
        }

        // ── describe_data ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The panel agent's own business definitions and view schemas, sliced from the one system
        /// prompt that holds them, so an edit to a definition reaches both agents.
        /// </summary>
        public static object DescribeData(JsonElement args)
        {
            var section = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("section", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : "all";
            var view = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("view", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()?.Trim() : null;

            string? definitions = section is "definitions" or "all" ? BusinessDefinitions.Value : null;
            string? views = null;
            if (section is "views" or "all")
            {
                if (!string.IsNullOrEmpty(view))
                {
                    var match = AgentViewCatalog.Views.FirstOrDefault(x => string.Equals(x.Name, view, StringComparison.OrdinalIgnoreCase))
                        ?? throw new McpToolException($"Unknown view '{view}'. Views: {string.Join(", ", AgentViewCatalog.ViewNames)}.");
                    views = string.Join("\n", new[] { $"### {match.Name}", match.Meaning }
                        .Concat(match.Columns.Select(c => $"- {c.Name} ({c.Type}): {c.Meaning}")));
                }
                else
                {
                    views = SqlNotes.Value + "\n\n" + AgentViewCatalog.PromptSchema();
                }
            }

            return new { today = McpExpenseTools.TodayNy(), definitions, views };
        }

        private static readonly Lazy<string> BusinessDefinitions = new(() =>
            Slice(AdminAgentSystemPrompt.Build(), "# BUSINESS DEFINITIONS (editable)", "# Tools and data"));

        private static readonly Lazy<string> SqlNotes = new(() =>
        {
            var tools = Slice(AdminAgentSystemPrompt.Build(), "# Tools and data", "### " + AgentViewCatalog.Views[0].Name);
            var line = tools.Split('\n').FirstOrDefault(l => l.StartsWith("run_sql", StringComparison.Ordinal)) ?? string.Empty;
            return line.Replace($"At most {AgentSqlRunner.MaxRows} rows", $"At most {McpToolCatalog.MaxSqlRows} rows").Trim();
        });

        private static string Slice(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal);
            if (from < 0) return string.Empty;
            from += start.Length;
            var to = text.IndexOf(end, from, StringComparison.Ordinal);
            return (to < 0 ? text[from..] : text[from..to]).Trim();
        }

        // ── Audit ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The arguments as recorded: expense notes never, SQL cut to 500 characters, the whole
        /// summary to 1000. Public for the tests.
        /// </summary>
        public static string SummarizeArguments(string tool, JsonElement args)
        {
            if (args.ValueKind != JsonValueKind.Object) return "{}";
            var node = JsonNode.Parse(args.GetRawText()) as JsonObject ?? new JsonObject();
            if (node.ContainsKey("notes")) node["notes"] = "[omitted]";
            // A change token is single-use, but it is still a credential until it is used.
            if (node.ContainsKey("token")) node["token"] = "[omitted]";
            if (node["sql"] is JsonValue sql && sql.TryGetValue<string>(out var text) && text.Length > 500)
                node["sql"] = text[..500] + "…";
            var json = node.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            return json.Length > 1000 ? json[..999] + "…" : json;
        }

        private async Task LogAsync(McpCaller caller, string tool, JsonElement args, int durationMs, string? error)
        {
            try
            {
                _context.ChangeTracker.Clear();
                _context.McpToolCallLogs.Add(new McpToolCallLog
                {
                    UserId = caller.UserId,
                    GrantId = caller.GrantId,
                    Tool = tool.Length > 64 ? tool[..64] : tool,
                    ArgumentsSummary = SummarizeArguments(tool, args),
                    DurationMs = durationMs,
                    Succeeded = error == null,
                    Error = error?.Length > 500 ? error[..500] : error,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // The audit row must never cost the answer, and never vanish silently either.
                _logger.LogError(ex, "Could not write the Claude connector audit row for {Tool}, user {UserId}", tool, caller.UserId);
            }
        }

        private static CallToolResult Fail(string message) =>
            new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
    }
}

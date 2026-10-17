using System.Text.Json;
using System.Text.Json.Nodes;
using DreamCleaningBackend.Services.AdminAgent;
using ModelContextProtocol.Protocol;

namespace DreamCleaningBackend.Services.Mcp
{
    /// <summary>
    /// What the Claude connector lists in <c>tools/list</c> (2026-10). This text is loaded into
    /// every Claude conversation that has the connector enabled, so it is kept SHORT: one line per
    /// tool, no schemas or business definitions (those are behind <c>describe_data</c>).
    ///
    /// The analytics tools reuse the panel agent's input schemas (<see cref="AdminAgentTools.Definitions"/>)
    /// with shorter descriptions; render_chart and render_table are not offered — Claude's app
    /// draws its own charts and tables.
    /// </summary>
    public static class McpToolCatalog
    {
        public const int MaxSqlRows = 200;

        public const string ServerName = "dream-cleaning";
        public const string ServerTitle = "Dream Cleaning";

        /// <summary>The initialize instructions. Short on purpose — see the class comment.</summary>
        public const string Instructions =
            "Dream Cleaning (NYC) business data for a SuperAdmin. Dates are New York calendar days; " +
            "describe_data and list_expense_categories return today's date. Prefer the get_* tools (they match the admin panel); " +
            "call describe_data before run_sql. Every money figure must name the result's definition field. " +
            "For add_expense, ask the user when the category or one-time vs recurring is unclear. " +
            "Every write is two steps: a preview tool returns the exact changes and a token; show them to the user and call apply_change only after they confirm.";

        public static readonly IReadOnlySet<string> AnalyticsTools = new HashSet<string>
        {
            "get_company_statistics", "get_revenue_breakdown", "get_admin_performance", "get_cleaner_payroll",
            "get_order_sources", "get_customer_statistics", "lookup", "run_sql"
        };

        private static readonly Dictionary<string, (string Title, string Description)> AnalyticsText = new()
        {
            ["get_company_statistics"] = ("Company statistics",
                "Statistics/Finances page totals for a date range: orders, Company Revenue, sales tax, tips, cleaner pay, fees, bonuses, expenses by category, Statistics Net Income."),
            ["get_revenue_breakdown"] = ("Revenue breakdown",
                "Statistics figures grouped by 1-2 of: period, service type, booking admin, recurring plan, commercial, payment method, new customer. Optional filters."),
            ["get_admin_performance"] = ("Admin performance",
                "Outgoing Payments -> Admins: per booking admin, Done+paid orders, order total, tax, wages, Stripe fees, Admins-tab net (bonuses shown separately, not subtracted); or one admin by period/service type/new customer."),
            ["get_cleaner_payroll"] = ("Cleaner payroll",
                "Outgoing Payments cleaner pay: hours, salary, tips, payout, paid and still owed, per cleaner or per order."),
            ["get_order_sources"] = ("Order sources",
                "Done orders and revenue by acquisition source (paid, organic, direct, referral, social, email, AI, admin) over time."),
            ["get_customer_statistics"] = ("Customer statistics",
                "Active, new, returning and repeat customers, retention, order frequency, top customers by spend."),
            ["lookup"] = ("Look up ids",
                "Resolve names to ids/keys: staff, service types, cleaners, plan tiers."),
            ["run_sql"] = ("Run read-only SQL",
                $"One read-only MariaDB SELECT/WITH over the agent_* views, only when no get_* tool fits. Call describe_data first. Max {MaxSqlRows} rows, 10 s; aggregate in SQL. Money sums are raw, not reconciled with the panel.")
        };

        public static IReadOnlyList<Tool> Tools { get; } = Build();

        private static List<Tool> Build()
        {
            // Only the two hints Claude acts on; the tool already carries its title.
            static ToolAnnotations Read(string title) => new() { ReadOnlyHint = true };

            var tools = new List<Tool>();
            foreach (var def in AdminAgentTools.Definitions().OfType<JsonObject>())
            {
                var name = def["name"]!.GetValue<string>();
                if (!AnalyticsText.TryGetValue(name, out var text)) continue; // render_chart / render_table
                tools.Add(new Tool
                {
                    Name = name,
                    Title = text.Title,
                    Description = text.Description,
                    InputSchema = ToElement(ShortenDates(def["input_schema"]!.DeepClone())),
                    Annotations = Read(text.Title)
                });
            }

            tools.Add(new Tool
            {
                Name = "describe_data",
                Title = "Describe data",
                Description = "Business definitions (how each money figure is computed), the agent_* view schemas for run_sql, and today's New York date.",
                InputSchema = Schema("""
                    {"type":"object","properties":{
                      "section":{"type":"string","enum":["definitions","views","all"],"description":"Default all."},
                      "view":{"type":"string","description":"Only this agent_* view's columns."}}}
                    """),
                Annotations = Read("Describe data")
            });

            tools.Add(new Tool
            {
                Name = "list_expense_categories",
                Title = "Expense categories",
                Description = "Expense categories add_expense accepts, and today's New York date (for 'today'/'yesterday').",
                InputSchema = Schema("""{"type":"object","properties":{}}"""),
                Annotations = Read("Expense categories")
            });

            tools.Add(new Tool
            {
                Name = "add_expense",
                Title = "Add expense",
                Description = "Preview adding a company expense (Expenses page validation); returns a token for apply_change, writes nothing. Call list_expense_categories first. If the category or one-time vs recurring is unclear, ask the user; never guess. Salaries and Google Ads are not accepted.",
                InputSchema = Schema("""
                    {"type":"object","properties":{
                      "name":{"type":"string","description":"What it is, e.g. 'Mops and buckets'."},
                      "category_id":{"type":"integer","description":"From list_expense_categories."},
                      "amount":{"type":"number","description":"USD. Negative = a credit/refund."},
                      "type":{"type":"string","enum":["one_time","recurring"]},
                      "frequency_months":{"type":"integer","description":"Recurring only: 1 monthly, 3 quarterly, 6, 12 yearly."},
                      "start_date":{"type":"string","description":"YYYY-MM-DD, New York date. Recurring: first charge."},
                      "prorate_by_day":{"type":"boolean","description":"Monthly recurring only: charge partial months by days. Default false."},
                      "notes":{"type":"string"},
                      "confirm_duplicate":{"type":"boolean","description":"Only after the user confirms a second identical entry."}},
                     "required":["name","category_id","amount","type","start_date"]}
                    """),
                Annotations = Preview()
            });

            tools.Add(new Tool
            {
                Name = "list_recent_expenses",
                Title = "Recent expenses",
                Description = "Most recently entered expenses, newest first, to check or correct entries (Google Ads daily rows excluded).",
                InputSchema = Schema("""
                    {"type":"object","properties":{
                      "limit":{"type":"integer","description":"1-50, default 10."},
                      "source":{"type":"string","enum":["all","claude"],"description":"claude = only ones added through this connector."}}}
                    """),
                Annotations = Read("Recent expenses")
            });

            tools.Add(new Tool
            {
                Name = "delete_expense",
                Title = "Delete expense",
                Description = "Preview deleting an expense the same user added through this connector in the last 24 hours; returns a token for apply_change. Anything else is changed on the Expenses page.",
                InputSchema = Schema("""
                    {"type":"object","properties":{"expense_id":{"type":"integer"}},"required":["expense_id"]}
                    """),
                Annotations = Preview()
            });

            // ── Services catalogue, change sets, history (2026-10) ─────────────────────────

            tools.Add(new Tool
            {
                Name = "get_services_catalog",
                Title = "Services catalogue",
                Description = "Whole Services configuration in one call: service types with services (included amounts, rate tiers), poll questions and configured extras; all extras; subscription plans. Every column, the ids the write tool takes, and per row the orders, future orders and active recurring plans using it.",
                InputSchema = Schema("""
                    {"type":"object","properties":{
                      "include_inactive":{"type":"boolean","description":"Default true."}}}
                    """),
                Annotations = Read("Services catalogue")
            });

            tools.Add(new Tool
            {
                Name = "preview_catalog_changes",
                Title = "Preview Services changes",
                Description =
                    "Preview Services-tab changes (all or none); writes nothing, returns each affected record old -> new and a token for apply_change. " +
                    "Operations {entity, op, id?, fields?}; fields use get_services_catalog column names, an update needs only the changed ones, \"@n\" = the record operation n creates.",
                InputSchema = Schema("""
                    {"type":"object","properties":{
                      "operations":{"type":"array","maxItems":50,"items":{"type":"object","properties":{
                        "entity":{"type":"string","enum":["service_type","service","included_amount","rate_tier","extra_service","subscription","poll_question","pricing_configuration"]},
                        "op":{"type":"string","enum":["create","update","copy","activate","deactivate","delete","import"],"description":"service_type/service/extra_service/subscription: create, update, activate, deactivate, delete (service and extra_service also copy). included_amount/rate_tier/poll_question: create, update, delete. pricing_configuration: import."},
                        "id":{"description":"Record id, or \"@n\". For copy: the source record."},
                        "service_id":{"description":"included_amount/rate_tier create: the owning service id or \"@n\"."},
                        "target_service_type_id":{"description":"copy: the service type to copy into."},
                        "fields":{"type":"object","description":"Column values, e.g. {\"cost\": 25, \"time_duration\": 30}."},
                        "payload":{"type":"object","description":"pricing_configuration import: the exported configuration."}},
                       "required":["entity","op"]}}},
                     "required":["operations"]}
                    """),
                Annotations = Preview()
            });

            tools.Add(new Tool
            {
                Name = "apply_change",
                Title = "Apply previewed change",
                Description = "Apply a change previewed by preview_catalog_changes, add_expense, delete_expense or preview_revert, ONLY after the user confirmed it. One transaction; refused if any record changed since the preview. Tokens are single-use and expire after 15 minutes.",
                InputSchema = Schema("""
                    {"type":"object","properties":{
                      "token":{"type":"string"},
                      "note":{"type":"string","description":"Optional note stored with the history entries (max 500)."}},
                     "required":["token"]}
                    """),
                Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true }
            });

            tools.Add(new Tool
            {
                Name = "get_change_history",
                Title = "Change history",
                Description = "History of the Services catalogue, subscription plans and expenses (admin panel, Claude and system changes), newest first, paginated. Filters are optional.",
                InputSchema = Schema("""
                    {"type":"object","properties":{
                      "entity_type":{"type":"string","description":"ServiceType, Service, ServiceThreshold (included_amount), ServiceRateTier, ExtraService, PollQuestion, Subscription, Expense, ExpenseCategory."},
                      "entity_id":{"type":"integer"},
                      "action":{"type":"string","enum":["create","update","delete","revert"]},
                      "source":{"type":"string","enum":["mcp","admin","system","unknown"]},
                      "actor":{"type":"string","description":"Name, or part of it."},
                      "actor_user_id":{"type":"integer"},
                      "batch_id":{"type":"string"},
                      "from":{"type":"string","description":"YYYY-MM-DD"},
                      "to":{"type":"string","description":"YYYY-MM-DD, inclusive"},
                      "search":{"type":"string","description":"Text in the values or the note."},
                      "page":{"type":"integer"},
                      "page_size":{"type":"integer","description":"1-50, default 20."}}}
                    """),
                Annotations = Read("Change history")
            });

            tools.Add(new Tool
            {
                Name = "get_change_detail",
                Title = "Change detail",
                Description = "One history entry in full: every field before and after, which changed, its batch, and what reverted it.",
                InputSchema = Schema("""
                    {"type":"object","properties":{"entry_id":{"type":"integer"}},"required":["entry_id"]}
                    """),
                Annotations = Read("Change detail")
            });

            tools.Add(new Tool
            {
                Name = "preview_revert",
                Title = "Preview revert",
                Description = "Preview reverting one history entry (entry_id) or a whole change (batch_id); returns a token for apply_change. Later changes to the same records show as conflicts and block it unless override_conflicts; a created record that orders use can be deactivated instead.",
                InputSchema = Schema("""
                    {"type":"object","properties":{
                      "entry_id":{"type":"integer"},
                      "batch_id":{"type":"string"},
                      "override_conflicts":{"type":"boolean","description":"Only after the user chose to overwrite the later changes shown."},
                      "use_deactivate_alternative":{"type":"boolean","description":"Only after the user chose deactivating over deleting."}}}
                    """),
                Annotations = Preview()
            });

            return tools;
        }

        /// <summary>A preview: writes no business data (only a pending, single-use token).</summary>
        private static ToolAnnotations Preview() => new() { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = true };

        /// <summary>The panel's from/to descriptions repeat the date rules the instructions state once.</summary>
        private static JsonNode ShortenDates(JsonNode schema)
        {
            if (schema["properties"] is JsonObject props)
            {
                if (props["from"] is JsonObject from) from["description"] = "YYYY-MM-DD (service date)";
                if (props["to"] is JsonObject to) to["description"] = "YYYY-MM-DD, inclusive";
            }
            return schema;
        }

        private static JsonElement Schema(string json) => JsonSerializer.Deserialize<JsonElement>(json);

        private static JsonElement ToElement(JsonNode node) => JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }
}

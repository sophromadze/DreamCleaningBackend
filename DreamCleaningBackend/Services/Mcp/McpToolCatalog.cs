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
            "For add_expense, ask the user when the category or one-time vs recurring is unclear.";

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
                "Outgoing Payments -> Admins: per booking admin, Done+paid orders, order total, tax, wages, bonuses, Admins-tab net; or one admin by period/service type/new customer."),
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
                Description = "Add a company expense with the Expenses page's own validation. Call list_expense_categories first. If the category or one-time vs recurring is unclear, ask the user; never guess. Salaries and Google Ads are not accepted.",
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
                Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false }
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
                Description = "Delete an expense the same user added through this connector in the last 24 hours. Anything else is changed on the Expenses page.",
                InputSchema = Schema("""
                    {"type":"object","properties":{"expense_id":{"type":"integer"}},"required":["expense_id"]}
                    """),
                Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true }
            });

            return tools;
        }

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

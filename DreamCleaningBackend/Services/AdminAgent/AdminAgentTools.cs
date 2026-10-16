using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Reporting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.AdminAgent
{
    /// <summary>What one tool call did, for the answer's "How this was calculated" section.</summary>
    public sealed class AgentToolTrailEntry
    {
        public string Tool { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public JsonNode? Input { get; set; }
        public string? DateRange { get; set; }
        public List<string> Filters { get; set; } = new();
        public string? Definition { get; set; }
        public string? Sql { get; set; }
        public int? RowCount { get; set; }
        public bool Truncated { get; set; }
        public int DurationMs { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Who reads a tool result. <see cref="Panel"/> is the in-panel analytics agent, whose output
    /// never changes shape. <see cref="Compact"/> is the Claude connector (MCP): the same figures,
    /// with the long definition text swapped for its short name, input echoes dropped and non-ASCII
    /// left unescaped — see <see cref="AgentToolCompactFormatter"/>.
    /// </summary>
    public enum AgentToolFormat
    {
        Panel,
        Compact
    }

    /// <summary>State one agent turn's tool calls share.</summary>
    public sealed class AgentToolContext
    {
        public int UserId { get; init; }
        public int SessionId { get; init; }
        public long MessageId { get; init; }

        public AgentToolFormat Format { get; init; } = AgentToolFormat.Panel;

        /// <summary>run_sql row cap; null = <see cref="AgentSqlRunner.MaxRows"/>.</summary>
        public int? MaxSqlRows { get; init; }
        public List<AgentToolTrailEntry> Trail { get; } = new();

        /// <summary>Charts and tables to show with the answer, in the order they were drawn.</summary>
        public JsonArray Parts { get; } = new();
    }

    public sealed record AgentToolResult(string Content, bool IsError);

    /// <summary>
    /// The analytics agent's tools. The CANONICAL ones call the very services the admin screens
    /// call — Statistics (<see cref="IStatisticsReportService"/>), Outgoing Payments → Admins
    /// (<see cref="IAdminPerformanceService"/>), Outgoing Payments' cleaner payroll
    /// (<see cref="IOutgoingPaymentService"/>, which runs <c>CleanerPayrollCalculator</c>), and the
    /// Order Sources and Customers reports (through their own controller actions, unchanged) — so a
    /// figure the agent states is the figure the panel shows. Grouping (by week, service type,
    /// admin ...) happens here in C#; nothing re-derives a formula.
    /// </summary>
    public sealed class AdminAgentTools
    {
        public const int MaxGroups = 400;

        /// <summary>
        /// The chart palette (--chart-cat-1..6) passes the colour-vision and normal-vision checks
        /// for six hues in both themes; slots 7-8 do not, so a chart never carries more series.
        /// </summary>
        public const int MaxChartSeries = 6;
        private const int MaxToolResultChars = 120_000;

        internal static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly IServiceProvider _services;
        private readonly ApplicationDbContext _context;
        private readonly IStatisticsReportService _statistics;
        private readonly IAdminPerformanceService _adminPerformance;
        private readonly IOutgoingPaymentService _outgoing;
        private readonly IAgentSqlRunner _sql;

        public AdminAgentTools(
            IServiceProvider services,
            ApplicationDbContext context,
            IStatisticsReportService statistics,
            IAdminPerformanceService adminPerformance,
            IOutgoingPaymentService outgoing,
            IAgentSqlRunner sql)
        {
            _services = services;
            _context = context;
            _statistics = statistics;
            _adminPerformance = adminPerformance;
            _outgoing = outgoing;
            _sql = sql;
        }

        /// <summary>A short label for the loading state while <paramref name="tool"/> runs.</summary>
        public static string ProgressLabel(string tool) => tool switch
        {
            "get_company_statistics" => "Reading the Statistics report…",
            "get_revenue_breakdown" => "Calculating revenue breakdown…",
            "get_admin_performance" => "Calculating admin performance…",
            "get_cleaner_payroll" => "Calculating payroll…",
            "get_order_sources" => "Reading order sources…",
            "get_customer_statistics" => "Reading customer statistics…",
            "lookup" => "Looking up names…",
            "run_sql" => "Running query…",
            "render_chart" => "Drawing chart…",
            "render_table" => "Building table…",
            _ => "Working…"
        };

        // ────────────────────────────────────────── definitions ──────────────────────────────────

        /// <summary>
        /// Tool definitions, built once and never reordered: the tool list is part of the cached
        /// prompt prefix, and it must stay byte-identical for the earlier turns' thinking to stay
        /// valid (the API checks that tools and system prompt are unchanged).
        /// </summary>
        public static JsonArray Definitions() => (JsonArray)JsonNode.Parse(DefinitionsJson)!;

        private static readonly string DefinitionsJson = BuildDefinitions().ToJsonString();

        private static JsonArray BuildDefinitions()
        {
            JsonObject Date(string what) => new() { ["type"] = "string", ["description"] = $"{what}, YYYY-MM-DD, inclusive (New York calendar day)." };
            JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
            JsonObject Int(string d) => new() { ["type"] = "integer", ["description"] = d };
            JsonObject Bool(string d) => new() { ["type"] = "boolean", ["description"] = d };
            JsonObject Enum(string d, params string[] values) => new() { ["type"] = "string", ["description"] = d, ["enum"] = new JsonArray(values.Select(v => (JsonNode)v!).ToArray()) };
            JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
            {
                ["name"] = name,
                ["description"] = description,
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JsonArray(required.Select(r => (JsonNode)r!).ToArray())
                }
            };

            JsonObject Period() => Enum("Bucket size when grouping by period. Weeks run Monday-Sunday.", "day", "week", "month");

            return new JsonArray
            {
                Tool("get_company_statistics",
                    "The Statistics / Finances page totals for a date range, exactly as the page computes them: orders, Company Revenue, sales tax (remitted and kept), tips, discounts, cleaner salaries, Stripe fees, staff bonuses, expenses by category, and Net Income (= 'Statistics Net Income'). Use for company-level money questions.",
                    new JsonObject
                    {
                        ["from"] = Date("First service date"),
                        ["to"] = Date("Last service date"),
                        ["include_upcoming"] = Bool("Projection: also count booked-and-paid cleanings not done yet. Default false (what the page shows by default).")
                    }, "from", "to"),

                Tool("get_revenue_breakdown",
                    "Statistics figures broken down per group: by period, service type, booking admin, recurring plan or not, commercial or not, payment method, new vs returning customer (one or two dimensions). Per group: orders, Company Revenue, sales tax, tips, discounts, cleaner pay, Stripe fees, staff bonuses, contribution before expenses, and per-order averages. Same order set and money rules as Statistics.",
                    new JsonObject
                    {
                        ["from"] = Date("First service date"),
                        ["to"] = Date("Last service date"),
                        ["group_by"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "One or two dimensions.",
                            ["items"] = Enum("Dimension.", "period", "service_type", "booking_admin", "recurring_plan", "commercial", "payment_method", "new_customer"),
                            ["minItems"] = 1,
                            ["maxItems"] = 2
                        },
                        ["period"] = Period(),
                        ["service_type_key"] = Str("Only orders of the service type with this ServiceKey (use lookup kind=service_types)."),
                        ["booking_admin_id"] = Int("Only orders booked by this admin (Order.BonusBookerId; use lookup kind=staff)."),
                        ["recurring_plan"] = Bool("true = only orders booked on a recurring plan, false = only one-time."),
                        ["commercial"] = Bool("true = only commercial orders, false = only regular."),
                        ["include_upcoming"] = Bool("Projection like the Statistics toggle. Default false.")
                    }, "from", "to", "group_by"),

                Tool("get_admin_performance",
                    "Outgoing Payments -> Admins figures: per Admin-role staff member, the orders they BOOKED (Order.BonusBookerId) that are Done and paid, Order total (incl. tax, excl. tips), sales tax, cleaner wages, Stripe fees, 'Admins-tab net' (total - tax - wages - Stripe fees) and, separately, the staff bonuses (not subtracted), with per-order averages and new/returning splits. Group by 'admin' (default) or break ONE admin down by period, service type or new/returning.",
                    new JsonObject
                    {
                        ["from"] = Date("First service date"),
                        ["to"] = Date("Last service date"),
                        ["admin_id"] = Int("Only this admin (agent_staff.user_id / lookup kind=staff)."),
                        ["group_by"] = Enum("Breakdown. Default admin.", "admin", "period", "service_type", "new_customer"),
                        ["period"] = Period()
                    }, "from", "to"),

                Tool("get_cleaner_payroll",
                    "Cleaner pay from Outgoing Payments (the payroll calculator: billable hours split across cleaners, per-cleaner overrides, unfilled slots): per cleaner hours, salary, tips, payout, paid and still owed. Optionally list per order.",
                    new JsonObject
                    {
                        ["from"] = Date("First service date"),
                        ["to"] = Date("Last service date"),
                        ["cleaner_id"] = Int("Only this cleaner (lookup kind=cleaners)."),
                        ["paid_status"] = Enum("Filter by payout state. Default all.", "all", "paid", "unpaid"),
                        ["group_by"] = Enum("Default cleaner.", "cleaner", "order")
                    }, "from", "to"),

                Tool("get_order_sources",
                    "Company -> Order Sources report: Done orders by acquisition source (Paid, Organic, Direct, Referral, Social, Email, AI, Admin-booked, Unknown) with revenue, over time.",
                    new JsonObject
                    {
                        ["from"] = Date("First service date"),
                        ["to"] = Date("Last service date"),
                        ["granularity"] = Enum("Bucket size. Default chosen from the range.", "day", "week", "month")
                    }, "from", "to"),

                Tool("get_customer_statistics",
                    "Company -> Customers report: active, new, returning, repeat customers, retention, order frequency and top customers by spend (names only) for a range.",
                    new JsonObject
                    {
                        ["from"] = Date("First service date"),
                        ["to"] = Date("Last service date")
                    }, "from", "to"),

                Tool("lookup",
                    "Resolve names to ids/keys: staff (admins, with role and position), service types (with ServiceKey), cleaners, subscriptions (plan tiers).",
                    new JsonObject
                    {
                        ["kind"] = Enum("What to list.", "staff", "service_types", "cleaners", "subscriptions"),
                        ["search"] = Str("Optional case-insensitive name filter (Latin or Georgian spelling as stored).")
                    }, "kind"),

                Tool("run_sql",
                    "Read-only MariaDB SELECT over the agent_* views ONLY, for questions no canonical tool covers. One statement starting with SELECT or WITH, no comments, max 1000 rows, 10 second limit. Aggregate IN SQL (COUNT/SUM/AVG/GROUP BY) - never pull raw rows to add up yourself. Money from plain sums is NOT reconciled with the panel; say so.",
                    new JsonObject
                    {
                        ["sql"] = Str("The SELECT statement."),
                        ["purpose"] = Str("One line: what this query answers (shown to the user).")
                    }, "sql", "purpose"),

                Tool("render_chart",
                    "Show a chart in the answer. Use numbers exactly as tools returned them. At most 6 series (6 slices for a pie): fold the smallest into one \"Other\" entry computed by a tool, or use render_table instead. Never two y-axes - two measures of different scale are two charts.",
                    new JsonObject
                    {
                        ["chart_type"] = Enum("Kind of chart.", "bar", "line", "pie", "stacked_bar"),
                        ["title"] = Str("Chart title."),
                        ["labels"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "X-axis labels (or pie slices)." },
                        ["series"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "One or more series; each data array has one number per label. A pie chart takes exactly one series. At most 6 series.",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["name"] = new JsonObject { ["type"] = "string" },
                                    ["data"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" } }
                                },
                                ["required"] = new JsonArray("name", "data")
                            }
                        },
                        ["format"] = Enum("How values are formatted. Percent values are percentages: 12.5 means 12.5%.", "currency", "count", "percent", "number"),
                        ["x_label"] = Str("Optional x-axis title."),
                        ["y_label"] = Str("Optional y-axis title.")
                    }, "chart_type", "title", "labels", "series", "format"),

                Tool("render_table",
                    "Show a table in the answer (for lists and multi-column results). Use values exactly as tools returned them.",
                    new JsonObject
                    {
                        ["title"] = Str("Table title."),
                        ["columns"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["key"] = new JsonObject { ["type"] = "string" },
                                    ["label"] = new JsonObject { ["type"] = "string" },
                                    ["format"] = Enum("Cell format.", "text", "currency", "count", "percent", "number", "date")
                                },
                                ["required"] = new JsonArray("key", "label")
                            }
                        },
                        ["rows"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "Objects keyed by column key.",
                            ["items"] = new JsonObject { ["type"] = "object" }
                        }
                    }, "title", "columns", "rows")
            };
        }

        // ────────────────────────────────────────── execution ────────────────────────────────────

        public async Task<AgentToolResult> ExecuteAsync(string name, JsonElement input, AgentToolContext ctx, CancellationToken ct)
        {
            var entry = new AgentToolTrailEntry
            {
                Tool = name,
                Label = ProgressLabel(name).TrimEnd('…'),
                Input = JsonNode.Parse(input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText())
            };
            var started = DateTime.UtcNow;
            AgentToolResult result;
            try
            {
                result = name switch
                {
                    "get_company_statistics" => await CompanyStatisticsAsync(input, entry),
                    "get_revenue_breakdown" => await RevenueBreakdownAsync(input, entry),
                    "get_admin_performance" => await AdminPerformanceAsync(input, entry),
                    "get_cleaner_payroll" => await CleanerPayrollAsync(input, entry),
                    "get_order_sources" => await OrderSourcesAsync(input, entry),
                    "get_customer_statistics" => await CustomerStatisticsAsync(input, entry),
                    "lookup" => await LookupAsync(input, entry),
                    "run_sql" => await RunSqlAsync(input, entry, ctx, ct),
                    "render_chart" => RenderChart(input, ctx),
                    "render_table" => RenderTable(input, ctx),
                    _ => Error($"Unknown tool: {name}")
                };
            }
            catch (ToolInputException ex)
            {
                result = Error(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                result = Error(ex.Message);
            }

            if (ctx.Format == AgentToolFormat.Compact && !result.IsError)
                result = new AgentToolResult(AgentToolCompactFormatter.Format(result.Content, entry.Definition), false);

            entry.DurationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            if (result.IsError)
                entry.Error = result.Content;
            // Charts and tables are presentation; the trail lists what was CALCULATED.
            if (name is not ("render_chart" or "render_table"))
                ctx.Trail.Add(entry);
            return result;
        }

        private static AgentToolResult Ok(object payload)
        {
            var json = JsonSerializer.Serialize(payload, Json);
            if (json.Length > MaxToolResultChars)
                json = json[..MaxToolResultChars] + "\n[RESULT CUT OFF: too large. Narrow the range or group by fewer dimensions.]";
            return new AgentToolResult(json, false);
        }

        private static AgentToolResult Error(string message) => new(message, true);

        // ── Statistics ───────────────────────────────────────────────────────────────────────────

        private const string StatisticsDefinition =
            "Statistics (Statistics / Finances page): Done orders (and Done-then-refunded ones for their wage cost), paid, by service date. Company Revenue = discounted subtotal net of refunds + sales tax kept on payments outside Stripe; Net Income = Company Revenue - cleaner salaries - expenses (Stripe fees, staff bonuses, expense table). Sales tax and tips are pass-through and not subtracted.";

        private async Task<AgentToolResult> CompanyStatisticsAsync(JsonElement input, AgentToolTrailEntry entry)
        {
            var (from, to) = RequiredRange(input);
            var upcoming = OptBool(input, "include_upcoming") ?? false;
            entry.DateRange = RangeText(from, to);
            entry.Definition = "Statistics Net Income";
            if (upcoming) entry.Filters.Add("Projection: includes booked-and-paid cleanings not done yet");

            var s = await _statistics.GetOrderStatisticsAsync(from, to, upcoming);
            return Ok(new
            {
                definition = StatisticsDefinition,
                date_range = entry.DateRange,
                includes_upcoming = s.IncludesUpcoming,
                orders = s.TotalOrders,
                company_revenue = s.TotalAmount,
                sales_tax_remitted = s.TotalTaxes,
                sales_tax_kept_outside_stripe_included_in_revenue = s.TotalTaxRetained,
                tips_pass_through = s.TotalTips,
                discounts_given = s.TotalDiscounts,
                cleaner_salaries = s.TotalCleanersSalary,
                company_revenue_after_cleaner_salaries = s.TotalCompanyRevenueGross,
                stripe_fees = s.StripeFees,
                staff_bonuses_usd = s.AdminBonusesUsd,
                staff_bonuses_gel = s.AdminBonusesGel,
                expense_table_total = s.ExpensesBreakdown?.Total ?? 0m,
                expenses_by_category = s.ExpensesBreakdown?.ByCategory.Select(c => new { category = c.CategoryName, total = c.Total }),
                total_expenses = s.TotalExpenses,
                statistics_net_income = s.TotalCompanyRevenue,
                average_company_revenue_per_order = Avg(s.TotalAmount, s.TotalOrders),
                average_cleaner_salary_per_order = Avg(s.TotalCleanersSalary, s.TotalOrders),
                average_net_income_per_order = Avg(s.TotalCompanyRevenue, s.TotalOrders),
                unfinished_cleanings_in_range = s.UpcomingOrders
            });
        }

        private async Task<AgentToolResult> RevenueBreakdownAsync(JsonElement input, AgentToolTrailEntry entry)
        {
            var (from, to) = RequiredRange(input);
            var dims = StringArray(input, "group_by");
            if (dims.Count is < 1 or > 2)
                throw new ToolInputException("group_by needs one or two dimensions.");
            var allowed = new[] { "period", "service_type", "booking_admin", "recurring_plan", "commercial", "payment_method", "new_customer" };
            foreach (var d in dims)
                if (!allowed.Contains(d)) throw new ToolInputException($"Unknown group_by '{d}'. Use: {string.Join(", ", allowed)}.");
            var period = OptString(input, "period") ?? "month";
            var serviceKey = OptString(input, "service_type_key");
            var adminId = OptInt(input, "booking_admin_id");
            var recurring = OptBool(input, "recurring_plan");
            var commercial = OptBool(input, "commercial");
            var upcoming = OptBool(input, "include_upcoming") ?? false;

            entry.DateRange = RangeText(from, to);
            entry.Definition = "Statistics (contribution before table expenses)";
            entry.Filters.Add("Grouped by " + string.Join(" + ", dims) + (dims.Contains("period") ? $" ({period})" : ""));
            if (serviceKey != null) entry.Filters.Add($"Service type key = {serviceKey}");
            if (adminId != null) entry.Filters.Add($"Booking admin (BonusBookerId) = {adminId}");
            if (recurring != null) entry.Filters.Add(recurring.Value ? "Recurring plan only" : "One-time only");
            if (commercial != null) entry.Filters.Add(commercial.Value ? "Commercial only" : "Regular only");
            if (upcoming) entry.Filters.Add("Projection: includes booked-and-paid cleanings not done yet");

            var report = await _statistics.GetOrderRowsAsync(from, to, upcoming);
            var dimsById = await LoadOrderDimensionsAsync(report.Orders.Select(r => r.OrderId).ToList());

            var rows = report.Orders.Where(r =>
            {
                var d = dimsById[r.OrderId];
                return (serviceKey == null || string.Equals(d.ServiceTypeKey, serviceKey, StringComparison.OrdinalIgnoreCase))
                    && (adminId == null || d.BookingAdminId == adminId)
                    && (recurring == null || d.IsRecurringPlan == recurring)
                    && (commercial == null || d.IsCommercial == commercial);
            }).ToList();

            if (serviceKey != null && !dimsById.Values.Any(d => string.Equals(d.ServiceTypeKey, serviceKey, StringComparison.OrdinalIgnoreCase)))
                entry.Filters.Add($"(No order in range has service type key '{serviceKey}')");

            var adminNames = await StaffNamesAsync();
            string Key(StatisticsOrderRow r, string dim)
            {
                var d = dimsById[r.OrderId];
                return dim switch
                {
                    "period" => PeriodKey(r.ServiceDate, period),
                    "service_type" => d.ServiceTypeLabel,
                    "booking_admin" => d.BookingAdminId is { } id ? adminNames.GetValueOrDefault(id, $"Staff #{id}") : "No booking admin (customer booked / unassigned)",
                    "recurring_plan" => d.IsRecurringPlan ? "Recurring plan" : "One-time",
                    "commercial" => d.IsCommercial ? "Commercial" : "Regular",
                    "payment_method" => d.PaymentMethod,
                    "new_customer" => d.IsNewCustomerOrder ? "New customer" : "Returning customer",
                    _ => "?"
                };
            }

            var groups = rows
                .GroupBy(r => string.Join(" | ", dims.Select(dim => Key(r, dim))))
                .Select(g => (Key: g.Key, Metrics: StatisticsMetrics(g.ToList())))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            if (groups.Count > MaxGroups)
                throw new ToolInputException($"That makes {groups.Count} groups. Use a larger period or fewer dimensions.");

            // Commercial-invoice money is reported by PAYMENT date with no order behind it, so it can
            // only be placed in a period bucket - and only when no order filter applies.
            var orderFilterApplied = serviceKey != null || adminId != null || recurring != null || commercial != null;
            object? invoiceReceipts = null;
            if (report.InvoiceReceipts.Count > 0)
            {
                invoiceReceipts = orderFilterApplied || !(dims.Count == 1 && dims[0] == "period")
                    ? new
                    {
                        note = "Commercial-invoice payments in the range are counted by Statistics by payment date and belong to no single order, so they are NOT inside the groups above.",
                        revenue = report.InvoiceReceipts.Sum(r => r.Revenue),
                        sales_tax = report.InvoiceReceipts.Sum(r => r.Tax)
                    }
                    : report.InvoiceReceipts
                        .GroupBy(r => PeriodKey(r.Date, period))
                        .OrderBy(g => g.Key, StringComparer.Ordinal)
                        .Select(g => (object)new { group = g.Key, revenue = g.Sum(r => r.Revenue), sales_tax = g.Sum(r => r.Tax) })
                        .ToList();
            }

            return Ok(new
            {
                definition = StatisticsDefinition + " Per group, 'contribution_before_expenses' = Company Revenue - cleaner pay - Stripe fees - staff bonuses (the expense table is company-wide and is not split per group; use get_company_statistics for Statistics Net Income). Staff bonuses here are rounded per order (as on the daily chart), so they can differ from the totals card by a few cents.",
                date_range = entry.DateRange,
                grouped_by = dims,
                period = dims.Contains("period") ? period : null,
                filters = entry.Filters.Skip(1),
                groups = groups.Select(g => new { group = g.Key, metrics = g.Metrics }),
                total_of_groups = StatisticsMetrics(rows),
                commercial_invoice_receipts = invoiceReceipts
            });
        }

        private static object StatisticsMetrics(List<StatisticsOrderRow> rows)
        {
            var orders = rows.Count(r => r.CountsAsOrder);
            var revenue = rows.Sum(r => r.Revenue);
            var pay = rows.Sum(r => r.CleanerSalary);
            var fees = rows.Sum(r => r.StripeFee);
            var bonuses = rows.Sum(r => r.AdminBonusUsd);
            var contribution = revenue - pay - fees - bonuses;
            return new
            {
                orders,
                fully_refunded_orders_kept_for_wage_cost = rows.Count(r => !r.CountsAsOrder),
                invoice_billed_orders_money_in_receipts = rows.Count(r => r.InvoiceLinked),
                company_revenue = revenue,
                sales_tax_remitted = rows.Sum(r => r.Tax),
                sales_tax_kept_included_in_revenue = rows.Sum(r => r.TaxRetained),
                tips_pass_through = rows.Sum(r => r.Tips),
                discounts_given = rows.Sum(r => r.Discounts),
                cleaner_pay = pay,
                stripe_fees = fees,
                staff_bonuses_usd = bonuses,
                contribution_before_expenses = contribution,
                average_company_revenue_per_order = Avg(revenue, orders),
                average_cleaner_pay_per_order = Avg(pay, orders),
                average_contribution_per_order = Avg(contribution, orders)
            };
        }

        // ── Outgoing Payments → Admins ───────────────────────────────────────────────────────────

        private const string AdminsDefinition =
            "Admins-tab (Outgoing Payments -> Admins): orders whose BOOKING admin (Order.BonusBookerId, Admin role only) is the person, Done AND paid, by service date. Order total = price incl. sales tax, excl. tips, net of refunds. Admins-tab net = Order total - sales tax - cleaner wages - Stripe fees (all sales tax is subtracted here, unlike Statistics). Staff bonuses are reported separately and NOT subtracted.";

        private async Task<AgentToolResult> AdminPerformanceAsync(JsonElement input, AgentToolTrailEntry entry)
        {
            var (from, to) = RequiredRange(input);
            var adminId = OptInt(input, "admin_id");
            var groupBy = OptString(input, "group_by") ?? "admin";
            var period = OptString(input, "period") ?? "month";
            entry.DateRange = RangeText(from, to);
            entry.Definition = "Admins-tab net";
            if (adminId != null) entry.Filters.Add($"Booking admin (BonusBookerId) = {adminId}");
            entry.Filters.Add("Grouped by " + groupBy + (groupBy == "period" ? $" ({period})" : ""));

            if (groupBy == "admin")
            {
                var list = await _adminPerformance.GetAsync(from, to);
                var admins = list.Admins.Where(a => adminId == null || a.AdminId == adminId).ToList();
                if (adminId != null && admins.Count == 0)
                    return Error($"No Admin-role staff member with id {adminId}. Only Admin-role staff are measured on this tab (SuperAdmins earn no bonus). Use lookup kind=staff.");
                return Ok(new
                {
                    definition = AdminsDefinition,
                    date_range = entry.DateRange,
                    admins = admins.Select(a => new
                    {
                        admin_id = a.AdminId,
                        name = $"{a.FirstName} {a.LastName}".Trim(),
                        position = a.Position,
                        is_active = a.IsActive,
                        completed_orders = a.CompletedOrders,
                        orders_total_incl_tax = a.OrdersTotal,
                        sales_tax = a.SalesTax,
                        revenue_excl_tax = a.OrdersTotal - a.SalesTax,
                        cleaner_wages = a.CleanerWages,
                        stripe_fees = a.StripeFees,
                        admins_tab_net = a.NetIncome,
                        staff_bonuses_usd_not_subtracted = a.StaffBonuses,
                        average_order_total_per_order = Avg(a.OrdersTotal, a.CompletedOrders),
                        average_cleaner_wages_per_order = Avg(a.CleanerWages, a.CompletedOrders),
                        average_sales_tax_per_order = Avg(a.SalesTax, a.CompletedOrders),
                        average_net_per_order = Avg(a.NetIncome, a.CompletedOrders),
                        new_customer_orders = a.NewCustomerOrders,
                        returning_customer_orders = a.ReturningCustomerOrders,
                        net_new_customers = a.NetIncomeNew,
                        net_returning_customers = a.NetIncomeReturning
                    })
                });
            }

            if (groupBy is not ("period" or "service_type" or "new_customer"))
                throw new ToolInputException("group_by must be admin, period, service_type or new_customer.");

            var rows = await _adminPerformance.GetOrderRowsAsync(from, to);
            if (adminId != null) rows = rows.Where(r => r.BookerId == adminId).ToList();
            var dimsById = await LoadOrderDimensionsAsync(rows.Select(r => r.OrderId).ToList());

            var groups = rows
                .GroupBy(r => groupBy switch
                {
                    "period" => PeriodKey(r.ServiceDate, period),
                    "service_type" => dimsById[r.OrderId].ServiceTypeLabel,
                    _ => r.IsNewCustomerOrder ? "New customer" : "Returning customer"
                })
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { group = g.Key, metrics = AdminMetrics(g.ToList()) })
                .ToList();

            return Ok(new
            {
                definition = AdminsDefinition,
                date_range = entry.DateRange,
                admin_id = adminId,
                grouped_by = groupBy,
                groups,
                total_of_groups = AdminMetrics(rows)
            });
        }

        private static object AdminMetrics(List<AdminPerformanceOrderRow> rows)
        {
            var n = rows.Count;
            var total = rows.Sum(r => r.OrderTotal);
            var tax = rows.Sum(r => r.SalesTax);
            var wages = rows.Sum(r => r.CleanerWages);
            var fees = rows.Sum(r => r.StripeFee);
            var bonuses = decimal.Round(rows.Sum(r => r.StaffBonusesUsd), 2);
            var net = decimal.Round(rows.Sum(r => r.Net), 2);
            return new
            {
                completed_orders = n,
                orders_total_incl_tax = total,
                sales_tax = tax,
                revenue_excl_tax = total - tax,
                cleaner_wages = wages,
                stripe_fees = fees,
                admins_tab_net = net,
                staff_bonuses_usd_not_subtracted = bonuses,
                average_order_total_per_order = Avg(total, n),
                average_sales_tax_per_order = Avg(tax, n),
                average_cleaner_wages_per_order = Avg(wages, n),
                average_net_per_order = Avg(net, n)
            };
        }

        // ── Cleaner payroll (Outgoing Payments) ──────────────────────────────────────────────────

        private async Task<AgentToolResult> CleanerPayrollAsync(JsonElement input, AgentToolTrailEntry entry)
        {
            var (from, to) = RequiredRange(input);
            var cleanerId = OptInt(input, "cleaner_id");
            var paid = (OptString(input, "paid_status") ?? "all").ToLowerInvariant();
            var groupBy = OptString(input, "group_by") ?? "cleaner";
            entry.DateRange = RangeText(from, to);
            entry.Definition = "Outgoing Payments cleaner payroll (CleanerPayrollCalculator)";
            if (cleanerId != null) entry.Filters.Add($"Cleaner = {cleanerId}");
            if (paid != "all") entry.Filters.Add($"Payout status = {paid}");

            var status = paid switch
            {
                "paid" => OutgoingPaymentPaidStatus.Paid,
                "unpaid" => OutgoingPaymentPaidStatus.Unpaid,
                _ => OutgoingPaymentPaidStatus.All
            };

            var orders = new List<OutgoingPaymentOrderDto>();
            OutgoingPaymentSummaryDto? summary = null;
            for (var page = 1; page <= 50; page++)
            {
                var result = await _outgoing.GetAsync(new OutgoingPaymentQuery
                {
                    From = from, To = to, PaidStatus = status, CleanerId = cleanerId, Page = page, PageSize = 200
                });
                summary ??= result.Summary;
                orders.AddRange(result.Orders);
                if (orders.Count >= result.TotalCount || result.Orders.Count == 0) break;
            }

            var lines = orders.SelectMany(o => o.Cleaners.Concat(o.UnassignedCleaners).Select(l => (Order: o, Line: l)))
                .Where(x => cleanerId == null || x.Line.CleanerId == cleanerId)
                .ToList();

            object payload = groupBy == "order"
                ? orders.Take(300).Select(o => new
                {
                    order_id = o.OrderId,
                    service_date = o.ServiceDate.ToString("yyyy-MM-dd"),
                    service_type = o.ServiceTypeName,
                    status = o.Status,
                    order_total_without_tips = o.TotalWithoutTips,
                    cleaner_total_salary = o.TotalSalary,
                    total_payout = o.TotalPayout,
                    split_count = o.SplitCount,
                    lines = o.Cleaners.Concat(o.UnassignedCleaners).Select(PayLine)
                }).ToList()
                : lines
                    .GroupBy(x => x.Line.IsUnassigned ? -1 : x.Line.CleanerId)
                    .Select(g =>
                    {
                        var first = g.First().Line;
                        return new
                        {
                            cleaner_id = first.IsUnassigned ? (int?)null : first.CleanerId,
                            name = first.IsUnassigned ? "Unfilled cleaner slots" : $"{first.FirstName} {first.LastName}".Trim(),
                            orders = g.Select(x => x.Order.OrderId).Distinct().Count(),
                            hours = decimal.Round(g.Sum(x => x.Line.BillableMinutes) / 60m, 2),
                            salary = g.Sum(x => x.Line.Salary),
                            tips = g.Sum(x => x.Line.Tips),
                            payout = g.Sum(x => x.Line.Payout),
                            paid_out = g.Sum(x => x.Line.PaidAmount ?? 0m),
                            still_owed = g.Sum(x => x.Line.OutstandingPayout),
                            average_salary_per_order = Avg(g.Sum(x => x.Line.Salary), g.Select(x => x.Order.OrderId).Distinct().Count())
                        };
                    })
                    .OrderByDescending(x => x.salary)
                    .ToList();

            return Ok(new
            {
                definition = "Cleaner pay as Outgoing Payments computes it: each order's billable hours are split across max(cleaners priced, cleaners assigned) by the payroll calculator, honouring per-cleaner hour and rate overrides; unfilled slots are paid too. Tips are passed through to cleaners.",
                date_range = entry.DateRange,
                summary = summary == null ? null : new
                {
                    orders = summary.OrderCount,
                    cleaner_lines = summary.CleanerLineCount,
                    total_salary = summary.TotalSalary,
                    total_tips = summary.TotalTips,
                    total_payout = summary.TotalPayout,
                    paid_payout = summary.PaidPayout,
                    still_owed = summary.UnpaidPayout,
                    average_salary_per_order = Avg(summary.TotalSalary, summary.OrderCount)
                },
                grouped_by = groupBy,
                rows = payload,
                orders_listed_truncated = groupBy == "order" && orders.Count > 300
            });
        }

        // Payout details (bank / Zelle handles) are deliberately never passed to the model.
        private static object PayLine(OutgoingPaymentCleanerDto l) => new
        {
            cleaner_id = l.IsUnassigned ? (int?)null : l.CleanerId,
            name = l.IsUnassigned ? "Unfilled slot" : $"{l.FirstName} {l.LastName}".Trim(),
            hours = decimal.Round(l.BillableMinutes / 60m, 2),
            hourly_rate = l.HourlyRate,
            salary = l.Salary,
            tips = l.Tips,
            payout = l.Payout,
            is_paid = l.IsPaid,
            still_owed = l.OutstandingPayout
        };

        // ── Order Sources / Customers (their own controller actions, unchanged) ──────────────────

        private async Task<AgentToolResult> OrderSourcesAsync(JsonElement input, AgentToolTrailEntry entry)
        {
            var (from, to) = RequiredRange(input);
            var granularity = OptString(input, "granularity");
            entry.DateRange = RangeText(from, to);
            entry.Definition = "Order Sources report (Done, not hidden, by service date)";
            var controller = ActivatorUtilities.CreateInstance<AdminOrderSourcesController>(_services);
            var report = Unwrap(await controller.GetOrderSources(from, to, granularity));
            return Ok(new { definition = "Company -> Order Sources: Done orders not hidden in the admin list, by service date; revenue = Statistics' Company Revenue per order.", report });
        }

        private async Task<AgentToolResult> CustomerStatisticsAsync(JsonElement input, AgentToolTrailEntry entry)
        {
            var (from, to) = RequiredRange(input);
            entry.DateRange = RangeText(from, to);
            entry.Definition = "Customers report";
            var controller = ActivatorUtilities.CreateInstance<AdminCustomerStatsController>(_services);
            var report = Unwrap(await controller.GetCustomerStatistics(from, to));
            // Names only: the agent never handles customer e-mail addresses.
            foreach (var c in report.TopCustomers)
                c.Email = string.Empty;
            return Ok(new { definition = "Company -> Customers: Active / New / Returning / Repeat are distinct definitions - quote the one asked for.", report });
        }

        private static T Unwrap<T>(ActionResult<T> result) where T : class
        {
            if (result.Value != null) return result.Value;
            if (result.Result is ObjectResult { Value: T value } && result.Result is OkObjectResult) return value;
            var message = (result.Result as ObjectResult)?.Value is { } body
                ? body.GetType().GetProperty("message")?.GetValue(body)?.ToString()
                : null;
            throw new ToolInputException(message ?? "The report could not be produced for that input.");
        }

        // ── Lookup ───────────────────────────────────────────────────────────────────────────────

        private async Task<AgentToolResult> LookupAsync(JsonElement input, AgentToolTrailEntry entry)
        {
            var kind = OptString(input, "kind") ?? throw new ToolInputException("kind is required.");
            var search = OptString(input, "search")?.Trim();
            entry.Filters.Add($"Kind = {kind}" + (search != null ? $", search = {search}" : ""));

            bool Match(params string?[] parts) =>
                string.IsNullOrEmpty(search) || parts.Any(p => p != null && p.Contains(search, StringComparison.OrdinalIgnoreCase));

            switch (kind)
            {
                case "staff":
                    var staff = await _context.Users
                        .Where(u => u.Role == UserRole.SuperAdmin || u.Role == UserRole.Admin || u.Role == UserRole.Moderator)
                        .Select(u => new { u.Id, u.FirstName, u.LastName, u.Role, u.AdminPosition, u.IsActive, u.IsDeleted })
                        .ToListAsync();
                    return Ok(staff.Where(u => Match(u.FirstName, u.LastName, $"{u.FirstName} {u.LastName}"))
                        .Select(u => new { user_id = u.Id, name = $"{u.FirstName} {u.LastName}".Trim(), role = u.Role.ToString(), position = u.AdminPosition.ToString(), is_active = u.IsActive, is_deleted = u.IsDeleted }));
                case "service_types":
                    var types = await _context.ServiceTypes.Select(t => new { t.Id, t.Name, t.ServiceKey, t.IsActive, t.IsCustom }).ToListAsync();
                    return Ok(types.Where(t => Match(t.Name, t.ServiceKey))
                        .Select(t => new { service_type_id = t.Id, service_type_key = t.ServiceKey, name = t.Name, is_active = t.IsActive, is_custom = t.IsCustom, note = t.ServiceKey == null ? "No ServiceKey set: it cannot be filtered by key; group by service type instead." : null }));
                case "cleaners":
                    var cleaners = await _context.Cleaners.Select(c => new { c.Id, c.FirstName, c.LastName, c.IsActive }).ToListAsync();
                    return Ok(cleaners.Where(c => Match(c.FirstName, c.LastName, $"{c.FirstName} {c.LastName}"))
                        .Select(c => new { cleaner_id = c.Id, name = $"{c.FirstName} {c.LastName}".Trim(), is_active = c.IsActive }));
                case "subscriptions":
                    var subs = await _context.Subscriptions.Select(s => new { s.Id, s.Name, s.SubscriptionDays, s.DiscountPercentage, s.IsActive }).ToListAsync();
                    return Ok(subs.Where(s => Match(s.Name))
                        .Select(s => new { subscription_id = s.Id, name = s.Name, subscription_days = s.SubscriptionDays, is_recurring = RecurringPlanRule.IsRecurringTier(s.SubscriptionDays), discount_percentage = s.DiscountPercentage, is_active = s.IsActive }));
                default:
                    throw new ToolInputException("kind must be staff, service_types, cleaners or subscriptions.");
            }
        }

        // ── SQL ──────────────────────────────────────────────────────────────────────────────────

        private async Task<AgentToolResult> RunSqlAsync(JsonElement input, AgentToolTrailEntry entry, AgentToolContext ctx, CancellationToken ct)
        {
            var sql = OptString(input, "sql") ?? throw new ToolInputException("sql is required.");
            var purpose = OptString(input, "purpose");
            entry.Sql = sql;
            entry.Definition = "Ad-hoc SQL over agent_* views (raw, not reconciled with the panel)";
            if (!string.IsNullOrWhiteSpace(purpose)) entry.Filters.Add(purpose!);

            var maxRows = ctx.MaxSqlRows ?? AgentSqlRunner.MaxRows;
            var result = await _sql.RunAsync(sql, ctx.UserId, ctx.SessionId, ctx.MessageId, ct, maxRows);
            entry.Sql = result.ExecutedSql ?? sql;
            if (!result.Ok)
                return Error(result.Error ?? "The query failed.");

            entry.RowCount = result.Rows.Count;
            entry.Truncated = result.Truncated;

            // Keep the result inside the tool budget: rows are dropped from the end, and the model
            // is told exactly how many it is seeing.
            var sent = result.Rows;
            string Serialize(List<object?[]> rows) => JsonSerializer.Serialize(new
            {
                columns = result.Columns,
                rows,
                row_count = rows.Count,
                truncated = result.Truncated || rows.Count < result.Rows.Count,
                note = result.Truncated
                    ? $"Only the first {maxRows} rows were read - the result is INCOMPLETE. Aggregate in SQL instead of listing rows."
                    : rows.Count < result.Rows.Count
                        ? $"Only the first {rows.Count} of {result.Rows.Count} rows fit in the reply - aggregate in SQL instead of listing rows."
                        : null
            }, Json);
            var json = Serialize(sent);
            while (json.Length > MaxToolResultChars && sent.Count > 10)
            {
                sent = sent.Take(sent.Count / 2).ToList();
                json = Serialize(sent);
            }
            entry.Truncated |= sent.Count < result.Rows.Count;
            return new AgentToolResult(json, false);
        }

        // ── Presentation ─────────────────────────────────────────────────────────────────────────

        private static AgentToolResult RenderChart(JsonElement input, AgentToolContext ctx)
        {
            var type = OptString(input, "chart_type");
            if (type is not ("bar" or "line" or "pie" or "stacked_bar"))
                return Error("chart_type must be bar, line, pie or stacked_bar.");
            var labels = StringArray(input, "labels");
            if (labels.Count is 0 or > 400)
                return Error("labels must have 1 to 400 entries.");
            if (!input.TryGetProperty("series", out var seriesEl) || seriesEl.ValueKind != JsonValueKind.Array || seriesEl.GetArrayLength() is 0 or > MaxChartSeries)
                return Error($"series must have 1 to {MaxChartSeries} entries - fold the rest into \"Other\" or use render_table.");
            if (type == "pie" && seriesEl.GetArrayLength() != 1)
                return Error("A pie chart takes exactly one series.");
            if (type == "pie" && labels.Count > MaxChartSeries)
                return Error($"A pie chart takes at most {MaxChartSeries} slices - fold the smallest into \"Other\" or use a bar chart.");

            var series = new JsonArray();
            foreach (var s in seriesEl.EnumerateArray())
            {
                var name = s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (!s.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return Error("Every series needs a data array.");
                var values = new JsonArray();
                foreach (var v in data.EnumerateArray())
                {
                    if (v.ValueKind == JsonValueKind.Number) values.Add(v.GetDecimal());
                    else if (v.ValueKind == JsonValueKind.Null) values.Add(null);
                    else return Error("Series data must be numbers.");
                }
                if (values.Count != labels.Count)
                    return Error($"Series '{name}' has {values.Count} values for {labels.Count} labels.");
                series.Add(new JsonObject { ["name"] = name ?? "Series", ["data"] = values });
            }

            var format = OptString(input, "format");
            ctx.Parts.Add(new JsonObject
            {
                ["type"] = "chart",
                ["chart_type"] = type,
                ["title"] = OptString(input, "title") ?? string.Empty,
                ["labels"] = new JsonArray(labels.Select(l => (JsonNode)l!).ToArray()),
                ["series"] = series,
                ["format"] = format is "currency" or "count" or "percent" or "number" ? format : "number",
                ["x_label"] = OptString(input, "x_label"),
                ["y_label"] = OptString(input, "y_label")
            });
            return new AgentToolResult("Chart shown to the user.", false);
        }

        private static AgentToolResult RenderTable(JsonElement input, AgentToolContext ctx)
        {
            if (!input.TryGetProperty("columns", out var colsEl) || colsEl.ValueKind != JsonValueKind.Array || colsEl.GetArrayLength() is 0 or > 20)
                return Error("columns must have 1 to 20 entries.");
            if (!input.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                return Error("rows must be an array.");
            if (rowsEl.GetArrayLength() > 500)
                return Error("At most 500 rows - summarise first.");

            var columns = new JsonArray();
            var keys = new List<string>();
            foreach (var c in colsEl.EnumerateArray())
            {
                var key = c.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                if (string.IsNullOrWhiteSpace(key)) return Error("Every column needs a key.");
                var label = c.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : key;
                var format = c.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : "text";
                keys.Add(key!);
                columns.Add(new JsonObject { ["key"] = key, ["label"] = label, ["format"] = format is "currency" or "count" or "percent" or "number" or "date" ? format : "text" });
            }

            var rows = new JsonArray();
            foreach (var r in rowsEl.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) return Error("Every row must be an object keyed by column key.");
                var row = new JsonObject();
                foreach (var key in keys)
                    row[key] = r.TryGetProperty(key, out var v) ? JsonNode.Parse(v.GetRawText()) : null;
                rows.Add(row);
            }

            ctx.Parts.Add(new JsonObject
            {
                ["type"] = "table",
                ["title"] = OptString(input, "title") ?? string.Empty,
                ["columns"] = columns,
                ["rows"] = rows
            });
            return new AgentToolResult("Table shown to the user.", false);
        }

        // ── Shared helpers ───────────────────────────────────────────────────────────────────────

        private sealed record OrderDimensions(
            string ServiceTypeLabel, string? ServiceTypeKey, int? BookingAdminId, bool IsRecurringPlan,
            bool IsCommercial, string PaymentMethod, bool IsNewCustomerOrder);

        private async Task<Dictionary<int, OrderDimensions>> LoadOrderDimensionsAsync(List<int> orderIds)
        {
            var result = new Dictionary<int, OrderDimensions>();
            foreach (var chunk in orderIds.Distinct().Chunk(2000))
            {
                var ids = chunk.ToList();
                var rows = await _context.Orders
                    .Where(o => ids.Contains(o.Id))
                    .Select(o => new
                    {
                        o.Id, o.ServiceTypeId, TypeName = o.ServiceType.Name, TypeKey = o.ServiceType.ServiceKey,
                        o.BonusBookerId, o.SubscriptionId, Days = (int?)o.Subscription!.SubscriptionDays,
                        o.IsCommercial, o.PaymentMethod, o.IsNewCustomerOrder
                    })
                    .ToListAsync();
                foreach (var o in rows)
                    result[o.Id] = new OrderDimensions(
                        o.TypeKey != null ? $"{o.TypeName} [{o.TypeKey}]" : $"{o.TypeName} [no key, type #{o.ServiceTypeId}]",
                        o.TypeKey, o.BonusBookerId, RecurringPlanRule.IsRecurringOrder(o.SubscriptionId, o.Days),
                        o.IsCommercial, o.PaymentMethod.ToString(), o.IsNewCustomerOrder);
            }
            return result;
        }

        private async Task<Dictionary<int, string>> StaffNamesAsync() =>
            (await _context.Users
                .Where(u => u.Role == UserRole.SuperAdmin || u.Role == UserRole.Admin || u.Role == UserRole.Moderator)
                .Select(u => new { u.Id, u.FirstName, u.LastName })
                .ToListAsync())
            .ToDictionary(u => u.Id, u => $"{u.FirstName} {u.LastName}".Trim());

        /// <summary>A sortable bucket label. Weeks start on Monday and carry their date span.</summary>
        public static string PeriodKey(DateTime date, string period)
        {
            var d = date.Date;
            switch (period)
            {
                case "day":
                    return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case "week":
                    var monday = d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
                    return $"{monday:yyyy-MM-dd} to {monday.AddDays(6):yyyy-MM-dd}";
                default:
                    return d.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            }
        }

        private static decimal? Avg(decimal total, int count) =>
            count > 0 ? decimal.Round(total / count, 2, MidpointRounding.AwayFromZero) : null;

        private static string RangeText(DateTime from, DateTime to) => $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd} (service dates, inclusive)";

        private static (DateTime From, DateTime To) RequiredRange(JsonElement input)
        {
            var from = OptDate(input, "from") ?? throw new ToolInputException("from is required (YYYY-MM-DD).");
            var to = OptDate(input, "to") ?? throw new ToolInputException("to is required (YYYY-MM-DD).");
            if (to < from) throw new ToolInputException("to is before from.");
            if ((to - from).TotalDays > 366 * 5) throw new ToolInputException("Use a range of five years or less.");
            return (from, to);
        }

        private static DateTime? OptDate(JsonElement input, string name)
        {
            var s = OptString(input, name);
            if (s == null) return null;
            return DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : throw new ToolInputException($"{name} must be a date in YYYY-MM-DD format.");
        }

        private static string? OptString(JsonElement input, string name) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()
                : null;

        private static int? OptInt(JsonElement input, string name) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
                ? i
                : null;

        private static bool? OptBool(JsonElement input, string name) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean()
                : null;

        private static List<string> StringArray(JsonElement input, string name) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : new List<string>();
    }

    /// <summary>A tool input the model can fix; its message goes back as the tool result.</summary>
    public sealed class ToolInputException : Exception
    {
        public ToolInputException(string message) : base(message) { }
    }
}

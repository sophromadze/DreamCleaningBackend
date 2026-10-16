using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Mcp
{
    /// <summary>A refusal or input problem Claude can act on; its message is the tool result.</summary>
    public sealed class McpToolException : Exception
    {
        public McpToolException(string message) : base(message) { }
    }

    /// <summary>
    /// The connector's expense tools (2026-10). Writes go through <see cref="IExpenseService"/> —
    /// the same <c>CreateAsync</c> / <c>DeleteAsync</c> the Expenses page's buttons call, with the
    /// same DTO and therefore the same validation (the DTO's data annotations, which MVC checks
    /// for the page, are checked here with the same <see cref="Validator"/>). On the normal app
    /// connection: the read-only SQL user is never involved.
    ///
    /// What the connector adds on top is refusal only, never a different rule:
    /// <list type="bullet">
    /// <item>no Salaries — a salary needs a staff member and may be in GEL, which is the page's job;</item>
    /// <item>no Google Ads — that category is filled by the daily sync, a hand entry would double-count;</item>
    /// <item>a duplicate guard: the same entry from the same person through Claude within
    /// <see cref="DuplicateWindow"/> needs <c>confirm_duplicate</c> (a voice retry or a client
    /// retry must not book the cost twice);</item>
    /// <item>delete only what this person added through Claude in the last 24 hours.</item>
    /// </list>
    /// </summary>
    public sealed class McpExpenseTools
    {
        public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan DeleteWindow = TimeSpan.FromHours(24);
        public const int MaxRecent = 50;

        private readonly IExpenseService _expenses;
        private readonly ApplicationDbContext _context;

        public McpExpenseTools(IExpenseService expenses, ApplicationDbContext context)
        {
            _expenses = expenses;
            _context = context;
        }

        public static string TodayNy() => NyTimeHelper.NowNy.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public async Task<object> ListCategoriesAsync()
        {
            var categories = await _expenses.GetCategoriesAsync();
            return new
            {
                today = TodayNy(),
                time_zone = "America/New_York",
                categories = categories
                    .Where(c => IsAllowedCategory(c.Id, c.Name))
                    .Select(c => new { id = c.Id, name = c.Name }),
                not_available_here = "Salaries (add in the panel) and Google Ads (synced automatically)"
            };
        }

        public async Task<object> AddAsync(JsonElement args, int userId, int grantId)
        {
            var name = Str(args, "name")?.Trim() ?? throw new McpToolException("name is required.");
            var categoryId = Int(args, "category_id") ?? throw new McpToolException("category_id is required (from list_expense_categories).");
            var amount = Dec(args, "amount") ?? throw new McpToolException("amount is required.");
            var type = Str(args, "type") ?? throw new McpToolException("type is required: one_time or recurring. Ask the user if unclear.");
            var startText = Str(args, "start_date") ?? throw new McpToolException("start_date is required (YYYY-MM-DD).");
            if (!DateTime.TryParseExact(startText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate))
                throw new McpToolException("start_date must be YYYY-MM-DD.");
            var frequency = Int(args, "frequency_months");
            var prorate = Bool(args, "prorate_by_day") ?? false;
            var notes = Str(args, "notes");
            var confirmDuplicate = Bool(args, "confirm_duplicate") ?? false;

            bool recurring = type switch
            {
                "one_time" => false,
                "recurring" => true,
                _ => throw new McpToolException("type must be one_time or recurring. Ask the user if unclear.")
            };
            if (!recurring && frequency != null)
                throw new McpToolException("frequency_months applies to recurring expenses only. Ask the user whether this repeats.");
            if (!recurring && prorate)
                throw new McpToolException("prorate_by_day applies to monthly recurring expenses only.");

            var category = await _context.ExpenseCategories.AsNoTracking().Where(c => c.Id == categoryId)
                .Select(c => new { c.Id, c.Name }).FirstOrDefaultAsync();
            if (category != null && SalaryExpenseRules.IsSalaryCategory(category.Id))
                throw new McpToolException("Salaries can't be added through Claude. Add them on the Expenses page in the admin panel (they need a staff member and may be in GEL).");
            if (category != null && IsGoogleAds(category.Name))
                throw new McpToolException("Google Ads spend syncs automatically from Google Ads every day, so it isn't added by hand.");

            var dto = new CreateExpenseDto
            {
                Name = name,
                Amount = amount,
                CategoryId = categoryId,
                StartDate = startDate,
                IsRecurring = recurring,
                FrequencyMonths = recurring ? frequency : null,
                ProrateByDay = prorate,
                Notes = notes
            };

            // The data annotations MVC enforces on the page's request body.
            var problems = new List<ValidationResult>();
            if (!Validator.TryValidateObject(dto, new ValidationContext(dto), problems, validateAllProperties: true))
                throw new McpToolException(string.Join(" ", problems.Select(p => p.ErrorMessage)));

            if (!confirmDuplicate)
            {
                var since = DateTime.UtcNow - DuplicateWindow;
                var day = startDate.Date;
                var duplicate = await _context.Expenses.AsNoTracking()
                    .Where(e => e.CreatedVia == ExpenseOrigins.Claude && e.CreatedByUserId == userId && e.CreatedAt >= since
                                && e.CategoryId == categoryId && e.Amount == amount && e.StartDate == day && e.Name == name)
                    .Select(e => new { e.Id, e.CreatedAt })
                    .FirstOrDefaultAsync();
                if (duplicate != null)
                {
                    var minutes = Math.Max(1, (int)Math.Round((DateTime.UtcNow - duplicate.CreatedAt).TotalMinutes));
                    throw new McpToolException(
                        $"An identical expense (id {duplicate.Id}) was added {minutes} min ago. If the user confirms this is a second, separate expense, call again with confirm_duplicate: true.");
                }
            }

            ExpenseDto created;
            try
            {
                created = await _expenses.CreateAsync(dto, userId, new ExpenseOrigin(ExpenseOrigins.Claude, grantId));
            }
            catch (InvalidOperationException ex)
            {
                // The page shows exactly this message for the same input.
                throw new McpToolException(ex.Message);
            }

            return new { added = Row(created, userId) };
        }

        public async Task<object> ListRecentAsync(JsonElement args, int userId)
        {
            var limit = Math.Clamp(Int(args, "limit") ?? 10, 1, MaxRecent);
            var onlyClaude = Str(args, "source") == "claude";
            var rows = onlyClaude
                ? (await _expenses.GetRecentlyCreatedAsync(MaxRecent * 4, includeSynced: false))
                    .Where(r => r.CreatedVia == ExpenseOrigins.Claude).Take(limit).ToList()
                : await _expenses.GetRecentlyCreatedAsync(limit, includeSynced: false);
            return new { expenses = rows.Select(r => Row(r, userId)) };
        }

        public async Task<object> DeleteAsync(JsonElement args, int userId)
        {
            var id = Int(args, "expense_id") ?? throw new McpToolException("expense_id is required (from list_recent_expenses).");
            var row = await _context.Expenses.AsNoTracking().Where(e => e.Id == id)
                .Select(e => new { e.Id, e.Name, e.Amount, e.CreatedVia, e.CreatedByUserId, e.CreatedAt })
                .FirstOrDefaultAsync()
                ?? throw new McpToolException($"There is no expense with id {id}.");

            var refusal = DeleteRefusal(row.CreatedVia, row.CreatedByUserId, row.CreatedAt, userId, DateTime.UtcNow);
            if (refusal != null) throw new McpToolException(refusal);

            if (!await _expenses.DeleteAsync(id))
                throw new McpToolException($"There is no expense with id {id}.");
            return new { deleted = new { id = row.Id, name = row.Name, amount = row.Amount } };
        }

        /// <summary>Null when the connector may delete the row; otherwise why not.</summary>
        public static string? DeleteRefusal(string? createdVia, int createdBy, DateTime createdAtUtc, int userId, DateTime nowUtc)
        {
            if (createdVia != ExpenseOrigins.Claude)
                return "This expense was not added through Claude, so it can only be changed or deleted on the Expenses page.";
            if (createdBy != userId)
                return "This expense was added through Claude by another user. It can only be deleted on the Expenses page.";
            if (createdAtUtc < nowUtc - DeleteWindow)
                return "This expense was added more than 24 hours ago. Delete or change it on the Expenses page.";
            return null;
        }

        private static object Row(ExpenseDto e, int userId) => new
        {
            id = e.Id,
            name = e.Name,
            category = e.CategoryName,
            amount = e.Amount,
            currency = e.Currency == "USD" ? null : e.Currency,
            date = e.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            repeats = e.IsRecurring ? FrequencyText(e.FrequencyMonths) : null,
            ends = e.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            prorated = e.ProrateByDay ? true : (bool?)null,
            via = e.CreatedVia,
            added = NyTimeHelper.ToNy(DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            can_delete = DeleteRefusal(e.CreatedVia, e.CreatedByUserId, e.CreatedAt, userId, DateTime.UtcNow) == null ? true : (bool?)null
        };

        private static string FrequencyText(int? months) => months switch
        {
            1 => "monthly",
            3 => "quarterly",
            6 => "every 6 months",
            12 => "yearly",
            { } m => $"every {m} months",
            _ => "recurring"
        };

        private static bool IsGoogleAds(string? name) =>
            string.Equals(name?.Trim(), GoogleAdsCostService.CategoryName, StringComparison.OrdinalIgnoreCase);

        private static bool IsAllowedCategory(int id, string? name) =>
            !SalaryExpenseRules.IsSalaryCategory(id) && !IsGoogleAds(name);

        private static string? Str(JsonElement a, string n) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString() : null;

        private static int? Int(JsonElement a, string n) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
                ? i : null;

        private static decimal? Dec(JsonElement a, string n) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)
                ? d : null;

        private static bool? Bool(JsonElement a, string n) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : null;
    }
}

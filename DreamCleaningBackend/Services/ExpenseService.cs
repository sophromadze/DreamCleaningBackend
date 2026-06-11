using System.Globalization;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class ExpenseService : IExpenseService
    {
        private readonly ApplicationDbContext _context;

        public ExpenseService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<List<ExpenseDto>> GetAllAsync()
        {
            return await _context.Expenses
                .Include(e => e.CreatedByUser)
                .Include(e => e.Category)
                .OrderByDescending(e => e.StartDate)
                .ThenByDescending(e => e.Id)
                .Select(e => ToDto(e))
                .ToListAsync();
        }

        public async Task<ExpenseDto?> GetByIdAsync(int id)
        {
            var row = await _context.Expenses
                .Include(e => e.CreatedByUser)
                .Include(e => e.Category)
                .FirstOrDefaultAsync(e => e.Id == id);
            return row == null ? null : ToDto(row);
        }

        public async Task<ExpenseDto> CreateAsync(CreateExpenseDto dto, int byUserId)
        {
            await ValidateInputAsync(dto);

            var row = new Expense
            {
                Name = dto.Name.Trim(),
                Amount = dto.Amount,
                CategoryId = dto.CategoryId,
                StartDate = dto.StartDate.Date,
                IsRecurring = dto.IsRecurring,
                FrequencyMonths = dto.IsRecurring ? dto.FrequencyMonths : null,
                EndDate = dto.IsRecurring ? dto.EndDate?.Date : null,
                ProrateByDay = dto.IsRecurring && dto.FrequencyMonths == 1 && dto.ProrateByDay,
                Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
                CreatedByUserId = byUserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Expenses.Add(row);
            await _context.SaveChangesAsync();

            return (await GetByIdAsync(row.Id))!;
        }

        public async Task<ExpenseDto> UpdateAsync(int id, UpdateExpenseDto dto)
        {
            await ValidateInputAsync(dto);

            var row = await _context.Expenses.FirstOrDefaultAsync(e => e.Id == id);
            if (row == null)
                throw new InvalidOperationException("Expense not found.");

            row.Name = dto.Name.Trim();
            row.Amount = dto.Amount;
            row.CategoryId = dto.CategoryId;
            row.StartDate = dto.StartDate.Date;
            row.IsRecurring = dto.IsRecurring;
            row.FrequencyMonths = dto.IsRecurring ? dto.FrequencyMonths : null;
            row.EndDate = dto.IsRecurring ? dto.EndDate?.Date : null;
            row.ProrateByDay = dto.IsRecurring && dto.FrequencyMonths == 1 && dto.ProrateByDay;
            row.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
            row.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return (await GetByIdAsync(row.Id))!;
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var row = await _context.Expenses.FirstOrDefaultAsync(e => e.Id == id);
            if (row == null) return false;
            _context.Expenses.Remove(row);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<List<ExpenseOccurrenceDto>> GetOccurrencesInRangeAsync(DateTime from, DateTime to)
        {
            // Pull every row whose start could plausibly hit [from, to). For one-time rows that
            // means StartDate ∈ [from, to). For recurring rows we can't push the projection
            // into SQL cleanly, so we filter SQL-side only by `StartDate < to`, then expand
            // in memory and filter by [from, to) per occurrence.
            var fromD = from.Date;
            var toD = to.Date;

            var rows = await _context.Expenses
                .Include(e => e.Category)
                .Where(e => e.StartDate < toD)
                .ToListAsync();

            var output = new List<ExpenseOccurrenceDto>();
            foreach (var e in rows)
            {
                foreach (var (date, amount) in ProjectOccurrences(e, fromD, toD))
                {
                    output.Add(new ExpenseOccurrenceDto
                    {
                        ExpenseId = e.Id,
                        Name = e.Name,
                        CategoryId = e.CategoryId,
                        CategoryName = e.Category?.Name ?? string.Empty,
                        Date = date,
                        Amount = amount,
                        IsRecurring = e.IsRecurring
                    });
                }
            }

            return output.OrderByDescending(o => o.Date).ToList();
        }

        public async Task<decimal> GetTotalInRangeAsync(DateTime from, DateTime to)
        {
            var occs = await GetOccurrencesInRangeAsync(from, to);
            return occs.Sum(o => o.Amount);
        }

        public async Task<ExpenseBreakdownDto> GetBreakdownAsync(DateTime from, DateTime to)
        {
            var occs = await GetOccurrencesInRangeAsync(from, to);
            var grouped = occs
                .GroupBy(o => new { o.CategoryId, o.CategoryName })
                .OrderBy(g => g.Key.CategoryId)
                .Select(g => new ExpenseCategoryBreakdownDto
                {
                    CategoryId = g.Key.CategoryId,
                    CategoryName = g.Key.CategoryName,
                    Total = g.Sum(x => x.Amount),
                    Items = g.OrderByDescending(x => x.Date).ToList()
                })
                .ToList();

            return new ExpenseBreakdownDto
            {
                Total = occs.Sum(o => o.Amount),
                ByCategory = grouped
            };
        }

        public async Task<GroupedExpensesDto> GetGroupedAsync(int year, int month)
        {
            if (month < 1 || month > 12)
                throw new InvalidOperationException("Month must be between 1 and 12.");

            var monthStart = new DateTime(year, month, 1);
            var monthEnd = monthStart.AddMonths(1);
            // "All-time" = everything charged up to the end of the selected month (so a future
            // month doesn't pre-count occurrences that haven't happened yet relative to the view).
            var allTimeTo = monthEnd;

            var categories = await _context.ExpenseCategories
                .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Id)
                .ToListAsync();

            var rows = await _context.Expenses
                .Include(e => e.Category)
                .Where(e => e.StartDate < allTimeTo)
                .ToListAsync();

            // Per-row month + all-time totals.
            var monthByRow = new Dictionary<int, decimal>();
            var allTimeByRow = new Dictionary<int, decimal>();
            foreach (var e in rows)
            {
                monthByRow[e.Id] = ProjectOccurrences(e, monthStart, monthEnd).Sum(o => o.Amount);
                allTimeByRow[e.Id] = ProjectOccurrences(e, DateTime.MinValue, allTimeTo).Sum(o => o.Amount);
            }

            var rowsByCategory = rows.ToLookup(e => e.CategoryId);

            var categoryDtos = new List<GroupedCategoryDto>();
            foreach (var cat in categories)
            {
                var catRows = rowsByCategory[cat.Id].ToList();

                // Aggregate by normalized (trimmed, case-insensitive) name within the category.
                var names = catRows
                    .GroupBy(e => e.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new GroupedNameDto
                    {
                        // Display the most recently created spelling of the name.
                        Name = g.OrderByDescending(e => e.CreatedAt).First().Name.Trim(),
                        MonthTotal = g.Sum(e => monthByRow[e.Id]),
                        AllTimeTotal = g.Sum(e => allTimeByRow[e.Id]),
                        Entries = g
                            .OrderByDescending(e => e.StartDate).ThenByDescending(e => e.Id)
                            .Select(e => ToDto(e)).ToList()
                    })
                    // Names with activity in the month sort first (by month spend), then the rest.
                    .OrderByDescending(n => n.MonthTotal)
                    .ThenByDescending(n => n.AllTimeTotal)
                    .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                categoryDtos.Add(new GroupedCategoryDto
                {
                    CategoryId = cat.Id,
                    CategoryName = cat.Name,
                    DisplayOrder = cat.DisplayOrder,
                    MonthTotal = names.Sum(n => n.MonthTotal),
                    Names = names
                });
            }

            return new GroupedExpensesDto
            {
                Year = year,
                Month = month,
                MonthLabel = monthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                MonthTotal = categoryDtos.Sum(c => c.MonthTotal),
                Categories = categoryDtos
            };
        }

        // ── Category management ─────────────────────────────────────────────────

        public async Task<List<ExpenseCategoryDto>> GetCategoriesAsync()
        {
            var counts = await _context.Expenses
                .GroupBy(e => e.CategoryId)
                .Select(g => new { CategoryId = g.Key, Count = g.Count() })
                .ToListAsync();
            var countMap = counts.ToDictionary(c => c.CategoryId, c => c.Count);

            return await _context.ExpenseCategories
                .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Id)
                .Select(c => new ExpenseCategoryDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    DisplayOrder = c.DisplayOrder,
                    IsSystem = c.IsSystem,
                    ExpenseCount = countMap.ContainsKey(c.Id) ? countMap[c.Id] : 0
                })
                .ToListAsync();
        }

        public async Task<ExpenseCategoryDto> CreateCategoryAsync(SaveExpenseCategoryDto dto)
        {
            var name = (dto.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Category name is required.");
            if (await _context.ExpenseCategories.AnyAsync(c => c.Name == name))
                throw new InvalidOperationException("A category with that name already exists.");

            // PK is not auto-generated — assign the next free Id and append to the display order.
            var maxId = await _context.ExpenseCategories.MaxAsync(c => (int?)c.Id) ?? -1;
            var maxOrder = await _context.ExpenseCategories.MaxAsync(c => (int?)c.DisplayOrder) ?? -1;

            var row = new ExpenseCategory
            {
                Id = maxId + 1,
                Name = name,
                DisplayOrder = maxOrder + 1,
                IsSystem = false,
                CreatedAt = DateTime.UtcNow
            };
            _context.ExpenseCategories.Add(row);
            await _context.SaveChangesAsync();

            return new ExpenseCategoryDto
            {
                Id = row.Id,
                Name = row.Name,
                DisplayOrder = row.DisplayOrder,
                IsSystem = row.IsSystem,
                ExpenseCount = 0
            };
        }

        public async Task<ExpenseCategoryDto> UpdateCategoryAsync(int id, SaveExpenseCategoryDto dto)
        {
            var name = (dto.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Category name is required.");

            var row = await _context.ExpenseCategories.FirstOrDefaultAsync(c => c.Id == id);
            if (row == null)
                throw new InvalidOperationException("Category not found.");
            if (await _context.ExpenseCategories.AnyAsync(c => c.Name == name && c.Id != id))
                throw new InvalidOperationException("A category with that name already exists.");

            row.Name = name;
            await _context.SaveChangesAsync();

            var count = await _context.Expenses.CountAsync(e => e.CategoryId == id);
            return new ExpenseCategoryDto
            {
                Id = row.Id,
                Name = row.Name,
                DisplayOrder = row.DisplayOrder,
                IsSystem = row.IsSystem,
                ExpenseCount = count
            };
        }

        public async Task<bool> DeleteCategoryAsync(int id)
        {
            var row = await _context.ExpenseCategories.FirstOrDefaultAsync(c => c.Id == id);
            if (row == null) return false;
            if (row.IsSystem)
                throw new InvalidOperationException("Built-in categories can't be deleted.");
            if (await _context.Expenses.AnyAsync(e => e.CategoryId == id))
                throw new InvalidOperationException("This category still has expenses. Move or delete them first.");

            _context.ExpenseCategories.Remove(row);
            await _context.SaveChangesAsync();
            return true;
        }

        // ──────────────────────────────────────────────────────────────────────────────

        // Yields (date, amount) for every occurrence of an expense that falls in [from, to).
        //
        //   • One-time row: yields (StartDate, Amount) if StartDate ∈ [from, to).
        //   • Recurring, NOT prorated: anchored occurrences StartDate + k*FrequencyMonths, each
        //     charging the full Amount (e.g. a $20 subscription that started mid-month still
        //     charges $20 every month).
        //   • Recurring, prorated (monthly only): one charge per calendar month from the start
        //     month through the end month, with the first/last partial months reduced by the
        //     fraction of days the expense was actually active that month.
        //
        // Dates are returned at midnight (date-only).
        private static IEnumerable<(DateTime Date, decimal Amount)> ProjectOccurrences(Expense e, DateTime from, DateTime to)
        {
            var start = e.StartDate.Date;

            // One-time.
            if (!e.IsRecurring || !e.FrequencyMonths.HasValue || e.FrequencyMonths.Value <= 0)
            {
                if (start >= from && start < to)
                    yield return (start, e.Amount);
                yield break;
            }

            var step = e.FrequencyMonths.Value;
            var endCap = e.EndDate?.Date;

            // Prorated monthly: walk calendar months, reduce the partial first/last month.
            if (e.ProrateByDay && step == 1)
            {
                var startMonth = new DateTime(start.Year, start.Month, 1);
                var endMonth = endCap.HasValue ? new DateTime(endCap.Value.Year, endCap.Value.Month, 1) : (DateTime?)null;

                const int hardCap = 2000;
                int k = 0;
                while (k < hardCap)
                {
                    var monthStart = startMonth.AddMonths(k);
                    if (monthStart >= to) yield break;
                    if (endMonth.HasValue && monthStart > endMonth.Value) yield break;

                    var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
                    var firstActiveDay = (monthStart == startMonth) ? start.Day : 1;
                    var lastActiveDay = (endMonth.HasValue && monthStart == endMonth.Value) ? endCap!.Value.Day : daysInMonth;
                    var activeDays = lastActiveDay - firstActiveDay + 1;
                    if (activeDays <= 0) { k++; continue; }

                    // Attribute the first month to the real start date; later months to the 1st.
                    var occDate = (monthStart == startMonth) ? start : monthStart;
                    var amount = activeDays >= daysInMonth
                        ? e.Amount
                        : Math.Round(e.Amount * activeDays / daysInMonth, 2, MidpointRounding.AwayFromZero);

                    if (occDate >= from && occDate < to)
                        yield return (occDate, amount);
                    k++;
                }
                yield break;
            }

            // Recurring, full amount on each anchored occurrence.
            {
                const int hardCap = 2000;
                int k = 0;
                while (k < hardCap)
                {
                    var occ = start.AddMonths(step * k);
                    if (occ >= to) yield break;
                    if (endCap.HasValue && occ > endCap.Value) yield break;
                    if (occ >= from) yield return (occ, e.Amount);
                    k++;
                }
            }
        }

        private async Task ValidateInputAsync(CreateExpenseDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                throw new InvalidOperationException("Name is required.");
            if (dto.Amount < 0)
                throw new InvalidOperationException("Amount cannot be negative.");
            if (!await _context.ExpenseCategories.AnyAsync(c => c.Id == dto.CategoryId))
                throw new InvalidOperationException("Selected category does not exist.");
            if (dto.IsRecurring)
            {
                if (!dto.FrequencyMonths.HasValue || dto.FrequencyMonths.Value <= 0)
                    throw new InvalidOperationException("Recurring expenses need a frequency in months > 0.");
                if (dto.EndDate.HasValue && dto.EndDate.Value.Date < dto.StartDate.Date)
                    throw new InvalidOperationException("End date cannot be before start date.");
                if (dto.ProrateByDay && dto.FrequencyMonths.Value != 1)
                    throw new InvalidOperationException("Day-based proration is only available for monthly expenses.");
            }
        }

        private static ExpenseDto ToDto(Expense e) => new()
        {
            Id = e.Id,
            Name = e.Name,
            Amount = e.Amount,
            CategoryId = e.CategoryId,
            CategoryName = e.Category?.Name ?? string.Empty,
            StartDate = e.StartDate,
            IsRecurring = e.IsRecurring,
            FrequencyMonths = e.FrequencyMonths,
            EndDate = e.EndDate,
            ProrateByDay = e.ProrateByDay,
            Notes = e.Notes,
            CreatedByUserId = e.CreatedByUserId,
            CreatedByUserName = e.CreatedByUser != null
                ? $"{e.CreatedByUser.FirstName} {e.CreatedByUser.LastName}".Trim()
                : null,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt
        };
    }
}

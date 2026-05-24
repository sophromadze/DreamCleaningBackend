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
                .OrderByDescending(e => e.StartDate)
                .ThenByDescending(e => e.Id)
                .Select(e => ToDto(e))
                .ToListAsync();
        }

        public async Task<ExpenseDto?> GetByIdAsync(int id)
        {
            var row = await _context.Expenses
                .Include(e => e.CreatedByUser)
                .FirstOrDefaultAsync(e => e.Id == id);
            return row == null ? null : ToDto(row);
        }

        public async Task<ExpenseDto> CreateAsync(CreateExpenseDto dto, int byUserId)
        {
            ValidateInput(dto);

            var row = new Expense
            {
                Name = dto.Name.Trim(),
                Amount = dto.Amount,
                Category = dto.Category,
                StartDate = dto.StartDate.Date,
                IsRecurring = dto.IsRecurring,
                FrequencyMonths = dto.IsRecurring ? dto.FrequencyMonths : null,
                EndDate = dto.IsRecurring ? dto.EndDate?.Date : null,
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
            ValidateInput(dto);

            var row = await _context.Expenses.FirstOrDefaultAsync(e => e.Id == id);
            if (row == null)
                throw new InvalidOperationException("Expense not found.");

            row.Name = dto.Name.Trim();
            row.Amount = dto.Amount;
            row.Category = dto.Category;
            row.StartDate = dto.StartDate.Date;
            row.IsRecurring = dto.IsRecurring;
            row.FrequencyMonths = dto.IsRecurring ? dto.FrequencyMonths : null;
            row.EndDate = dto.IsRecurring ? dto.EndDate?.Date : null;
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
                .Where(e => e.StartDate < toD)
                .ToListAsync();

            var output = new List<ExpenseOccurrenceDto>();
            foreach (var e in rows)
            {
                foreach (var occ in ProjectOccurrences(e, fromD, toD))
                {
                    output.Add(new ExpenseOccurrenceDto
                    {
                        ExpenseId = e.Id,
                        Name = e.Name,
                        Category = e.Category,
                        CategoryName = e.Category.ToString(),
                        Date = occ,
                        Amount = e.Amount,
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
                .GroupBy(o => o.Category)
                .OrderBy(g => (int)g.Key)
                .Select(g => new ExpenseCategoryBreakdownDto
                {
                    Category = g.Key,
                    CategoryName = g.Key.ToString(),
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

        // ──────────────────────────────────────────────────────────────────────────────

        // Yields the occurrence dates of an expense that fall in [from, to).
        // For one-time rows: yields StartDate if it lies in the window.
        // For recurring rows: yields StartDate + k*FrequencyMonths for k = 0,1,2,…
        //   while the occurrence is < to AND (EndDate == null OR occurrence <= EndDate).
        // Returns DateTime values at midnight (date-only).
        private static IEnumerable<DateTime> ProjectOccurrences(Expense e, DateTime from, DateTime to)
        {
            var start = e.StartDate.Date;

            if (!e.IsRecurring || !e.FrequencyMonths.HasValue || e.FrequencyMonths.Value <= 0)
            {
                if (start >= from && start < to)
                    yield return start;
                yield break;
            }

            var step = e.FrequencyMonths.Value;
            var endCap = e.EndDate?.Date;

            // Safety cap so a bad input (e.g. step=0 sneaks past validation) can't loop forever.
            // 50 years of monthly occurrences = 600 iterations — plenty of headroom.
            const int hardCap = 2000;
            int k = 0;
            while (k < hardCap)
            {
                var occ = start.AddMonths(step * k);
                if (occ >= to) yield break;
                if (endCap.HasValue && occ > endCap.Value) yield break;
                if (occ >= from) yield return occ;
                k++;
            }
        }

        private static void ValidateInput(CreateExpenseDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                throw new InvalidOperationException("Name is required.");
            if (dto.Amount < 0)
                throw new InvalidOperationException("Amount cannot be negative.");
            if (dto.IsRecurring)
            {
                if (!dto.FrequencyMonths.HasValue || dto.FrequencyMonths.Value <= 0)
                    throw new InvalidOperationException("Recurring expenses need a frequency in months > 0.");
                if (dto.EndDate.HasValue && dto.EndDate.Value.Date < dto.StartDate.Date)
                    throw new InvalidOperationException("End date cannot be before start date.");
            }
        }

        private static ExpenseDto ToDto(Expense e) => new()
        {
            Id = e.Id,
            Name = e.Name,
            Amount = e.Amount,
            Category = e.Category,
            CategoryName = e.Category.ToString(),
            StartDate = e.StartDate,
            IsRecurring = e.IsRecurring,
            FrequencyMonths = e.FrequencyMonths,
            EndDate = e.EndDate,
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

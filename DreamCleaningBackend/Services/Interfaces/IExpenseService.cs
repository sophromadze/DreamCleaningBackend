using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IExpenseService
    {
        Task<List<ExpenseDto>> GetAllAsync();
        Task<ExpenseDto?> GetByIdAsync(int id);
        // `origin` marks a row entered somewhere other than the Expenses page (the Claude connector);
        // null for the page. Validation is the same either way — it is this one method.
        Task<ExpenseDto> CreateAsync(CreateExpenseDto dto, int byUserId, ExpenseOrigin? origin = null);

        // The most recently ENTERED rows (by CreatedAt), newest first. `includeSynced` = false leaves
        // out the Google Ads daily rows, which would otherwise fill any short list.
        Task<List<ExpenseDto>> GetRecentlyCreatedAsync(int count, bool includeSynced);
        Task<ExpenseDto> UpdateAsync(int id, UpdateExpenseDto dto);
        Task<bool> DeleteAsync(int id);

        // Splits a recurring row at EffectiveDate into "was" (capped there) and "now" (a new row
        // at NewAmount) — see AdjustExpenseAmountDto for why this needs no new schema.
        Task<ExpenseDto> AdjustAmountAsync(int id, AdjustExpenseAmountDto dto, int byUserId);

        // Projects all expense occurrences (one-time + each recurring instance) that fall
        // within [from, to). Amounts are already prorated for expenses that opt into day-based
        // proration. Used both for statistics aggregation and per-day attribution.
        Task<List<ExpenseOccurrenceDto>> GetOccurrencesInRangeAsync(DateTime from, DateTime to);

        // Sum-only convenience. Equivalent to GetOccurrencesInRangeAsync(…).Sum(x => x.Amount).
        Task<decimal> GetTotalInRangeAsync(DateTime from, DateTime to);

        // Breakdown grouped by category, items inside each group sorted by date desc.
        Task<ExpenseBreakdownDto> GetBreakdownAsync(DateTime from, DateTime to);

        // Grouped Category → Name → entries view for a single calendar month. Only entries that
        // charge in that month are listed. `today` (defaults to today in New York) decides the
        // month's charged-so-far figure when the month is the running one.
        Task<GroupedExpensesDto> GetGroupedAsync(int year, int month, DateTime? today = null);

        // People a salary can be recorded against: current staff, plus anyone who already has
        // salary rows so a leaver's final payment can still be entered against them.
        Task<List<ExpenseStaffMemberDto>> GetStaffMembersAsync();

        // ── Category management ──
        Task<List<ExpenseCategoryDto>> GetCategoriesAsync();
        Task<ExpenseCategoryDto> CreateCategoryAsync(SaveExpenseCategoryDto dto);
        Task<ExpenseCategoryDto> UpdateCategoryAsync(int id, SaveExpenseCategoryDto dto);
        Task<bool> DeleteCategoryAsync(int id);
    }
}

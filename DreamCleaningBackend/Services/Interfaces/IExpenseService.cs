using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IExpenseService
    {
        Task<List<ExpenseDto>> GetAllAsync();
        Task<ExpenseDto?> GetByIdAsync(int id);
        Task<ExpenseDto> CreateAsync(CreateExpenseDto dto, int byUserId);
        Task<ExpenseDto> UpdateAsync(int id, UpdateExpenseDto dto);
        Task<bool> DeleteAsync(int id);

        // Projects all expense occurrences (one-time + each recurring instance) that fall
        // within [from, to). Amounts are already prorated for expenses that opt into day-based
        // proration. Used both for statistics aggregation and per-day attribution.
        Task<List<ExpenseOccurrenceDto>> GetOccurrencesInRangeAsync(DateTime from, DateTime to);

        // Sum-only convenience. Equivalent to GetOccurrencesInRangeAsync(…).Sum(x => x.Amount).
        Task<decimal> GetTotalInRangeAsync(DateTime from, DateTime to);

        // Breakdown grouped by category, items inside each group sorted by date desc.
        Task<ExpenseBreakdownDto> GetBreakdownAsync(DateTime from, DateTime to);

        // Grouped Category → Name → entries view for a single calendar month.
        Task<GroupedExpensesDto> GetGroupedAsync(int year, int month);

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

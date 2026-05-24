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
        // within [from, to). Used both for statistics aggregation and per-day attribution.
        Task<List<ExpenseOccurrenceDto>> GetOccurrencesInRangeAsync(DateTime from, DateTime to);

        // Sum-only convenience. Equivalent to GetOccurrencesInRangeAsync(…).Sum(x => x.Amount).
        Task<decimal> GetTotalInRangeAsync(DateTime from, DateTime to);

        // Breakdown grouped by category, items inside each group sorted by date desc.
        Task<ExpenseBreakdownDto> GetBreakdownAsync(DateTime from, DateTime to);
    }
}

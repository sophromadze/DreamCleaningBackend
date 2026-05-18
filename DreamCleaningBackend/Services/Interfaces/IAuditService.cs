using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IAuditService
    {
        Task LogCreateAsync<T>(T entity) where T : class;
        Task LogUpdateAsync<T>(T originalEntity, T currentEntity) where T : class;
        Task LogDeleteAsync<T>(T entity) where T : class;
        Task<List<AuditLog>> GetEntityHistoryAsync(string entityType, long entityId);
        Task LogCleanerAssignmentAsync(int orderId, string cleanerEmail, string action, int adminId);
        Task LogBubblePointsAdjustmentAsync(int targetUserId, string targetUserName, int points, string? reason, int adminId, string adminName);

        // Loyalty Discount change audit. EntityType is the virtual "UserLoyaltyDiscount" so the
        // admin audit-history page can filter on this dedicated stream rather than wading through
        // generic User updates. adminUserId is null for background-service writes (auto-activate /
        // auto-upgrade / loyalty-used / loyalty-reversed).
        Task LogLoyaltyDiscountChangeAsync(
            int targetUserId,
            string action,
            decimal oldPercentage, bool oldIsManualOverride, DateTime? oldActivatedAt, DateTime? oldLastUsedAt,
            decimal newPercentage, bool newIsManualOverride, DateTime? newActivatedAt, DateTime? newLastUsedAt,
            int? adminUserId);

        // Reverts the change captured by the given audit row (Create/Update/Delete) by applying
        // OldValues back to the underlying entity. Throws InvalidOperationException for entity
        // types that aren't safely undoable (side-effect logs, payments, etc.) or rows already
        // undone. RedoAsync re-applies the change.
        Task UndoAsync(long auditLogId);
        Task RedoAsync(long auditLogId);
    }
}

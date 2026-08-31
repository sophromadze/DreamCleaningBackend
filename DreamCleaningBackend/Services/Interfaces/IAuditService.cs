using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IAuditService
    {
        Task LogCreateAsync<T>(T entity) where T : class;
        Task LogUpdateAsync<T>(T originalEntity, T currentEntity) where T : class;
        Task LogDeleteAsync<T>(T entity) where T : class;
        Task<List<AuditLog>> GetEntityHistoryAsync(string entityType, long entityId);

        // The general-purpose writer for admin actions that are EVENTS rather than field edits on
        // one EF row — a cleaner paid, a refund issued, a change request rejected, a data sync
        // run. entityType is an AuditEntityTypes constant; changedFields is DERIVED from the two
        // payloads when null. See AuditService.LogActionAsync for the full contract.
        Task LogActionAsync(
            string entityType,
            long entityId,
            string action,
            object? oldValues,
            object? newValues,
            IEnumerable<string>? changedFields = null,
            int? actingUserId = null);
        Task LogCleanerAssignmentAsync(int orderId, string cleanerEmail, string action, int adminId);

        // Customer-facing notification sent for an order (e.g. a resent confirmation). Audit-only:
        // the mail has already left, so these rows are undo-blocked like the other side-effect logs.
        Task LogOrderNotificationAsync(int orderId, string action, string details, int adminId);
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

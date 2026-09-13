using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// An <see cref="IAuditService"/> that records what it was told instead of writing to a
    /// database.
    ///
    /// Two reasons it records rather than simply swallowing: several rules in this codebase are
    /// ABOUT the audit trail — "log the value that was in force, not the nullable column behind
    /// it", "the payload names which invoice re-priced this cleaning" — and asserting them needs
    /// the payload. And a stub that threw would make every service under test un-testable for
    /// reasons unrelated to what is being tested.
    ///
    /// Shared by the tests that construct services needing an audit dependency.
    /// </summary>
    public class RecordingAuditService : IAuditService
    {
        public record Entry(string EntityType, long EntityId, string Action,
            object? OldValues, object? NewValues, int? ActingUserId);

        public List<Entry> Actions { get; } = new();

        public Task LogActionAsync(
            string entityType, long entityId, string action,
            object? oldValues, object? newValues,
            IEnumerable<string>? changedFields = null, int? actingUserId = null)
        {
            Actions.Add(new Entry(entityType, entityId, action, oldValues, newValues, actingUserId));
            return Task.CompletedTask;
        }

        public Task LogCreateAsync<T>(T entity) where T : class => Task.CompletedTask;
        public Task LogUpdateAsync<T>(T originalEntity, T currentEntity) where T : class => Task.CompletedTask;
        public Task LogDeleteAsync<T>(T entity) where T : class => Task.CompletedTask;
        public Task<List<AuditLog>> GetEntityHistoryAsync(string entityType, long entityId) =>
            Task.FromResult(new List<AuditLog>());
        public Task LogCleanerAssignmentAsync(int orderId, string cleanerEmail, string action, int adminId) =>
            Task.CompletedTask;
        public Task LogOrderNotificationAsync(int orderId, string action, string details, int adminId) =>
            Task.CompletedTask;
        public Task LogBubblePointsAdjustmentAsync(
            int targetUserId, string targetUserName, int points, string? reason,
            int adminId, string adminName) => Task.CompletedTask;
        public Task LogLoyaltyDiscountChangeAsync(
            int targetUserId, string action,
            decimal oldPercentage, bool oldIsManualOverride, DateTime? oldActivatedAt, DateTime? oldLastUsedAt,
            decimal newPercentage, bool newIsManualOverride, DateTime? newActivatedAt, DateTime? newLastUsedAt,
            int? adminUserId) => Task.CompletedTask;
        public Task UndoAsync(long auditLogId) => Task.CompletedTask;
        public Task RedoAsync(long auditLogId) => Task.CompletedTask;
    }
}

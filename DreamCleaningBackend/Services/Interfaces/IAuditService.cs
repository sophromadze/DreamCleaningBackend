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
    }
}

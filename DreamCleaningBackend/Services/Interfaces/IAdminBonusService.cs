using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IAdminBonusService
    {
        // Sets, changes, or clears (when adminId == null) the assigned admin on an order.
        // Always writes a row to OrderAdminAssignmentHistory so the change stays auditable.
        Task<OrderAssignedAdminDto> AssignAdminAsync(int orderId, int? adminId, int byUserId);

        // Returns the current bonus rate setting (single-row, seeded at 10 GEL).
        Task<AdminBonusRateDto> GetRateAsync();

        // Updates the rate. SuperAdmin-only check is enforced at the controller layer.
        Task<AdminBonusRateDto> SetRateAsync(decimal newRate, int byUserId);

        // Returns one row per admin within [from, to). When viewer is not SuperAdmin only
        // their own row is returned. Window is filtered on Order.ServiceDate so the data
        // lines up with the shifts calendar.
        Task<List<AdminBonusSummaryDto>> GetBonusesAsync(
            DateTime from,
            DateTime to,
            int viewerUserId,
            bool viewerIsSuperAdmin,
            int? adminIdFilter = null);

        // Returns a single admin's lifetime/period totals, used by the admin user-profile page.
        Task<AdminBonusSummaryDto> GetSummaryForAdminAsync(int adminId, DateTime? from, DateTime? to);
    }
}

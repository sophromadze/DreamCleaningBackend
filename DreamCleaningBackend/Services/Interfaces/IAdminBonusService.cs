using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IAdminBonusService
    {
        Task<OrderAssignedAdminDto> AssignAdminAsync(int orderId, int? adminId, int byUserId);

        /// <summary>Company-wide defaults: three slots x new-vs-returning customer.</summary>
        Task<AdminBonusRatesDto> GetRatesAsync();
        Task<AdminBonusRatesDto> SetRatesAsync(SetAdminBonusRatesDto dto, int byUserId);

        /// <summary>
        /// Per-person rates: one pair for their own bookings, one for a manager's team share.
        /// Nulls on every field clear the override. Returns what was there before, so the caller
        /// can audit the change without re-reading it.
        /// </summary>
        Task<SetAdminBonusOverrideDto> SetRateOverrideAsync(int adminId, SetAdminBonusOverrideDto dto, int byUserId);

        Task<List<AdminBonusSummaryDto>> GetBonusesAsync(
            DateTime from, DateTime to, int viewerUserId, bool viewerIsSuperAdmin, int? adminIdFilter = null);

        Task<AdminBonusSummaryDto> GetSummaryForAdminAsync(int adminId, DateTime? from, DateTime? to);

        /// <summary>
        /// What each order in the window actually costs the company in staff bonuses (GEL), keyed
        /// by order id. Both slots are included — the booker's share and their manager's — which is
        /// why statistics cannot get this from an order count times one rate any more.
        /// Only bonus-eligible orders appear; everything else costs nothing and is absent.
        /// </summary>
        /// <param name="includeUnfinished">
        /// Widens the set to booked-but-undelivered jobs (the finances page's projection toggle).
        /// Never true on a path that pays somebody — a bonus is earned on delivery.
        /// </param>
        Task<Dictionary<int, decimal>> GetOrderBonusCostsGelAsync(DateTime? from, DateTime? to, bool includeUnfinished = false);
    }
}

using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>
    /// Reads behind the cleaner portal. READ ONLY, entirely and on purpose: there is no method here
    /// that writes, so no route into this section can change an order. Cleaners are told about work,
    /// they do not administer it, and editing an order already lives in the admin orders panel where
    /// the pricing rules are enforced.
    /// </summary>
    public interface ICleanerPortalService
    {
        /// <summary>A cleaner's own jobs: what they are staffed on now, and their finished history.</summary>
        Task<CleanerPortalMyJobsDto> GetMyJobsAsync(int cleanerId);

        /// <summary>
        /// EVERY cleaning in the system for the SuperAdmin view - past, current and future, for
        /// every cleaner, unfiltered by cleaner. Newest service date first, so today's work and the
        /// days either side of it are what the page opens on.
        /// </summary>
        Task<List<CleanerPortalAdminJobDto>> GetAllJobsAsync(DateTime? from, DateTime? to, string? search);

        /// <summary>The SuperAdmin's full read-only detail for one order. Null when it does not exist.</summary>
        Task<CleanerPortalOrderDetailDto?> GetOrderDetailAsync(int orderId);
    }
}

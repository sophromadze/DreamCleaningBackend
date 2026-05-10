using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>
    /// Persists per-user cleaning photos (admin-only library) and prunes older orders'
    /// photos so each user keeps at most their two most recent cleanings on disk.
    /// Used by both the admin user-care upload and the booking flow's post-payment save.
    /// </summary>
    public interface IUserCleaningPhotoService
    {
        /// <summary>
        /// Saves an image as a resized WebP under user-cleaning-photos/, persists a
        /// UserCleaningPhoto row, and returns the saved entity.
        /// </summary>
        Task<UserCleaningPhoto> SavePhotoFromStreamAsync(
            int userId,
            int? orderId,
            Stream imageStream,
            string? caption = null,
            int? uploadedByAdminId = null,
            string? uploadedByAdminName = null);

        /// <summary>
        /// Same as SavePhotoFromStreamAsync but accepts an IFormFile and validates
        /// size + extension before saving (used by admin upload endpoints).
        /// </summary>
        Task<UserCleaningPhoto> SavePhotoFromFormFileAsync(
            int userId,
            int? orderId,
            IFormFile file,
            string? caption = null,
            int? uploadedByAdminId = null,
            string? uploadedByAdminName = null);

        /// <summary>
        /// Prunes photos belonging to orders older than the user's two most recent
        /// (distinct) orders. Returns the number of pruned photos.
        /// </summary>
        Task<int> PruneOldPhotosAsync(int userId);
    }
}

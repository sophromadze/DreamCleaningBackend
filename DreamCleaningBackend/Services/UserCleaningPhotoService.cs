using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Resize-to-WebP + DB write + retention prune for the per-user cleaning photo
    /// library. Centralises the logic so the admin upload endpoint and the booking
    /// flow's post-payment save behave identically (same dimensions, codec, retention).
    /// </summary>
    public class UserCleaningPhotoService : IUserCleaningPhotoService
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;

        // Match AdminUserCareController/CleanerManagementService limits
        private const long MaxUploadSizeBytes = 10 * 1024 * 1024;
        private const int MaxWidth = 1600;
        private const int MaxHeight = 1600;
        private const int WebpQuality = 80;
        private const string Subfolder = "user-cleaning-photos";

        private static readonly string[] AllowedImageExtensions =
            { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };

        public UserCleaningPhotoService(ApplicationDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        public async Task<UserCleaningPhoto> SavePhotoFromFormFileAsync(
            int userId,
            int? orderId,
            IFormFile file,
            string? caption = null,
            int? uploadedByAdminId = null,
            string? uploadedByAdminName = null)
        {
            if (file == null || file.Length == 0)
                throw new InvalidOperationException("No file uploaded.");

            if (file.Length > MaxUploadSizeBytes)
                throw new InvalidOperationException("File size must be less than 10MB.");

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedImageExtensions.Contains(extension))
                throw new InvalidOperationException("Invalid file type. Only image files are allowed.");

            using var stream = file.OpenReadStream();
            return await SavePhotoFromStreamAsync(userId, orderId, stream, caption, uploadedByAdminId, uploadedByAdminName);
        }

        public async Task<UserCleaningPhoto> SavePhotoFromStreamAsync(
            int userId,
            int? orderId,
            Stream imageStream,
            string? caption = null,
            int? uploadedByAdminId = null,
            string? uploadedByAdminName = null)
        {
            var basePath = _configuration["FileUpload:Path"];
            if (string.IsNullOrWhiteSpace(basePath))
                throw new InvalidOperationException("FileUpload:Path is not configured.");

            var uploadDir = Path.Combine(basePath, Subfolder);
            Directory.CreateDirectory(uploadDir);

            var fileName = $"user-{userId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.webp";
            var fullPath = Path.Combine(uploadDir, fileName);

            using (var image = await Image.LoadAsync(imageStream))
            {
                if (image.Width > MaxWidth || image.Height > MaxHeight)
                {
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(MaxWidth, MaxHeight),
                        Mode = ResizeMode.Max
                    }));
                }

                var encoder = new WebpEncoder
                {
                    Quality = WebpQuality,
                    Method = WebpEncodingMethod.BestQuality
                };

                await image.SaveAsync(fullPath, encoder);
            }

            var info = new FileInfo(fullPath);
            var publicUrl = "/" + Path.Combine(Subfolder, fileName).Replace("\\", "/");

            var photo = new UserCleaningPhoto
            {
                UserId = userId,
                OrderId = orderId,
                PhotoUrl = publicUrl,
                SizeBytes = info.Length,
                UploadedByAdminId = uploadedByAdminId,
                UploadedByAdminName = uploadedByAdminName,
                Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(),
                CreatedAt = DateTime.UtcNow
            };

            _context.UserCleaningPhotos.Add(photo);
            await _context.SaveChangesAsync();
            return photo;
        }

        public async Task<int> PruneOldPhotosAsync(int userId)
        {
            // Distinct order IDs that this user has photos for (null OrderIds are kept as-is)
            var orderIdsWithPhotos = await _context.UserCleaningPhotos
                .Where(p => p.UserId == userId && p.OrderId != null)
                .Select(p => p.OrderId!.Value)
                .Distinct()
                .ToListAsync();

            if (orderIdsWithPhotos.Count <= 2) return 0;

            var orderDates = await _context.Orders
                .Where(o => orderIdsWithPhotos.Contains(o.Id))
                .Select(o => new { o.Id, o.ServiceDate })
                .ToListAsync();

            var keepOrderIds = orderDates
                .OrderByDescending(o => o.ServiceDate)
                .Take(2)
                .Select(o => o.Id)
                .ToHashSet();

            var toDelete = await _context.UserCleaningPhotos
                .Where(p => p.UserId == userId
                            && p.OrderId != null
                            && !keepOrderIds.Contains(p.OrderId!.Value))
                .ToListAsync();

            foreach (var p in toDelete)
                DeleteFileIfExists(p.PhotoUrl);

            _context.UserCleaningPhotos.RemoveRange(toDelete);
            await _context.SaveChangesAsync();
            return toDelete.Count;
        }

        private void DeleteFileIfExists(string? publicUrl)
        {
            if (string.IsNullOrWhiteSpace(publicUrl)) return;

            var basePath = _configuration["FileUpload:Path"];
            if (string.IsNullOrWhiteSpace(basePath)) return;

            var relative = publicUrl.TrimStart('/');
            var fullPath = Path.Combine(basePath, relative);

            try
            {
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
            }
            catch
            {
                // ignore — the DB row is being removed regardless
            }
        }
    }
}

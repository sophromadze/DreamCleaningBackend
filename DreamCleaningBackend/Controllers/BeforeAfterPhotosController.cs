using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Manages the before/after photo gallery rendered in the homepage
    /// "See the difference" section. Public read endpoint is anonymous;
    /// all write endpoints require Admin/SuperAdmin.
    /// </summary>
    [ApiController]
    public class BeforeAfterPhotosController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;

        // Match AdminUserCareController image-upload constraints for consistency.
        private const long MaxUploadSizeBytes = 10 * 1024 * 1024;
        private static readonly string[] AllowedImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };

        private readonly IAuditService _auditService;
        private readonly IMemoryCache _cache;

        // Re-encode quality for originals and their resized variants.
        private const int WebpQuality = 82;
        // The public list reads image headers to build srcset; cache the result per URL. Upload and
        // replace always produce NEW urls, so only the backfill has to evict.
        private static readonly TimeSpan SrcsetCacheDuration = TimeSpan.FromHours(6);

        public BeforeAfterPhotosController(ApplicationDbContext context, IConfiguration configuration, IAuditService auditService, IMemoryCache cache)
        {
            _context = context;
            _configuration = configuration;
            _auditService = auditService;
            _cache = cache;
        }

        // ─────────────────────────────────────────────────────────
        //  PUBLIC: list active pairs for the homepage
        // ─────────────────────────────────────────────────────────
        [HttpGet("api/before-after-photos")]
        [AllowAnonymous]
        public async Task<ActionResult<List<BeforeAfterPhotoDto>>> GetPublic()
        {
            var rows = await _context.BeforeAfterPhotos
                .Where(p => p.IsActive)
                .OrderBy(p => p.DisplayOrder).ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            var dtos = new List<BeforeAfterPhotoDto>(rows.Count);
            foreach (var row in rows)
            {
                var dto = MapDto(row);
                dto.BeforeSrcset = await GetSrcsetAsync(row.BeforePhotoUrl);
                dto.AfterSrcset = await GetSrcsetAsync(row.AfterPhotoUrl);
                dtos.Add(dto);
            }
            return Ok(dtos);
        }

        // ─────────────────────────────────────────────────────────
        //  ADMIN
        // ─────────────────────────────────────────────────────────
        [HttpGet("api/admin/before-after-photos")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<BeforeAfterPhotoDto>>> ListAdmin()
        {
            var rows = await _context.BeforeAfterPhotos
                .OrderBy(p => p.DisplayOrder).ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            return Ok(rows.Select(MapDto).ToList());
        }

        /// <summary>
        /// Backfill: writes the missing 400w/800w variants for every stored pair (active or not).
        /// Idempotent — variants already on disk are skipped, so it can be run any number of times.
        /// </summary>
        [HttpPost("api/admin/before-after-photos/generate-variants")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> GenerateVariants()
        {
            if (string.IsNullOrWhiteSpace(_configuration["FileUpload:Path"]))
                return BadRequest(new { message = "FileUpload:Path is not configured." });

            var urls = (await _context.BeforeAfterPhotos
                    .Select(p => new { p.BeforePhotoUrl, p.AfterPhotoUrl })
                    .ToListAsync())
                .SelectMany(p => new[] { p.BeforePhotoUrl, p.AfterPhotoUrl })
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct()
                .ToList();

            int processed = 0, alreadyComplete = 0, variantsWritten = 0, originalsMissing = 0, failed = 0;
            foreach (var url in urls)
            {
                var fullPath = ResolveFullPath(url);
                if (fullPath == null || !System.IO.File.Exists(fullPath))
                {
                    originalsMissing++;
                    continue;
                }

                try
                {
                    // Read the header first so a fully backfilled image is never decoded again.
                    var info = await Image.IdentifyAsync(fullPath);
                    var missing = ResponsiveImageVariants.WidthsFor(info.Width)
                        .Any(w => !System.IO.File.Exists(ResponsiveImageVariants.VariantPath(fullPath, w)));
                    if (!missing)
                    {
                        alreadyComplete++;
                    }
                    else
                    {
                        using var image = await Image.LoadAsync(fullPath);
                        variantsWritten += await ResponsiveImageVariants.WriteMissingAsync(image, fullPath, WebpQuality);
                        processed++;
                    }
                }
                catch
                {
                    failed++;
                }
                _cache.Remove(SrcsetCacheKey(url));
            }

            return Ok(new { images = urls.Count, processed, alreadyComplete, variantsWritten, originalsMissing, failed });
        }

        [HttpPost("api/admin/before-after-photos")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        [RequirePermission(Permission.Create)]
        [RequestSizeLimit(25 * 1024 * 1024)] // two ~10MB images + metadata
        public async Task<ActionResult<BeforeAfterPhotoDto>> Create(
            [FromForm] IFormFile beforeFile,
            [FromForm] IFormFile afterFile,
            [FromForm, Required] string title,
            [FromForm] string? subtitle,
            [FromForm] string? linkUrl,
            [FromForm] int? displayOrder)
        {
            if (string.IsNullOrWhiteSpace(title))
                return BadRequest(new { message = "Title is required." });

            try
            {
                var savedBefore = await SaveWebpImageAsync(beforeFile, "before-after", "before", 1800, 1800, WebpQuality);
                var savedAfter = await SaveWebpImageAsync(afterFile, "before-after", "after", 1800, 1800, WebpQuality);

                if (savedBefore == null || savedAfter == null)
                    return BadRequest(new { message = "Could not process one of the images." });

                var entity = new BeforeAfterPhoto
                {
                    Title = title.Trim(),
                    Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle.Trim(),
                    LinkUrl = string.IsNullOrWhiteSpace(linkUrl) ? null : linkUrl.Trim(),
                    DisplayOrder = displayOrder ?? 0,
                    IsActive = true,
                    BeforePhotoUrl = savedBefore.Url,
                    AfterPhotoUrl = savedAfter.Url,
                    BeforeSizeBytes = savedBefore.SizeBytes,
                    AfterSizeBytes = savedAfter.SizeBytes,
                    UploadedByAdminId = GetUserId(),
                    UploadedByAdminName = GetUserDisplayName(),
                    CreatedAt = DateTime.UtcNow
                };

                _context.BeforeAfterPhotos.Add(entity);
                await _context.SaveChangesAsync();

                await _auditService.LogCreateAsync(entity);

                return Ok(MapDto(entity));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPatch("api/admin/before-after-photos/{id}")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<BeforeAfterPhotoDto>> Update(int id, [FromBody] UpdateBeforeAfterPhotoDto body)
        {
            var entity = await _context.BeforeAfterPhotos.FirstOrDefaultAsync(p => p.Id == id);
            if (entity == null) return NotFound(new { message = "Before/after pair not found." });

            var before = AuditSnapshot.Of(entity);

            if (body.Title != null)
            {
                if (string.IsNullOrWhiteSpace(body.Title))
                    return BadRequest(new { message = "Title cannot be empty." });
                entity.Title = body.Title.Trim();
            }
            if (body.Subtitle != null) entity.Subtitle = string.IsNullOrWhiteSpace(body.Subtitle) ? null : body.Subtitle.Trim();
            if (body.LinkUrl != null)  entity.LinkUrl  = string.IsNullOrWhiteSpace(body.LinkUrl)  ? null : body.LinkUrl.Trim();
            if (body.DisplayOrder.HasValue) entity.DisplayOrder = body.DisplayOrder.Value;
            if (body.IsActive.HasValue) entity.IsActive = body.IsActive.Value;
            entity.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _auditService.LogUpdateAsync(before, entity);

            return Ok(MapDto(entity));
        }

        [HttpPost("api/admin/before-after-photos/{id}/replace-before")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        [RequirePermission(Permission.Update)]
        [RequestSizeLimit(15 * 1024 * 1024)]
        public async Task<ActionResult<BeforeAfterPhotoDto>> ReplaceBefore(int id, IFormFile file)
            => await ReplaceImageAsync(id, file, isBefore: true);

        [HttpPost("api/admin/before-after-photos/{id}/replace-after")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        [RequirePermission(Permission.Update)]
        [RequestSizeLimit(15 * 1024 * 1024)]
        public async Task<ActionResult<BeforeAfterPhotoDto>> ReplaceAfter(int id, IFormFile file)
            => await ReplaceImageAsync(id, file, isBefore: false);

        private async Task<ActionResult<BeforeAfterPhotoDto>> ReplaceImageAsync(int id, IFormFile file, bool isBefore)
        {
            var entity = await _context.BeforeAfterPhotos.FirstOrDefaultAsync(p => p.Id == id);
            if (entity == null) return NotFound(new { message = "Before/after pair not found." });

            // Taken before the swap: the replaced file is deleted from disk below, so this row is
            // the only record that the previous image ever existed.
            var beforeReplace = AuditSnapshot.Of(entity);

            try
            {
                var saved = await SaveWebpImageAsync(file, "before-after",
                    isBefore ? "before" : "after",
                    1800, 1800, WebpQuality);
                if (saved == null) return BadRequest(new { message = "Could not process the image." });

                if (isBefore)
                {
                    DeleteFileIfExists(entity.BeforePhotoUrl);
                    entity.BeforePhotoUrl = saved.Url;
                    entity.BeforeSizeBytes = saved.SizeBytes;
                }
                else
                {
                    DeleteFileIfExists(entity.AfterPhotoUrl);
                    entity.AfterPhotoUrl = saved.Url;
                    entity.AfterSizeBytes = saved.SizeBytes;
                }
                entity.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                await _auditService.LogUpdateAsync(beforeReplace, entity);

                return Ok(MapDto(entity));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("api/admin/before-after-photos/{id}")]
        [Authorize(Roles = "Admin,SuperAdmin")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> Delete(int id)
        {
            var entity = await _context.BeforeAfterPhotos.FirstOrDefaultAsync(p => p.Id == id);
            if (entity == null) return NotFound(new { message = "Before/after pair not found." });

            await _auditService.LogDeleteAsync(entity);

            DeleteFileIfExists(entity.BeforePhotoUrl);
            DeleteFileIfExists(entity.AfterPhotoUrl);
            _context.BeforeAfterPhotos.Remove(entity);
            await _context.SaveChangesAsync();
            return NoContent();
        }

        // ─────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────
        private static BeforeAfterPhotoDto MapDto(BeforeAfterPhoto p) => new()
        {
            Id = p.Id,
            Title = p.Title,
            Subtitle = p.Subtitle,
            BeforePhotoUrl = p.BeforePhotoUrl,
            AfterPhotoUrl = p.AfterPhotoUrl,
            LinkUrl = p.LinkUrl,
            DisplayOrder = p.DisplayOrder,
            IsActive = p.IsActive,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt
        };

        private async Task<UploadedImageInfo?> SaveWebpImageAsync(IFormFile file, string subfolder, string baseFileName, int maxWidth, int maxHeight, int quality)
        {
            if (file == null || file.Length == 0)
                throw new InvalidOperationException("No file uploaded.");

            if (file.Length > MaxUploadSizeBytes)
                throw new InvalidOperationException("File size must be less than 10MB.");

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedImageExtensions.Contains(extension))
                throw new InvalidOperationException("Invalid file type. Only image files are allowed.");

            var basePath = _configuration["FileUpload:Path"];
            if (string.IsNullOrWhiteSpace(basePath))
                throw new InvalidOperationException("FileUpload:Path is not configured.");

            var uploadDir = Path.Combine(basePath, subfolder);
            Directory.CreateDirectory(uploadDir);

            var fileName = $"{baseFileName}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.webp";
            var fullPath = Path.Combine(uploadDir, fileName);

            using (var inputStream = file.OpenReadStream())
            using (var image = await Image.LoadAsync(inputStream))
            {
                if (image.Width > maxWidth || image.Height > maxHeight)
                {
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(maxWidth, maxHeight),
                        Mode = ResizeMode.Max
                    }));
                }

                var encoder = new WebpEncoder
                {
                    Quality = quality,
                    Method = WebpEncodingMethod.BestQuality
                };

                await image.SaveAsync(fullPath, encoder);

                // Resized copies for srcset (-400w.webp / -800w.webp), from the same decoded image.
                await ResponsiveImageVariants.WriteMissingAsync(image, fullPath, quality);
            }

            var info = new FileInfo(fullPath);
            var publicUrl = "/" + Path.Combine(subfolder, fileName).Replace("\\", "/");

            return new UploadedImageInfo { Url = publicUrl, SizeBytes = info.Length };
        }

        private void DeleteFileIfExists(string? publicUrl)
        {
            if (string.IsNullOrWhiteSpace(publicUrl)) return;

            var fullPath = ResolveFullPath(publicUrl);
            if (fullPath == null) return;

            try
            {
                if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
            }
            catch
            {
                // DB row is being removed regardless — ignore disk failures
            }
            ResponsiveImageVariants.DeleteAll(fullPath);
            _cache.Remove(SrcsetCacheKey(publicUrl));
        }

        /// <summary>Disk path of an uploaded file's public URL, under FileUpload:Path.</summary>
        private string? ResolveFullPath(string publicUrl)
        {
            var basePath = _configuration["FileUpload:Path"];
            if (string.IsNullOrWhiteSpace(basePath)) return null;
            return Path.Combine(basePath, publicUrl.TrimStart('/'));
        }

        private static string SrcsetCacheKey(string publicUrl) => $"BeforeAfterSrcset:{publicUrl}";

        private async Task<string?> GetSrcsetAsync(string publicUrl)
        {
            if (string.IsNullOrWhiteSpace(publicUrl)) return null;
            var key = SrcsetCacheKey(publicUrl);
            // Cached as "" when there is no srcset, so a missing one is not recomputed per request.
            if (_cache.TryGetValue(key, out string? cached)) return string.IsNullOrEmpty(cached) ? null : cached;

            var fullPath = ResolveFullPath(publicUrl);
            var srcset = fullPath == null ? null : await ResponsiveImageVariants.BuildSrcsetAsync(publicUrl, fullPath);
            _cache.Set(key, srcset ?? string.Empty, SrcsetCacheDuration);
            return srcset;
        }

        private int GetUserId()
        {
            var id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(id, out var parsed) ? parsed : 0;
        }

        private string GetUserDisplayName()
        {
            var first = User.FindFirst(ClaimTypes.GivenName)?.Value ?? User.FindFirst("FirstName")?.Value;
            var last = User.FindFirst(ClaimTypes.Surname)?.Value ?? User.FindFirst("LastName")?.Value;
            var combined = $"{first} {last}".Trim();
            if (!string.IsNullOrWhiteSpace(combined)) return combined;

            var name = User.FindFirst(ClaimTypes.Name)?.Value;
            if (!string.IsNullOrWhiteSpace(name)) return name;

            return User.FindFirst(ClaimTypes.Email)?.Value ?? "Admin";
        }

        private sealed class UploadedImageInfo
        {
            public string Url { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  DTOs (kept here to keep the feature self-contained)
    // ─────────────────────────────────────────────────────────
    public class BeforeAfterPhotoDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public string BeforePhotoUrl { get; set; } = string.Empty;
        public string AfterPhotoUrl { get; set; } = string.Empty;
        /// <summary>Public list only: srcset over the resized variants that exist, else null.</summary>
        public string? BeforeSrcset { get; set; }
        public string? AfterSrcset { get; set; }
        public string? LinkUrl { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    public class UpdateBeforeAfterPhotoDto
    {
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public string? LinkUrl { get; set; }
        public int? DisplayOrder { get; set; }
        public bool? IsActive { get; set; }
    }
}

using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

        public BeforeAfterPhotosController(ApplicationDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
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

            return Ok(rows.Select(MapDto).ToList());
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
                var savedBefore = await SaveWebpImageAsync(beforeFile, "before-after", "before", 1800, 1800, 82);
                var savedAfter = await SaveWebpImageAsync(afterFile, "before-after", "after", 1800, 1800, 82);

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

            try
            {
                var saved = await SaveWebpImageAsync(file, "before-after",
                    isBefore ? "before" : "after",
                    1800, 1800, 82);
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
            }

            var info = new FileInfo(fullPath);
            var publicUrl = "/" + Path.Combine(subfolder, fileName).Replace("\\", "/");

            return new UploadedImageInfo { Url = publicUrl, SizeBytes = info.Length };
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
                if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
            }
            catch
            {
                // DB row is being removed regardless — ignore disk failures
            }
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

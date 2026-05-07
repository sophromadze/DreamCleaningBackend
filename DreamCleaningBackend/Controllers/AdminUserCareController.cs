using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Admin endpoints that drive the user detail panel: per-user notes (general & follow-up),
    /// admin-only cleaning photos (last two orders kept), per-user communications log
    /// (backed by ClientInteractions), and per-user task lookup.
    /// </summary>
    [Route("api/admin/user-care")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminUserCareController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;

        // Image upload constraints — match CleanerManagementService for consistency
        private const long MaxUploadSizeBytes = 10 * 1024 * 1024;
        private static readonly string[] AllowedImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };

        public AdminUserCareController(ApplicationDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        // ─────────────────────────────────────────────────────────
        //  NOTES (general / follow-up) — multi-row per user
        // ─────────────────────────────────────────────────────────

        [HttpGet("users/{userId}/notes")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<UserNoteDto>>> GetUserNotes(int userId, [FromQuery] string? type = null)
        {
            var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
            if (!userExists) return NotFound(new { message = "User not found" });

            var query = _context.UserNotes.Where(n => n.UserId == userId);
            if (!string.IsNullOrWhiteSpace(type))
                query = query.Where(n => n.Type == type);

            var notes = await query
                .OrderByDescending(n => n.CreatedAt)
                .Select(n => new UserNoteDto
                {
                    Id = n.Id,
                    UserId = n.UserId,
                    Type = n.Type,
                    Content = n.Content,
                    NextOffer = n.NextOffer,
                    CreatedByAdminId = n.CreatedByAdminId,
                    CreatedByAdminName = n.CreatedByAdminName,
                    CreatedAt = n.CreatedAt,
                    UpdatedAt = n.UpdatedAt
                })
                .ToListAsync();

            return Ok(notes);
        }

        [HttpPost("users/{userId}/notes")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<UserNoteDto>> CreateUserNote(int userId, [FromBody] CreateUserNoteDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
            if (!userExists) return NotFound(new { message = "User not found" });

            var type = NormalizeNoteType(dto.Type);
            if (type == null)
                return BadRequest(new { message = "Type must be 'General' or 'FollowUp'." });

            var note = new UserNote
            {
                UserId = userId,
                Type = type,
                Content = dto.Content.Trim(),
                NextOffer = string.IsNullOrWhiteSpace(dto.NextOffer) ? null : dto.NextOffer.Trim(),
                CreatedByAdminId = GetUserId(),
                CreatedByAdminName = GetUserDisplayName(),
                CreatedAt = DateTime.UtcNow
            };

            _context.UserNotes.Add(note);
            await _context.SaveChangesAsync();

            return Ok(MapNote(note));
        }

        [HttpPut("notes/{noteId}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<UserNoteDto>> UpdateUserNote(int noteId, [FromBody] UpdateUserNoteDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var note = await _context.UserNotes.FirstOrDefaultAsync(n => n.Id == noteId);
            if (note == null) return NotFound(new { message = "Note not found" });

            note.Content = dto.Content.Trim();
            note.NextOffer = string.IsNullOrWhiteSpace(dto.NextOffer) ? null : dto.NextOffer.Trim();
            note.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return Ok(MapNote(note));
        }

        [HttpDelete("notes/{noteId}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> DeleteUserNote(int noteId)
        {
            var note = await _context.UserNotes.FirstOrDefaultAsync(n => n.Id == noteId);
            if (note == null) return NotFound(new { message = "Note not found" });

            _context.UserNotes.Remove(note);
            await _context.SaveChangesAsync();
            return Ok(new { message = "Note deleted" });
        }

        // ─────────────────────────────────────────────────────────
        //  CLEANING PHOTOS — admin-only, last 2 orders kept
        // ─────────────────────────────────────────────────────────

        [HttpGet("users/{userId}/cleaning-photos")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<UserCleaningPhotosByOrderDto>>> GetUserCleaningPhotos(int userId)
        {
            var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
            if (!userExists) return NotFound(new { message = "User not found" });

            var photos = await _context.UserCleaningPhotos
                .Where(p => p.UserId == userId)
                .Include(p => p.Order)
                    .ThenInclude(o => o!.ServiceType)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            // Group by order (treat unassigned/null OrderId as one bucket so they still display)
            var grouped = photos
                .GroupBy(p => p.OrderId)
                .Select(g =>
                {
                    var first = g.First();
                    return new UserCleaningPhotosByOrderDto
                    {
                        OrderId = g.Key,
                        OrderServiceDate = first.Order?.ServiceDate,
                        OrderServiceTypeName = first.Order?.ServiceType?.Name,
                        Photos = g.OrderByDescending(p => p.CreatedAt).Select(MapPhoto).ToList()
                    };
                })
                .OrderByDescending(g => g.OrderServiceDate ?? DateTime.MinValue)
                .ToList();

            return Ok(grouped);
        }

        [HttpPost("users/{userId}/cleaning-photos")]
        [RequirePermission(Permission.Update)]
        [RequestSizeLimit(15 * 1024 * 1024)]
        public async Task<ActionResult<UserCleaningPhotoUploadResultDto>> UploadCleaningPhoto(
            int userId,
            IFormFile file,
            [FromQuery] int? orderId = null,
            [FromQuery] string? caption = null)
        {
            try
            {
                var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
                if (!userExists) return NotFound(new { message = "User not found" });

                if (orderId.HasValue)
                {
                    var orderBelongs = await _context.Orders.AnyAsync(o => o.Id == orderId.Value && o.UserId == userId);
                    if (!orderBelongs) return BadRequest(new { message = "Order does not belong to this user." });
                }

                var saved = await SaveWebpImageAsync(
                    file,
                    "user-cleaning-photos",
                    $"user-{userId}",
                    maxWidth: 1600,
                    maxHeight: 1600,
                    quality: 80);

                if (saved == null) return BadRequest(new { message = "Could not process image." });

                var photo = new UserCleaningPhoto
                {
                    UserId = userId,
                    OrderId = orderId,
                    PhotoUrl = saved.Url,
                    SizeBytes = saved.SizeBytes,
                    UploadedByAdminId = GetUserId(),
                    UploadedByAdminName = GetUserDisplayName(),
                    Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(),
                    CreatedAt = DateTime.UtcNow
                };

                _context.UserCleaningPhotos.Add(photo);
                await _context.SaveChangesAsync();

                // Prune photos for orders older than the user's two most recent distinct orders
                var pruned = await PruneOldCleaningPhotosAsync(userId);

                return Ok(new UserCleaningPhotoUploadResultDto
                {
                    Photo = MapPhoto(photo),
                    PrunedCount = pruned
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("cleaning-photos/{photoId}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> DeleteCleaningPhoto(int photoId)
        {
            var photo = await _context.UserCleaningPhotos.FirstOrDefaultAsync(p => p.Id == photoId);
            if (photo == null) return NotFound(new { message = "Photo not found" });

            DeleteFileIfExists(photo.PhotoUrl);
            _context.UserCleaningPhotos.Remove(photo);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Photo deleted" });
        }

        /// <summary>
        /// Streams the photo file by id. Anonymous so it can be used as an &lt;img src=&gt;
        /// without complicated credential handling — the photo URLs are non-guessable
        /// (random id) and gated behind the admin SPA. Goes through /api so it works
        /// uniformly in dev (proxy) and prod (same origin) without server-side aliases.
        /// </summary>
        [HttpGet("cleaning-photos/{photoId}/raw")]
        [AllowAnonymous]
        public async Task<IActionResult> GetCleaningPhotoFile(int photoId)
        {
            var photo = await _context.UserCleaningPhotos
                .Where(p => p.Id == photoId)
                .Select(p => new { p.PhotoUrl })
                .FirstOrDefaultAsync();

            if (photo == null || string.IsNullOrWhiteSpace(photo.PhotoUrl))
                return NotFound();

            var basePath = _configuration["FileUpload:Path"];
            if (string.IsNullOrWhiteSpace(basePath)) return NotFound();

            var relative = photo.PhotoUrl.TrimStart('/');
            var fullPath = Path.Combine(basePath, relative);

            // Defense-in-depth: ensure the resolved path stays inside the upload root
            var fullBase = Path.GetFullPath(basePath);
            var resolved = Path.GetFullPath(fullPath);
            if (!resolved.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase)) return Forbid();

            if (!System.IO.File.Exists(resolved)) return NotFound();

            var ext = Path.GetExtension(resolved).ToLowerInvariant();
            var contentType = ext switch
            {
                ".webp" => "image/webp",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => "application/octet-stream"
            };

            // Cache for an hour — files are immutable per id (uploads create new ids)
            Response.Headers["Cache-Control"] = "private, max-age=3600";
            return PhysicalFile(resolved, contentType);
        }

        // ─────────────────────────────────────────────────────────
        //  COMMUNICATIONS — list & quick-create against ClientInteractions
        // ─────────────────────────────────────────────────────────

        [HttpGet("users/{userId}/communications")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<ClientInteractionDto>>> GetUserCommunications(int userId)
        {
            var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
            if (!userExists) return NotFound(new { message = "User not found" });

            var items = await _context.ClientInteractions
                .Where(i => i.ClientId == userId)
                .OrderByDescending(i => i.InteractionDate)
                .Include(i => i.Admin)
                .Select(i => new ClientInteractionDto
                {
                    Id = i.Id,
                    ClientName = i.ClientName,
                    ClientPhone = i.ClientPhone,
                    ClientEmail = i.ClientEmail,
                    ClientId = i.ClientId,
                    InteractionDate = i.InteractionDate,
                    AdminId = i.AdminId,
                    AdminName = i.Admin != null ? i.Admin.FirstName + " " + i.Admin.LastName : "",
                    AdminRole = i.Admin != null ? i.Admin.Role.ToString() : "",
                    Type = i.Type,
                    Notes = i.Notes,
                    Status = i.Status,
                    CreatedAt = i.CreatedAt
                })
                .ToListAsync();

            return Ok(items);
        }

        [HttpPost("users/{userId}/communications")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ClientInteractionDto>> CreateUserCommunication(int userId, [FromBody] CreateClientInteractionDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return NotFound(new { message = "User not found" });

            var adminId = GetUserId();
            if (adminId == 0) return Unauthorized();

            var interaction = new ClientInteraction
            {
                ClientName = string.IsNullOrWhiteSpace(dto.ClientName)
                    ? $"{user.FirstName} {user.LastName}".Trim()
                    : dto.ClientName.Trim(),
                ClientPhone = string.IsNullOrWhiteSpace(dto.ClientPhone) ? user.Phone : dto.ClientPhone,
                ClientEmail = string.IsNullOrWhiteSpace(dto.ClientEmail) ? user.Email : dto.ClientEmail,
                ClientId = userId,
                AdminId = adminId,
                Type = dto.Type.Trim(),
                Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
                Status = string.IsNullOrWhiteSpace(dto.Status) ? "Pending" : dto.Status.Trim(),
                InteractionDate = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };

            _context.ClientInteractions.Add(interaction);
            await _context.SaveChangesAsync();

            // Reload with admin for display name
            var admin = await _context.Users.FindAsync(adminId);

            return Ok(new ClientInteractionDto
            {
                Id = interaction.Id,
                ClientName = interaction.ClientName,
                ClientPhone = interaction.ClientPhone,
                ClientEmail = interaction.ClientEmail,
                ClientId = interaction.ClientId,
                InteractionDate = interaction.InteractionDate,
                AdminId = interaction.AdminId,
                AdminName = admin != null ? admin.FirstName + " " + admin.LastName : "",
                AdminRole = admin != null ? admin.Role.ToString() : "",
                Type = interaction.Type,
                Notes = interaction.Notes,
                Status = interaction.Status,
                CreatedAt = interaction.CreatedAt
            });
        }

        [HttpPut("communications/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<ClientInteractionDto>> UpdateUserCommunication(int id, [FromBody] UpdateClientInteractionDto dto)
        {
            var item = await _context.ClientInteractions
                .Include(i => i.Admin)
                .FirstOrDefaultAsync(i => i.Id == id);
            if (item == null) return NotFound();

            if (!string.IsNullOrWhiteSpace(dto.Type)) item.Type = dto.Type.Trim();
            if (dto.Notes != null) item.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
            if (!string.IsNullOrWhiteSpace(dto.Status)) item.Status = dto.Status.Trim();
            // ClientName/Phone/Email are kept as-is — the user this communication is attached to does not change.

            await _context.SaveChangesAsync();

            return Ok(new ClientInteractionDto
            {
                Id = item.Id,
                ClientName = item.ClientName,
                ClientPhone = item.ClientPhone,
                ClientEmail = item.ClientEmail,
                ClientId = item.ClientId,
                InteractionDate = item.InteractionDate,
                AdminId = item.AdminId,
                AdminName = item.Admin != null ? (item.Admin.FirstName + " " + item.Admin.LastName).Trim() : "",
                AdminRole = item.Admin != null ? item.Admin.Role.ToString() : "",
                Type = item.Type,
                Notes = item.Notes,
                Status = item.Status,
                CreatedAt = item.CreatedAt
            });
        }

        [HttpDelete("communications/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> DeleteUserCommunication(int id)
        {
            var item = await _context.ClientInteractions.FirstOrDefaultAsync(i => i.Id == id);
            if (item == null) return NotFound();
            _context.ClientInteractions.Remove(item);
            await _context.SaveChangesAsync();
            return Ok(new { message = "Communication deleted" });
        }

        // ─────────────────────────────────────────────────────────
        //  TASKS for this user
        // ─────────────────────────────────────────────────────────

        [HttpGet("users/{userId}/tasks")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<AdminTaskDto>>> GetUserTasks(int userId)
        {
            var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
            if (!userExists) return NotFound();

            var tasks = await _context.AdminTasks
                .Where(t => t.ClientId == userId)
                .Include(t => t.CreatedByAdmin)
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => new AdminTaskDto
                {
                    Id = t.Id,
                    Title = t.Title,
                    Description = t.Description,
                    Priority = t.Priority,
                    Status = t.Status,
                    DueDate = t.DueDate,
                    ClientName = t.ClientName,
                    ClientEmail = t.ClientEmail,
                    ClientPhone = t.ClientPhone,
                    ClientId = t.ClientId,
                    OrderId = t.OrderId,
                    CreatedByAdminId = t.CreatedByAdminId,
                    CreatedByAdminName = t.CreatedByAdmin != null
                        ? (t.CreatedByAdmin.FirstName + " " + t.CreatedByAdmin.LastName).Trim()
                        : "",
                    CreatedByAdminRole = t.CreatedByAdmin != null
                        ? t.CreatedByAdmin.Role.ToString()
                        : "",
                    CompletionNote = t.CompletionNote,
                    CompletedAt = t.CompletedAt,
                    CreatedAt = t.CreatedAt,
                    UpdatedAt = t.UpdatedAt
                })
                .ToListAsync();

            return Ok(tasks);
        }

        // ─────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns "General" or "FollowUp" (canonical casing) for any acceptable input,
        /// or null if the supplied string can't be matched to a known type.
        /// </summary>
        private static string? NormalizeNoteType(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "General";
            var t = raw.Trim().Replace("_", "").Replace("-", "");
            if (t.Equals("General", StringComparison.OrdinalIgnoreCase)) return "General";
            if (t.Equals("FollowUp", StringComparison.OrdinalIgnoreCase)) return "FollowUp";
            return null;
        }

        private static UserNoteDto MapNote(UserNote n) => new()
        {
            Id = n.Id,
            UserId = n.UserId,
            Type = n.Type,
            Content = n.Content,
            NextOffer = n.NextOffer,
            CreatedByAdminId = n.CreatedByAdminId,
            CreatedByAdminName = n.CreatedByAdminName,
            CreatedAt = n.CreatedAt,
            UpdatedAt = n.UpdatedAt
        };

        private static UserCleaningPhotoDto MapPhoto(UserCleaningPhoto p) => new()
        {
            Id = p.Id,
            UserId = p.UserId,
            OrderId = p.OrderId,
            PhotoUrl = p.PhotoUrl,
            SizeBytes = p.SizeBytes,
            UploadedByAdminName = p.UploadedByAdminName,
            Caption = p.Caption,
            CreatedAt = p.CreatedAt
        };

        /// <summary>
        /// After a new photo is uploaded, prune photos belonging to orders older than the
        /// user's two most recent (distinct) orders. Photos with no OrderId are kept.
        /// Returns the number of pruned photos.
        /// </summary>
        private async Task<int> PruneOldCleaningPhotosAsync(int userId)
        {
            // Distinct order IDs that this user has photos for, ordered by most recent service date
            var orderIdsWithPhotos = await _context.UserCleaningPhotos
                .Where(p => p.UserId == userId && p.OrderId != null)
                .Select(p => p.OrderId!.Value)
                .Distinct()
                .ToListAsync();

            if (orderIdsWithPhotos.Count <= 2) return 0;

            // Resolve each order's service date so we know which two are newest
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
                if (System.IO.File.Exists(fullPath))
                    System.IO.File.Delete(fullPath);
            }
            catch
            {
                // ignore — the DB row is being removed regardless
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
}

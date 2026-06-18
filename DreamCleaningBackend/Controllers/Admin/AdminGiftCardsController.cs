using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Hubs;
using System.Linq;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>Gift cards: listing, activation, configuration and background image.
    /// Split out of the monolithic AdminController; same api/admin route prefix, so URLs are unchanged.</summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminGiftCardsController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IGiftCardService _giftCardService;
        private readonly IAuditService _auditService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AdminGiftCardsController> _logger;

        public AdminGiftCardsController(ApplicationDbContext context,
            IGiftCardService giftCardService,
            IAuditService auditService,
            IConfiguration configuration,
            ILogger<AdminGiftCardsController> logger)
        {
            _context = context;
            _giftCardService = giftCardService;
            _auditService = auditService;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet("gift-cards")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<GiftCardAdminDto>>> GetAllGiftCards()
        {
            try
            {
                var giftCards = await _giftCardService.GetAllGiftCardsForAdmin();
                return Ok(giftCards);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("gift-cards/{id}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<GiftCardAdminDto>> GetGiftCardDetails(int id)
        {
            try
            {
                var giftCard = await _context.GiftCards
                    .Include(g => g.PurchasedByUser)
                    .Include(g => g.GiftCardUsages)
                        .ThenInclude(u => u.User)
                    .Include(g => g.GiftCardUsages)
                        .ThenInclude(u => u.Order)
                    .FirstOrDefaultAsync(g => g.Id == id);

                if (giftCard == null)
                    return NotFound(new { message = "Gift card not found" });

                var dto = new GiftCardAdminDto
                {
                    Id = giftCard.Id,
                    Code = giftCard.Code,
                    OriginalAmount = giftCard.OriginalAmount,
                    CurrentBalance = giftCard.CurrentBalance,
                    RecipientName = giftCard.RecipientName,
                    RecipientEmail = giftCard.RecipientEmail,
                    SenderName = giftCard.SenderName,
                    SenderEmail = giftCard.SenderEmail,
                    Message = giftCard.Message,
                    IsActive = giftCard.IsActive,
                    IsPaid = giftCard.IsPaid,
                    CreatedAt = giftCard.CreatedAt,
                    PaidAt = giftCard.PaidAt,
                    PurchasedByUserName = giftCard.PurchasedByUser.FirstName + " " + giftCard.PurchasedByUser.LastName,
                    TotalAmountUsed = giftCard.OriginalAmount - giftCard.CurrentBalance,
                    TimesUsed = giftCard.GiftCardUsages.Count,
                    LastUsedAt = giftCard.GiftCardUsages.OrderByDescending(u => u.UsedAt).FirstOrDefault()?.UsedAt,
                    Usages = giftCard.GiftCardUsages.Select(u => new GiftCardUsageDto
                    {
                        Id = u.Id,
                        AmountUsed = u.AmountUsed,
                        BalanceAfterUsage = u.BalanceAfterUsage,
                        UsedAt = u.UsedAt,
                        OrderReference = $"Order #{u.OrderId} - ${u.Order.Total:F2}",
                        UsedByName = u.User.FirstName + " " + u.User.LastName,
                        UsedByEmail = u.User.Email
                    }).ToList()
                };

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("gift-cards/{id}/deactivate")]
        [RequirePermission(Permission.Deactivate)]
        public async Task<ActionResult> DeactivateGiftCard(int id)
        {
            try
            {
                var giftCard = await _context.GiftCards.FindAsync(id);
                if (giftCard == null)
                    return NotFound(new { message = "Gift card not found" });

                // CREATE A COPY WITH ALL CURRENT VALUES
                var originalGiftCard = new GiftCard
                {
                    Id = giftCard.Id,
                    Code = giftCard.Code,
                    OriginalAmount = giftCard.OriginalAmount,
                    CurrentBalance = giftCard.CurrentBalance,
                    RecipientName = giftCard.RecipientName,
                    RecipientEmail = giftCard.RecipientEmail,
                    SenderName = giftCard.SenderName,
                    SenderEmail = giftCard.SenderEmail,
                    Message = giftCard.Message,
                    IsActive = giftCard.IsActive,
                    PurchasedByUserId = giftCard.PurchasedByUserId,
                    PaymentIntentId = giftCard.PaymentIntentId,
                    IsPaid = giftCard.IsPaid,
                    PaidAt = giftCard.PaidAt,
                    CreatedAt = giftCard.CreatedAt,
                    UpdatedAt = giftCard.UpdatedAt
                };

                giftCard.IsActive = false;
                giftCard.UpdatedAt = DateTime.UtcNow;

                // Save first
                await _context.SaveChangesAsync();

                // CREATE UPDATED COPY
                var updatedGiftCard = new GiftCard
                {
                    Id = giftCard.Id,
                    Code = giftCard.Code,
                    OriginalAmount = giftCard.OriginalAmount,
                    CurrentBalance = giftCard.CurrentBalance,
                    RecipientName = giftCard.RecipientName,
                    RecipientEmail = giftCard.RecipientEmail,
                    SenderName = giftCard.SenderName,
                    SenderEmail = giftCard.SenderEmail,
                    Message = giftCard.Message,
                    IsActive = giftCard.IsActive,
                    PurchasedByUserId = giftCard.PurchasedByUserId,
                    PaymentIntentId = giftCard.PaymentIntentId,
                    IsPaid = giftCard.IsPaid,
                    PaidAt = giftCard.PaidAt,
                    CreatedAt = giftCard.CreatedAt,
                    UpdatedAt = giftCard.UpdatedAt
                };

                // LOG THE UPDATE
                try
                {
                    await _auditService.LogUpdateAsync(originalGiftCard, updatedGiftCard);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Audit logging failed");
                }

                return Ok(new { message = "Gift card deactivated successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("gift-cards/{id}/activate")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> ActivateGiftCard(int id)
        {
            try
            {
                var giftCard = await _context.GiftCards.FindAsync(id);
                if (giftCard == null)
                    return NotFound(new { message = "Gift card not found" });

                // CREATE A COPY WITH ALL CURRENT VALUES
                var originalGiftCard = new GiftCard
                {
                    Id = giftCard.Id,
                    Code = giftCard.Code,
                    OriginalAmount = giftCard.OriginalAmount,
                    CurrentBalance = giftCard.CurrentBalance,
                    RecipientName = giftCard.RecipientName,
                    RecipientEmail = giftCard.RecipientEmail,
                    SenderName = giftCard.SenderName,
                    SenderEmail = giftCard.SenderEmail,
                    Message = giftCard.Message,
                    IsActive = giftCard.IsActive,
                    PurchasedByUserId = giftCard.PurchasedByUserId,
                    PaymentIntentId = giftCard.PaymentIntentId,
                    IsPaid = giftCard.IsPaid,
                    PaidAt = giftCard.PaidAt,
                    CreatedAt = giftCard.CreatedAt,
                    UpdatedAt = giftCard.UpdatedAt
                };

                giftCard.IsActive = true;
                giftCard.UpdatedAt = DateTime.UtcNow;

                // Save first
                await _context.SaveChangesAsync();

                // CREATE UPDATED COPY
                var updatedGiftCard = new GiftCard
                {
                    Id = giftCard.Id,
                    Code = giftCard.Code,
                    OriginalAmount = giftCard.OriginalAmount,
                    CurrentBalance = giftCard.CurrentBalance,
                    RecipientName = giftCard.RecipientName,
                    RecipientEmail = giftCard.RecipientEmail,
                    SenderName = giftCard.SenderName,
                    SenderEmail = giftCard.SenderEmail,
                    Message = giftCard.Message,
                    IsActive = giftCard.IsActive,
                    PurchasedByUserId = giftCard.PurchasedByUserId,
                    PaymentIntentId = giftCard.PaymentIntentId,
                    IsPaid = giftCard.IsPaid,
                    PaidAt = giftCard.PaidAt,
                    CreatedAt = giftCard.CreatedAt,
                    UpdatedAt = giftCard.UpdatedAt
                };

                // LOG THE UPDATE
                try
                {
                    await _auditService.LogUpdateAsync(originalGiftCard, updatedGiftCard);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Audit logging failed");
                }

                return Ok(new { message = "Gift card activated successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("gift-card-config")]
        [AllowAnonymous] // Allow public access to gift card config
        public async Task<ActionResult> GetGiftCardConfig()
        {
            var config = await _context.GiftCardConfigs.FirstOrDefaultAsync();

            return Ok(new
            {
                backgroundImagePath = config?.BackgroundImagePath ?? "",
                lastUpdated = config?.LastUpdated,
                hasBackground = !string.IsNullOrEmpty(config?.BackgroundImagePath)
            });
        }

        [HttpGet("debug-gift-card-image")]
        [AllowAnonymous] // Debug endpoint to test image loading
        public async Task<ActionResult> DebugGiftCardImage()
        {
            try
            {
                var config = await _context.GiftCardConfigs.FirstOrDefaultAsync();
                var backgroundPath = config?.BackgroundImagePath;
                var fileUploadPath = _configuration["FileUpload:Path"];

                var debugInfo = new
                {
                    configExists = config != null,
                    backgroundPathFromDb = backgroundPath ?? "NULL",
                    fileUploadPath = fileUploadPath ?? "NULL",
                    paths = new List<object>()
                };

                if (!string.IsNullOrEmpty(backgroundPath) && !string.IsNullOrEmpty(fileUploadPath))
                {
                    // Test path 1: Current implementation
                    var normalizedPath = backgroundPath.TrimStart('/', '\\');
                    var pathParts = normalizedPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                    var fullImagePath1 = Path.Combine(new[] { fileUploadPath }.Concat(pathParts).ToArray());
                    
                    // Test path 2: Simple combine
                    var fullImagePath2 = Path.Combine(fileUploadPath, backgroundPath.TrimStart('/'));
                    
                    // Test path 3: Extract filename only
                    var fileName = Path.GetFileName(backgroundPath);
                    var fullImagePath3 = Path.Combine(fileUploadPath, "images", fileName);

                    debugInfo.paths.Add(new
                    {
                        method = "Current implementation (split path)",
                        path = fullImagePath1,
                        exists = System.IO.File.Exists(fullImagePath1),
                        fileSize = System.IO.File.Exists(fullImagePath1) ? new FileInfo(fullImagePath1).Length : 0
                    });

                    debugInfo.paths.Add(new
                    {
                        method = "Simple combine",
                        path = fullImagePath2,
                        exists = System.IO.File.Exists(fullImagePath2),
                        fileSize = System.IO.File.Exists(fullImagePath2) ? new FileInfo(fullImagePath2).Length : 0
                    });

                    debugInfo.paths.Add(new
                    {
                        method = "Filename only",
                        path = fullImagePath3,
                        exists = System.IO.File.Exists(fullImagePath3),
                        fileSize = System.IO.File.Exists(fullImagePath3) ? new FileInfo(fullImagePath3).Length : 0
                    });
                }

                return Ok(debugInfo);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message, stackTrace = ex.StackTrace });
            }
        }

        [HttpPost("upload-gift-card-background")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> UploadGiftCardBackground(IFormFile file)
        {
            try
            {
                if (file == null || file.Length == 0)
                    return BadRequest(new { message = "No file uploaded" });

                // Validate file type
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
                var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

                if (!allowedExtensions.Contains(extension))
                    return BadRequest(new { message = "Invalid file type. Only JPG, PNG, GIF, and WebP are allowed." });

                // Validate file size (e.g., 5MB limit)
                if (file.Length > 5 * 1024 * 1024)
                    return BadRequest(new { message = "File size must be less than 5MB" });

                // Generate unique filename (always .webp)
                var baseFileName = $"gift-card-bg-{DateTime.UtcNow:yyyyMMddHHmmss}";
                var fileName = $"{baseFileName}.webp";

                // Define the path where frontend serves static files
                var uploadPath = Path.Combine(_configuration["FileUpload:Path"], "images");

                // Create directory if it doesn't exist
                Directory.CreateDirectory(uploadPath);

                var filePath = Path.Combine(uploadPath, fileName);

                // Convert to WebP and save with smart resizing
                using (var inputStream = file.OpenReadStream())
                using (var image = await Image.LoadAsync(inputStream))
                {
                    // Define minimum dimensions
                    const int minWidth = 400;
                    const int minHeight = 280;

                    // Calculate if resizing is needed
                    if (image.Width > minWidth || image.Height > minHeight)
                    {
                        // Calculate scale factors
                        var widthScale = (double)minWidth / image.Width;
                        var heightScale = (double)minHeight / image.Height;

                        // Use the larger scale factor to ensure both dimensions meet minimums
                        var scale = Math.Max(widthScale, heightScale);

                        // Only scale down, never up
                        if (scale < 1)
                        {
                            var newWidth = (int)(image.Width * scale);
                            var newHeight = (int)(image.Height * scale);

                            // Resize the image
                            image.Mutate(x => x.Resize(newWidth, newHeight));
                        }
                    }

                    // Save as WebP with good quality
                    var encoder = new WebpEncoder
                    {
                        Quality = 85,
                        Method = WebpEncodingMethod.BestQuality
                    };

                    await image.SaveAsync(filePath, encoder);
                }

                // Verify file was saved
                if (!System.IO.File.Exists(filePath))
                {
                    return BadRequest(new { message = "Failed to save image file" });
                }

                // Update database with new image path
                var relativePath = $"/images/{fileName}";

                var config = await _context.GiftCardConfigs.FirstOrDefaultAsync();
                if (config == null)
                {
                    config = new GiftCardConfig
                    {
                        BackgroundImagePath = relativePath,
                        LastUpdated = DateTime.UtcNow
                    };
                    _context.GiftCardConfigs.Add(config);
                }
                else
                {
                    // Delete old image if exists
                    if (!string.IsNullOrEmpty(config.BackgroundImagePath) &&
                        config.BackgroundImagePath != "/images/mainImage.webp" &&
                        config.BackgroundImagePath != "/images/mainImage.png")
                    {
                        var oldFileName = Path.GetFileName(config.BackgroundImagePath);
                        var oldImagePath = Path.Combine(uploadPath, oldFileName);
                        if (System.IO.File.Exists(oldImagePath))
                        {
                            try
                            {
                                System.IO.File.Delete(oldImagePath);
                            }
                            catch
                            {
                                // Ignore if old file can't be deleted - not critical
                            }
                        }
                    }

                    config.BackgroundImagePath = relativePath;
                    config.LastUpdated = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();

                return Ok(new
                {
                    message = "Image uploaded and converted to WebP successfully",
                    imagePath = relativePath,
                    originalFormat = extension,
                    convertedFormat = ".webp",
                    fileSize = new FileInfo(filePath).Length
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Upload failed: {ex.Message}" });
            }
        }

    }
}

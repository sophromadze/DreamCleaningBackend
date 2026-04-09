using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/admin/rewards")]
    [ApiController]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public class AdminRewardsController : ControllerBase
    {
        private readonly IBubbleRewardsSettingsService _settingsService;
        private readonly IBubblePointsService _pointsService;
        private readonly IReferralService _referralService;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AdminRewardsController> _logger;
        private readonly IAuditService _auditService;

        public AdminRewardsController(
            IBubbleRewardsSettingsService settingsService,
            IBubblePointsService pointsService,
            IReferralService referralService,
            ApplicationDbContext context,
            ILogger<AdminRewardsController> logger,
            IAuditService auditService)
        {
            _settingsService = settingsService;
            _pointsService = pointsService;
            _referralService = referralService;
            _context = context;
            _logger = logger;
            _auditService = auditService;
        }

        // ─── Settings ───────────────────────────────────────────────────────────

        [HttpGet("settings")]
        public async Task<ActionResult<List<BubbleRewardsSettingDto>>> GetSettings()
        {
            try
            {
                var settings = await _settingsService.GetAllSettings();
                return Ok(settings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting rewards settings");
                return StatusCode(500, new { message = "Failed to load settings." });
            }
        }

        [HttpPut("settings/{key}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> UpdateSetting(string key, [FromBody] UpdateSettingDto dto)
        {
            try
            {
                await _settingsService.SetSetting(key, dto.Value);
                return Ok(new { message = "Setting updated." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating setting {Key}", key);
                return StatusCode(500, new { message = "Failed to update setting." });
            }
        }

        [HttpPut("settings/bulk")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> BulkUpdateSettings([FromBody] List<BulkUpdateSettingDto> updates)
        {
            try
            {
                await _settingsService.BulkUpdateSettings(updates);
                return Ok(new { message = $"Updated {updates.Count} settings." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error bulk updating settings");
                return StatusCode(500, new { message = "Failed to update settings." });
            }
        }

        // ─── User Rewards ────────────────────────────────────────────────────────

        [HttpGet("users/{userId}/summary")]
        public async Task<ActionResult<AdminUserRewardsSummaryDto>> GetUserSummary(int userId)
        {
            try
            {
                var user = await _context.Users
                    .Include(u => u.ReferredBy)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user == null) return NotFound();

                var summary = await _pointsService.GetSummary(userId);
                var history = await _pointsService.GetHistory(userId, 1, 50);
                var referrals = await _referralService.GetMyReferrals(userId);

                return Ok(new AdminUserRewardsSummaryDto
                {
                    UserId = userId,
                    UserName = $"{user.FirstName} {user.LastName}",
                    UserEmail = user.Email,
                    CurrentPoints = summary.CurrentPoints,
                    BubbleCredits = summary.BubbleCredits,
                    Tier = summary.Tier,
                    TierEmoji = summary.TierEmoji,
                    TierProgressPercent = summary.TierProgressPercent,
                    NextTierName = summary.NextTierName,
                    AmountToNextTier = summary.AmountToNextTier,
                    TotalSpentAmount = summary.TotalSpentAmount,
                    AvailableRedemptions = summary.AvailableRedemptions,
                    StreakCount = summary.StreakCount,
                    ReferralCode = summary.ReferralCode,
                    ShareUrl = summary.ShareUrl,
                    TotalEarned = summary.TotalEarned,
                    TotalRedeemed = summary.TotalRedeemed,
                    PointsSystemEnabled = summary.PointsSystemEnabled,
                    PointsHistory = history.Items,
                    Referrals = referrals,
                    ReferredByName = user.ReferredBy != null
                        ? user.ReferredBy.Email
                        : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user rewards summary for {UserId}", userId);
                return StatusCode(500, new { message = "Failed to load user rewards." });
            }
        }

        [HttpPost("users/{userId}/adjust-points")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> AdjustPoints(int userId, [FromBody] AdjustPointsDto dto)
        {
            try
            {
                var user = await _context.Users.FindAsync(userId);
                if (user == null) return NotFound();

                await _pointsService.AdminAdjustPoints(userId, dto.Points, dto.Description);

                // Audit log — who, what, why
                var adminIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                var adminNameClaim = $"{User.FindFirst(ClaimTypes.GivenName)?.Value} {User.FindFirst(ClaimTypes.Surname)?.Value}".Trim();
                if (string.IsNullOrWhiteSpace(adminNameClaim))
                    adminNameClaim = User.FindFirst(ClaimTypes.Email)?.Value ?? "Unknown admin";

                if (int.TryParse(adminIdClaim, out int adminId))
                {
                    var targetUserName = $"{user.FirstName} {user.LastName}".Trim();
                    await _auditService.LogBubblePointsAdjustmentAsync(
                        userId, targetUserName, dto.Points, dto.Description, adminId, adminNameClaim);
                }

                return Ok(new { message = $"Adjusted {dto.Points} points for user {userId}." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adjusting points for user {UserId}", userId);
                return StatusCode(500, new { message = "Failed to adjust points." });
            }
        }

        [HttpPost("users/{userId}/grant-review-bonus")]
        public async Task<ActionResult> GrantReviewBonus(int userId)
        {
            try
            {
                var user = await _context.Users.FindAsync(userId);
                if (user == null) return NotFound();

                if (user.ReviewBonusGranted)
                    return BadRequest(new { message = "Review bonus already granted for this user." });

                await _pointsService.AdminGrantReviewBonus(userId);
                return Ok(new { message = "Review bonus granted." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error granting review bonus for user {UserId}", userId);
                return StatusCode(500, new { message = "Failed to grant review bonus." });
            }
        }

        [HttpPost("users/{userId}/grant-credit")]
        public async Task<ActionResult> GrantCredit(int userId, [FromBody] GrantCreditDto dto)
        {
            try
            {
                var user = await _context.Users.FindAsync(userId);
                if (user == null) return NotFound();

                await _pointsService.AdminGrantCredit(userId, dto.Amount, dto.Description);
                return Ok(new { message = $"Granted ${dto.Amount} credit to user {userId}." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error granting credit for user {UserId}", userId);
                return StatusCode(500, new { message = "Failed to grant credit." });
            }
        }

        // ─── Referral Management (SuperAdmin) ───────────────────────────────────

        /// <summary>Search users eligible to be added as a referral for userId:
        /// they must exist, not be deleted, not already have a referrer, and not be the user themselves.</summary>
        [HttpGet("users/{userId}/eligible-referrals")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> SearchEligibleReferrals(int userId, [FromQuery] string query = "")
        {
            try
            {
                var q = _context.Users.Where(u =>
                    !u.IsDeleted &&
                    u.Id != userId &&
                    u.ReferredByUserId == null);

                if (!string.IsNullOrWhiteSpace(query))
                    q = q.Where(u => u.Email.Contains(query) || u.FirstName.Contains(query) || u.LastName.Contains(query));

                var results = await q
                    .OrderBy(u => u.Email)
                    .Take(10)
                    .Select(u => new { u.Id, u.Email, name = u.FirstName + " " + u.LastName })
                    .ToListAsync();

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching eligible referrals");
                return StatusCode(500, new { message = "Failed to search users." });
            }
        }

        /// <summary>Remove a referred user from this user's referral list.
        /// Deletes the Referral row and clears the referred user's ReferredByUserId.</summary>
        [HttpDelete("users/{userId}/referrals/{referralId}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> RemoveReferredUser(int userId, int referralId)
        {
            try
            {
                var referral = await _context.Referrals
                    .Include(r => r.Referred)
                    .FirstOrDefaultAsync(r => r.Id == referralId && r.ReferrerUserId == userId);

                if (referral == null)
                    return NotFound(new { message = "Referral not found." });

                // Clear the referred user's ReferredByUserId
                if (referral.Referred != null)
                    referral.Referred.ReferredByUserId = null;

                _context.Referrals.Remove(referral);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Referred user removed." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing referred user {ReferralId} from user {UserId}", referralId, userId);
                return StatusCode(500, new { message = "Failed to remove referred user." });
            }
        }

        /// <summary>Remove the "referred by" link from this user.
        /// Clears user's ReferredByUserId and deletes the corresponding Referral row.</summary>
        [HttpDelete("users/{userId}/referred-by")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> RemoveReferredBy(int userId)
        {
            try
            {
                var user = await _context.Users.FindAsync(userId);
                if (user == null) return NotFound(new { message = "User not found." });

                if (user.ReferredByUserId == null)
                    return BadRequest(new { message = "User has no referrer." });

                var referrerId = user.ReferredByUserId.Value;
                user.ReferredByUserId = null;

                // Delete the Referral row
                var referral = await _context.Referrals
                    .FirstOrDefaultAsync(r => r.ReferrerUserId == referrerId && r.ReferredUserId == userId);
                if (referral != null)
                    _context.Referrals.Remove(referral);

                await _context.SaveChangesAsync();
                return Ok(new { message = "Referred-by link removed." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing referred-by for user {UserId}", userId);
                return StatusCode(500, new { message = "Failed to remove referred-by." });
            }
        }

        /// <summary>Add a referred user to this user's referrals by email.
        /// Target must exist and must not already have a referrer.</summary>
        [HttpPost("users/{userId}/referrals")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> AddReferredUser(int userId, [FromBody] AddReferralDto dto)
        {
            try
            {
                var referrer = await _context.Users.FindAsync(userId);
                if (referrer == null) return NotFound(new { message = "Referrer user not found." });

                var referred = await _context.Users
                    .FirstOrDefaultAsync(u => u.Email == dto.Email && !u.IsDeleted);

                if (referred == null)
                    return BadRequest(new { message = "No user found with that email." });

                if (referred.Id == userId)
                    return BadRequest(new { message = "A user cannot refer themselves." });

                if (referred.ReferredByUserId != null)
                    return BadRequest(new { message = "That user already has a referrer." });

                // Check for duplicate referral row
                var existing = await _context.Referrals
                    .AnyAsync(r => r.ReferrerUserId == userId && r.ReferredUserId == referred.Id);
                if (existing)
                    return BadRequest(new { message = "Referral already exists." });

                referred.ReferredByUserId = userId;

                var referral = new Referral
                {
                    ReferrerUserId = userId,
                    ReferredUserId = referred.Id,
                    Status = "Registered",
                    RegistrationBonusGiven = false,
                    OrderBonusGiven = false,
                    CreatedAt = DateTime.UtcNow
                };
                _context.Referrals.Add(referral);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Referred user added.", referredUserEmail = referred.Email, referredUserName = $"{referred.FirstName} {referred.LastName}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding referred user to {UserId}", userId);
                return StatusCode(500, new { message = "Failed to add referred user." });
            }
        }

        // ─── Referrals ───────────────────────────────────────────────────────────

        [HttpGet("referrals")]
        public async Task<ActionResult> GetAllReferrals(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] string? status = null)
        {
            try
            {
                var query = _context.Referrals
                    .Include(r => r.Referrer)
                    .Include(r => r.Referred)
                    .AsQueryable();

                if (!string.IsNullOrEmpty(status))
                    query = query.Where(r => r.Status == status);

                var total = await query.CountAsync();
                var items = await query
                    .OrderByDescending(r => r.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(r => new
                    {
                        r.Id,
                        ReferrerName = r.Referrer.FirstName + " " + r.Referrer.LastName,
                        ReferrerEmail = r.Referrer.Email,
                        ReferredName = r.Referred.FirstName + " " + r.Referred.LastName,
                        ReferredEmail = r.Referred.Email,
                        r.Status,
                        r.RegistrationBonusGiven,
                        r.OrderBonusGiven,
                        r.CreatedAt,
                        r.CompletedAt
                    })
                    .ToListAsync();

                return Ok(new { items, total, page, pageSize });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting referrals list");
                return StatusCode(500, new { message = "Failed to load referrals." });
            }
        }

        // ─── Reset ───────────────────────────────────────────────────────────────

        [HttpPost("reset")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> ResetBubblePoints([FromBody] ResetBubblePointsDto dto)
        {
            try
            {
                if (dto.UserId.HasValue)
                {
                    var user = await _context.Users.FindAsync(dto.UserId.Value);
                    if (user == null) return NotFound(new { message = "User not found." });
                    await ResetUserPoints(user);
                }
                else
                {
                    var users = await _context.Users.Where(u => !u.IsDeleted).ToListAsync();
                    foreach (var user in users)
                        await ResetUserPoints(user);
                }

                await _context.SaveChangesAsync();
                return Ok(new { message = dto.UserId.HasValue ? "User bubble points cleared." : "All users' bubble points cleared." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting bubble points");
                return StatusCode(500, new { message = "Failed to reset bubble points." });
            }
        }

        private async Task ResetUserPoints(Models.User user)
        {
            // Delete all point history for this user
            var histories = await _context.BubblePointsHistories
                .Where(h => h.UserId == user.Id)
                .ToListAsync();
            _context.BubblePointsHistories.RemoveRange(histories);

            // Reset all bubble-related fields on the user
            user.BubblePoints = 0;
            user.BubbleCredits = 0;
            user.TotalSpentAmount = 0;
            user.ConsecutiveOrderCount = 0;
            user.LastCompletedOrderDate = null;
            user.WelcomeBonusGranted = false;
            user.ReviewBonusGranted = false;
        }

        // ─── Stats ───────────────────────────────────────────────────────────────

        [HttpGet("stats")]
        public async Task<ActionResult<RewardsStatsDto>> GetStats()
        {
            try
            {
                var totalIssued = await _context.BubblePointsHistories
                    .Where(h => h.Points > 0)
                    .SumAsync(h => (long?)h.Points) ?? 0;

                var totalRedeemed = await _context.BubblePointsHistories
                    .Where(h => h.Points < 0)
                    .SumAsync(h => (long?)h.Points) ?? 0;

                var totalCredits = await _context.Users
                    .SumAsync(u => (decimal?)u.BubbleCredits) ?? 0;

                var activeUsers = await _context.Users
                    .CountAsync(u => u.BubblePoints > 0 && !u.IsDeleted);

                var superMin = 500m;
                var ultraMin = 1500m;

                try
                {
                    var settingsService = HttpContext.RequestServices.GetService<IBubbleRewardsSettingsService>();
                    if (settingsService != null)
                    {
                        superMin = await settingsService.GetSetting<decimal>("TierSuperBubbleMinSpent", 1000);
                        ultraMin = await settingsService.GetSetting<decimal>("TierUltraBubbleMinSpent", 3000);
                    }
                }
                catch { }

                var ultraCount = await _context.Users.CountAsync(u => u.TotalSpentAmount >= ultraMin && !u.IsDeleted);
                var superCount = await _context.Users.CountAsync(u => u.TotalSpentAmount >= superMin && u.TotalSpentAmount < ultraMin && !u.IsDeleted);
                var bubbleCount = await _context.Users.CountAsync(u => u.TotalSpentAmount < superMin && !u.IsDeleted);

                var totalReferrals = await _context.Referrals.CountAsync();
                var completedReferrals = await _context.Referrals.CountAsync(r => r.Status == "OrderCompleted");

                return Ok(new RewardsStatsDto
                {
                    TotalPointsIssued = totalIssued,
                    TotalPointsRedeemed = Math.Abs(totalRedeemed),
                    TotalCreditsIssued = totalCredits,
                    ActiveUsersWithPoints = activeUsers,
                    BubbleTierCount = bubbleCount,
                    SuperBubbleTierCount = superCount,
                    UltraBubbleTierCount = ultraCount,
                    TotalReferrals = totalReferrals,
                    CompletedReferrals = completedReferrals
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting rewards stats");
                return StatusCode(500, new { message = "Failed to load stats." });
            }
        }
    }
}

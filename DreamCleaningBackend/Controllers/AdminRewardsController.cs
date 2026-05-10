using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using System.Security.Claims;
using System.Text.Json;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/admin/rewards")]
    [ApiController]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public class AdminRewardsController : ControllerBase
    {
        private const string BubbleResetSnapshotEntityType = "BubblePointsResetSnapshot";
        private static readonly TimeSpan ResetUndoWindow = TimeSpan.FromHours(48);
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

        /// <summary>Search users that can be set as the referrer of userId:
        /// must exist, not be deleted, and not be the user themselves.
        /// Excludes users that userId has already referred (would create a cycle).</summary>
        [HttpGet("users/{userId}/eligible-referrers")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> SearchEligibleReferrers(int userId, [FromQuery] string query = "")
        {
            try
            {
                var alreadyReferredIds = await _context.Referrals
                    .Where(r => r.ReferrerUserId == userId)
                    .Select(r => r.ReferredUserId)
                    .ToListAsync();

                var q = _context.Users.Where(u =>
                    !u.IsDeleted &&
                    u.Id != userId &&
                    !alreadyReferredIds.Contains(u.Id));

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
                _logger.LogError(ex, "Error searching eligible referrers");
                return StatusCode(500, new { message = "Failed to search users." });
            }
        }

        /// <summary>Set/correct the "referred by" link for this user. Overwrites any existing referrer.
        /// Target referrer must exist, must not be the user themselves, and must not be one of this user's referrals.</summary>
        [HttpPost("users/{userId}/referred-by")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> SetReferredBy(int userId, [FromBody] AddReferralDto dto)
        {
            try
            {
                var user = await _context.Users.FindAsync(userId);
                if (user == null) return NotFound(new { message = "User not found." });

                if (string.IsNullOrWhiteSpace(dto.Email))
                    return BadRequest(new { message = "Email is required." });

                var newReferrer = await _context.Users
                    .FirstOrDefaultAsync(u => u.Email == dto.Email && !u.IsDeleted);

                if (newReferrer == null)
                    return BadRequest(new { message = "No user found with that email." });

                if (newReferrer.Id == userId)
                    return BadRequest(new { message = "A user cannot be their own referrer." });

                // Cycle guard: the new referrer must not be one of this user's referrals.
                var wouldCycle = await _context.Referrals
                    .AnyAsync(r => r.ReferrerUserId == userId && r.ReferredUserId == newReferrer.Id);
                if (wouldCycle)
                    return BadRequest(new { message = "Cannot set referrer to a user that this user has already referred." });

                // If the new referrer is the same as the current one, no-op.
                if (user.ReferredByUserId == newReferrer.Id)
                    return Ok(new { message = "User is already referred by this person.", referredByName = newReferrer.Email });

                // Remove the old Referral row, if any.
                if (user.ReferredByUserId != null)
                {
                    var oldReferrerId = user.ReferredByUserId.Value;
                    var oldReferral = await _context.Referrals
                        .FirstOrDefaultAsync(r => r.ReferrerUserId == oldReferrerId && r.ReferredUserId == userId);
                    if (oldReferral != null)
                        _context.Referrals.Remove(oldReferral);
                }

                user.ReferredByUserId = newReferrer.Id;

                // Avoid duplicate row in case one already exists (shouldn't, but be safe).
                var existing = await _context.Referrals
                    .AnyAsync(r => r.ReferrerUserId == newReferrer.Id && r.ReferredUserId == userId);
                if (!existing)
                {
                    _context.Referrals.Add(new Referral
                    {
                        ReferrerUserId = newReferrer.Id,
                        ReferredUserId = userId,
                        Status = "Registered",
                        RegistrationBonusGiven = false,
                        OrderBonusGiven = false,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await _context.SaveChangesAsync();
                return Ok(new { message = "Referrer set.", referredByName = newReferrer.Email });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting referred-by for user {UserId}", userId);
                return StatusCode(500, new { message = "Failed to set referrer." });
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
                var usersToReset = dto.UserId.HasValue
                    ? await _context.Users.Where(u => u.Id == dto.UserId.Value).ToListAsync()
                    : await _context.Users.Where(u => !u.IsDeleted).ToListAsync();

                if (usersToReset.Count == 0)
                {
                    return dto.UserId.HasValue
                        ? NotFound(new { message = "User not found." })
                        : BadRequest(new { message = "No users found to reset." });
                }

                var targetUserIds = usersToReset.Select(u => u.Id).ToList();
                var histories = await _context.BubblePointsHistories
                    .Where(h => targetUserIds.Contains(h.UserId))
                    .ToListAsync();

                var snapshot = new ResetSnapshotPayload
                {
                    Scope = dto.UserId.HasValue ? "specific" : "all",
                    TargetUserId = dto.UserId,
                    CreatedAtUtc = DateTime.UtcNow,
                    Users = usersToReset.Select(u => new ResetSnapshotUserData
                    {
                        UserId = u.Id,
                        BubblePoints = u.BubblePoints,
                        BubbleCredits = u.BubbleCredits,
                        TotalSpentAmount = u.TotalSpentAmount,
                        ConsecutiveOrderCount = u.ConsecutiveOrderCount,
                        LastCompletedOrderDate = u.LastCompletedOrderDate,
                        WelcomeBonusGranted = u.WelcomeBonusGranted,
                        ReviewBonusGranted = u.ReviewBonusGranted
                    }).ToList(),
                    Histories = histories.Select(h => new ResetSnapshotHistoryData
                    {
                        UserId = h.UserId,
                        Points = h.Points,
                        Type = h.Type,
                        Description = h.Description,
                        OrderId = h.OrderId,
                        CreatedAt = h.CreatedAt
                    }).ToList()
                };

                var adminUserId = GetCurrentUserId();
                _context.AuditLogs.Add(new AuditLog
                {
                    EntityType = BubbleResetSnapshotEntityType,
                    EntityId = dto.UserId ?? 0,
                    Action = "Create",
                    NewValues = JsonSerializer.Serialize(snapshot),
                    ChangedFields = JsonSerializer.Serialize(new[] { "BubblePointsResetSnapshot" }),
                    UserId = adminUserId,
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = HttpContext.Request.Headers.UserAgent.ToString()
                });

                foreach (var user in usersToReset)
                    await ResetUserPoints(user);

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

        [HttpGet("reset/undo-status")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> GetUndoStatus()
        {
            var latestSnapshot = await GetLatestRestorableSnapshot();
            if (latestSnapshot == null)
            {
                return Ok(new { available = false });
            }

            return Ok(new
            {
                available = true,
                createdAt = latestSnapshot.CreatedAt,
                scope = latestSnapshot.EntityId == 0 ? "all" : "specific"
            });
        }

        [HttpPost("reset/undo")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> UndoLastReset()
        {
            try
            {
                var latestSnapshot = await GetLatestRestorableSnapshot();
                if (latestSnapshot == null || string.IsNullOrWhiteSpace(latestSnapshot.NewValues))
                {
                    return BadRequest(new { message = "No reset snapshot is available to undo." });
                }

                var snapshot = JsonSerializer.Deserialize<ResetSnapshotPayload>(latestSnapshot.NewValues);
                if (snapshot == null || snapshot.Users.Count == 0)
                {
                    return BadRequest(new { message = "Reset snapshot data is invalid." });
                }

                var userIds = snapshot.Users.Select(u => u.UserId).ToList();
                var users = await _context.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);

                var existingHistories = await _context.BubblePointsHistories
                    .Where(h => userIds.Contains(h.UserId))
                    .ToListAsync();
                _context.BubblePointsHistories.RemoveRange(existingHistories);

                foreach (var userSnapshot in snapshot.Users)
                {
                    if (!users.TryGetValue(userSnapshot.UserId, out var user)) continue;
                    user.BubblePoints = userSnapshot.BubblePoints;
                    user.BubbleCredits = userSnapshot.BubbleCredits;
                    user.TotalSpentAmount = userSnapshot.TotalSpentAmount;
                    user.ConsecutiveOrderCount = userSnapshot.ConsecutiveOrderCount;
                    user.LastCompletedOrderDate = userSnapshot.LastCompletedOrderDate;
                    user.WelcomeBonusGranted = userSnapshot.WelcomeBonusGranted;
                    user.ReviewBonusGranted = userSnapshot.ReviewBonusGranted;
                }

                var restoredHistories = snapshot.Histories
                    .Where(h => users.ContainsKey(h.UserId))
                    .Select(h => new BubblePointsHistory
                    {
                        UserId = h.UserId,
                        Points = h.Points,
                        Type = h.Type,
                        Description = h.Description,
                        OrderId = h.OrderId,
                        CreatedAt = h.CreatedAt
                    })
                    .ToList();
                _context.BubblePointsHistories.AddRange(restoredHistories);

                _context.AuditLogs.Add(new AuditLog
                {
                    EntityType = BubbleResetSnapshotEntityType,
                    EntityId = latestSnapshot.Id,
                    Action = "Restore",
                    NewValues = JsonSerializer.Serialize(new { message = "Undo reset executed." }),
                    ChangedFields = JsonSerializer.Serialize(new[] { "ResetUndo" }),
                    UserId = GetCurrentUserId(),
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = HttpContext.Request.Headers.UserAgent.ToString()
                });

                await _context.SaveChangesAsync();
                return Ok(new { message = "Successfully restored bubble points from the last reset snapshot." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error undoing bubble points reset");
                return StatusCode(500, new { message = "Failed to undo the last reset." });
            }
        }

        private async Task<AuditLog?> GetLatestRestorableSnapshot()
        {
            var cutoff = DateTime.UtcNow.Subtract(ResetUndoWindow);
            var snapshots = await _context.AuditLogs
                .Where(a =>
                    a.EntityType == BubbleResetSnapshotEntityType &&
                    a.Action == "Create" &&
                    a.CreatedAt >= cutoff)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync();

            foreach (var snapshot in snapshots)
            {
                var alreadyRestored = await _context.AuditLogs.AnyAsync(a =>
                    a.EntityType == BubbleResetSnapshotEntityType &&
                    a.Action == "Restore" &&
                    a.EntityId == snapshot.Id);
                if (!alreadyRestored) return snapshot;
            }

            return null;
        }

        private int? GetCurrentUserId()
        {
            var adminIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(adminIdClaim, out var adminId) ? adminId : null;
        }

        private class ResetSnapshotPayload
        {
            public string Scope { get; set; } = string.Empty;
            public int? TargetUserId { get; set; }
            public DateTime CreatedAtUtc { get; set; }
            public List<ResetSnapshotUserData> Users { get; set; } = new();
            public List<ResetSnapshotHistoryData> Histories { get; set; } = new();
        }

        private class ResetSnapshotUserData
        {
            public int UserId { get; set; }
            public int BubblePoints { get; set; }
            public decimal BubbleCredits { get; set; }
            public decimal TotalSpentAmount { get; set; }
            public int ConsecutiveOrderCount { get; set; }
            public DateTime? LastCompletedOrderDate { get; set; }
            public bool WelcomeBonusGranted { get; set; }
            public bool ReviewBonusGranted { get; set; }
        }

        private class ResetSnapshotHistoryData
        {
            public int UserId { get; set; }
            public int Points { get; set; }
            public string Type { get; set; } = string.Empty;
            public string? Description { get; set; }
            public int? OrderId { get; set; }
            public DateTime CreatedAt { get; set; }
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

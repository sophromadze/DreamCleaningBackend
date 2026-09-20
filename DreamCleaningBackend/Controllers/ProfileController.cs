using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Data;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Helpers;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ProfileController : ControllerBase
    {
        private readonly IProfileService _profileService;
        private readonly IAuditService _auditService; 
        private readonly ISpecialOfferService _specialOfferService;
        private readonly ISubscriptionService _subscriptionService;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<ProfileController> _logger;

        public ProfileController(
            IProfileService profileService,
            IAuditService auditService,
            ISpecialOfferService specialOfferService,
            ISubscriptionService subscriptionService,
            ApplicationDbContext context,
            ILogger<ProfileController> logger)
        {
            _logger = logger;
            _profileService = profileService;
            _auditService = auditService;
            _specialOfferService = specialOfferService;
            _subscriptionService = subscriptionService;
            _context = context; 
        }

        [HttpGet]
        public async Task<ActionResult<ProfileDto>> GetProfile()
        {
            try
            {
                var userId = GetUserId();
                // Ensure expired subscriptions are cleared before returning profile data.
                await _subscriptionService.CheckAndUpdateSubscriptionStatus(userId);
                var profile = await _profileService.GetProfile(userId);
                return Ok(profile);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut]
        public async Task<ActionResult<ProfileDto>> UpdateProfile(UpdateProfileDto updateProfileDto)
        {
            try
            {
                var userId = GetUserId();

                // Get the user before update for auditing
                var userBeforeUpdate = await _context.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (userBeforeUpdate == null)
                    return NotFound();

                // Call the service to update
                var profile = await _profileService.UpdateProfile(userId, updateProfileDto);

                // Get the user after update for auditing
                var userAfterUpdate = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == userId);

                // Log the audit
                if (userAfterUpdate != null)
                {
                    try
                    {
                        await _auditService.LogUpdateAsync(userBeforeUpdate, userAfterUpdate);
                    }
                    catch (Exception ex)
                    {
                        // Don't fail the operation if audit fails
                        _logger.LogError(ex, "Audit logging failed");
                    }
                }

                return Ok(profile);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("apartments")]
        public async Task<ActionResult<List<ApartmentDto>>> GetApartments()
        {
            try
            {
                var userId = GetUserId();
                var apartments = await _profileService.GetUserApartments(userId);
                return Ok(apartments);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("apartments")]
        public async Task<ActionResult<ApartmentDto>> AddApartment(CreateApartmentDto createApartmentDto)
        {
            try
            {
                var userId = GetUserId();
                var apartment = await _profileService.AddApartment(userId, createApartmentDto);

                // Log the creation
                try
                {
                    var createdApartment = await _context.Apartments
                        .FirstOrDefaultAsync(a => a.Id == apartment.Id);

                    if (createdApartment != null)
                    {
                        await _auditService.LogCreateAsync(createdApartment);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Audit logging failed");
                }

                return Ok(apartment);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut("apartments/{apartmentId}")]
        public async Task<ActionResult<ApartmentDto>> UpdateApartment(int apartmentId, ApartmentDto apartmentDto)
        {
            try
            {
                var userId = GetUserId();

                // Get the apartment before update
                var apartmentBefore = await _context.Apartments.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == apartmentId && a.UserId == userId);

                if (apartmentBefore == null)
                    return NotFound();

                var apartment = await _profileService.UpdateApartment(userId, apartmentId, apartmentDto);

                // Get the apartment after update
                var apartmentAfter = await _context.Apartments
                    .FirstOrDefaultAsync(a => a.Id == apartmentId);

                // Log the update
                if (apartmentAfter != null)
                {
                    try
                    {
                        await _auditService.LogUpdateAsync(apartmentBefore, apartmentAfter);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Audit logging failed");
                    }
                }

                return Ok(apartment);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("apartments/{apartmentId}")]
        public async Task<ActionResult> DeleteApartment(int apartmentId)
        {
            try
            {
                var userId = GetUserId();

                // Get the apartment before deletion
                var apartment = await _context.Apartments
                    .FirstOrDefaultAsync(a => a.Id == apartmentId && a.UserId == userId);

                if (apartment == null)
                    return NotFound();

                await _profileService.DeleteApartment(userId, apartmentId);

                // Log the deletion
                try
                {
                    await _auditService.LogDeleteAsync(apartment);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Audit logging failed");
                }

                return Ok(new { message = "Apartment deleted successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("special-offers")]
        public async Task<ActionResult<List<UserSpecialOfferDto>>> GetMySpecialOffers()
        {
            try
            {
                var userId = GetUserId();
                var offers = await _specialOfferService.GetUserAvailableOffers(userId);
                return Ok(offers);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }


        // ══ The customer's plan (2026-09) ══════════════════════════════════════════════════
        //
        // Read Helpers/PlanSelectionPolicy before touching these. Choosing a plan here records a
        // PREFERENCE: it pre-selects the tier on the booking page and does NOTHING else. It never
        // writes User.SubscriptionId, never moves the expiry, never grants a discount and never
        // charges. A plan becomes real by booking a cleaning on it, exactly as before.

        [HttpGet("plan")]
        public async Task<ActionResult<PlanOverviewDto>> GetPlan()
        {
            try
            {
                var userId = GetUserId();

                // Clear a lapsed plan first, so the tab can never show an expired one as live.
                await _subscriptionService.CheckAndUpdateSubscriptionStatus(userId);

                var user = await _context.Users
                    .Include(u => u.Subscription)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user == null) return NotFound(new { message = "User not found" });

                var tiers = await _context.Subscriptions
                    .AsNoTracking()
                    .Where(s => s.IsActive && s.SubscriptionDays > 0)
                    .OrderBy(s => s.DisplayOrder)
                    .ToListAsync();

                var now = DateTime.UtcNow;
                var planIsLive = RecurringPlanRule.IsActiveUserSubscription(user, now);

                var overview = new PlanOverviewDto
                {
                    PreferredSubscriptionId = user.PreferredSubscriptionId,
                    ActiveSubscriptionId = planIsLive ? user.SubscriptionId : null,
                    ActiveSubscriptionName = planIsLive ? user.Subscription?.Name : null,
                    ActiveDiscountPercentage = planIsLive ? user.Subscription?.DiscountPercentage : null,
                    ActiveExpiresAt = planIsLive ? user.SubscriptionExpiryDate : null,
                    // "First" means no cleaning has ever been booked on the account, which is the
                    // only case where the wording can promise the discount starts on the next one
                    // but one. FirstTimeOrder is cleared when an order is paid for.
                    NextCleaningIsFirstOnPlan = !planIsLive,
                    Plans = tiers.Select(s => new PlanOptionDto
                    {
                        Id = s.Id,
                        Name = s.Name,
                        Description = s.Description,
                        DiscountPercentage = s.DiscountPercentage,
                        SubscriptionDays = s.SubscriptionDays,
                        DisplayOrder = s.DisplayOrder,
                        IsPreferred = user.PreferredSubscriptionId == s.Id,
                        IsActive = PlanSelectionPolicy.NextCleaningWouldBeDiscounted(user, s.SubscriptionDays, now)
                    }).ToList()
                };

                return Ok(overview);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut("plan")]
        public async Task<ActionResult<PlanOverviewDto>> SelectPlan(SelectPlanDto dto)
        {
            try
            {
                var userId = GetUserId();
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null) return NotFound(new { message = "User not found" });

                if (dto.SubscriptionId.HasValue)
                {
                    var tier = await _context.Subscriptions
                        .AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == dto.SubscriptionId.Value);

                    // One Time is refused by IsSelectable: it is the absence of a plan, and the
                    // way off a plan is "no plan" (a null SubscriptionId), not a one-off tier.
                    if (!PlanSelectionPolicy.IsSelectable(tier))
                        return BadRequest(new { message = "That plan isn't available." });

                    user.PreferredSubscriptionId = tier!.Id;
                    user.PreferredSubscriptionSelectedAt = DateTime.UtcNow;
                }
                else
                {
                    user.PreferredSubscriptionId = null;
                    user.PreferredSubscriptionSelectedAt = null;
                }

                user.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                return await GetPlan();
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
        private int GetUserId()
        {
            // Try "UserId" first (what your JWT probably uses), then fallback to NameIdentifier
            var userIdClaim = User.FindFirst("UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                throw new Exception("Invalid user");

            return userId;
        }
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/referral")]
    [ApiController]
    [Authorize]
    public class ReferralController : ControllerBase
    {
        private readonly IReferralService _referralService;
        private readonly IBubblePointsService _bubblePointsService;
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ReferralController> _logger;

        public ReferralController(
            IReferralService referralService,
            IBubblePointsService bubblePointsService,
            ApplicationDbContext context,
            IConfiguration configuration,
            ILogger<ReferralController> logger)
        {
            _referralService = referralService;
            _bubblePointsService = bubblePointsService;
            _context = context;
            _configuration = configuration;
            _logger = logger;
        }

        private int GetUserId()
        {
            var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("nameid")?.Value;
            return int.Parse(idClaim ?? "0");
        }

        [HttpGet("my-code")]
        public async Task<ActionResult> GetMyCode()
        {
            try
            {
                var userId = GetUserId();
                var user = await _context.Users.FindAsync(userId);
                if (user == null) return NotFound();

                if (string.IsNullOrEmpty(user.ReferralCode))
                {
                    user.ReferralCode = await _referralService.GenerateReferralCode(userId);
                    await _context.SaveChangesAsync();
                }

                var frontendUrl = _configuration["Frontend:Url"] ?? "https://dreamcleaningnearme.com";
                return Ok(new
                {
                    code = user.ReferralCode,
                    shareUrl = $"{frontendUrl}/?ref={user.ReferralCode}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting referral code");
                return StatusCode(500, new { message = "Failed to get referral code." });
            }
        }

        [HttpGet("my-referrals")]
        public async Task<ActionResult<List<ReferralDto>>> GetMyReferrals()
        {
            try
            {
                var userId = GetUserId();
                var referrals = await _referralService.GetMyReferrals(userId);
                return Ok(referrals);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting referrals");
                return StatusCode(500, new { message = "Failed to get referrals." });
            }
        }

        [HttpPost("validate")]
        [AllowAnonymous]
        public async Task<ActionResult<ReferralValidationResult>> ValidateCode([FromBody] ValidateReferralCodeDto dto)
        {
            try
            {
                var result = await _referralService.ValidateCode(dto.Code);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating referral code");
                return StatusCode(500, new { message = "Failed to validate code." });
            }
        }
    }
}

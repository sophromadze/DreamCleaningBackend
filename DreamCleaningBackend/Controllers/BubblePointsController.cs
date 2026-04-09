using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/bubble-points")]
    [ApiController]
    [Authorize]
    public class BubblePointsController : ControllerBase
    {
        private readonly IBubblePointsService _bubblePointsService;
        private readonly ILogger<BubblePointsController> _logger;

        public BubblePointsController(IBubblePointsService bubblePointsService, ILogger<BubblePointsController> logger)
        {
            _bubblePointsService = bubblePointsService;
            _logger = logger;
        }

        private int GetUserId()
        {
            var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("nameid")?.Value;
            return int.Parse(idClaim ?? "0");
        }

        [HttpGet("summary")]
        public async Task<ActionResult<BubbleRewardsSummaryDto>> GetSummary()
        {
            try
            {
                var userId = GetUserId();
                var summary = await _bubblePointsService.GetSummary(userId);
                return Ok(summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting bubble rewards summary");
                return StatusCode(500, new { message = "Failed to load rewards summary." });
            }
        }

        [HttpGet("header-summary")]
        public async Task<ActionResult<HeaderSummaryDto>> GetHeaderSummary()
        {
            try
            {
                var userId = GetUserId();
                var summary = await _bubblePointsService.GetHeaderSummary(userId);
                return Ok(summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting header summary");
                return StatusCode(500, new { message = "Failed to load header summary." });
            }
        }

        [HttpGet("history")]
        public async Task<ActionResult<PagedResult<BubblePointsHistoryDto>>> GetHistory(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            try
            {
                var userId = GetUserId();
                var result = await _bubblePointsService.GetHistory(userId, page, pageSize);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting points history");
                return StatusCode(500, new { message = "Failed to load history." });
            }
        }

        [HttpPost("redeem")]
        public async Task<ActionResult<RedemptionResultDto>> RedeemPoints([FromBody] RedeemPointsDto dto)
        {
            try
            {
                var userId = GetUserId();
                var result = await _bubblePointsService.RedeemPoints(userId, dto.Points, dto.OrderId);
                if (!result.Success)
                    return BadRequest(new { message = result.Message });
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error redeeming points");
                return StatusCode(500, new { message = "Failed to redeem points." });
            }
        }
    }
}

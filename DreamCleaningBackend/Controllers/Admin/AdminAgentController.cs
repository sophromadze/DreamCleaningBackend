using DreamCleaningBackend.Services.AdminAgent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// The SuperAdmin analytics agent (2026-10): an internal chat that answers business questions
    /// from real data. Entirely separate from the public chat agent (ChatController) — its own
    /// endpoints, prompt, tools, tables and rate limit.
    ///
    /// <b>SuperAdmin only, enforced HERE.</b> The class carries the <see cref="Policy"/> policy,
    /// so every action — present and future — answers 403 to Admins, Moderators, cleaners and
    /// customers and 401 to anonymous callers. The hidden tab and the route guard in the admin
    /// panel are conveniences; this attribute is the boundary (<c>AdminAgentAccessTests</c>).
    /// On top of that every session is scoped to its owner, so one SuperAdmin cannot reach
    /// another's conversations.
    /// </summary>
    [Route("api/admin/agent")]
    [ApiController]
    [Authorize(Policy = Policy)]
    public class AdminAgentController : AdminControllerBase
    {
        public const string Policy = "AdminAgentSuperAdmin";
        public const string RateLimitPolicy = "admin-agent";

        /// <summary>Registers <see cref="Policy"/>: an authenticated SuperAdmin, nobody else.
        /// Called by Program.cs and by AdminAgentAccessTests, so the test checks the real rule.</summary>
        public static void AddPolicy(AuthorizationOptions options) =>
            options.AddPolicy(Policy, policy => policy.RequireAuthenticatedUser().RequireRole("SuperAdmin"));

        private readonly IAdminAgentService _agent;

        public AdminAgentController(IAdminAgentService agent)
        {
            _agent = agent;
        }

        /// <summary>Whether the agent and its SQL connection are configured, and the model in use.</summary>
        [HttpGet("status")]
        public ActionResult<AdminAgentStatusDto> GetStatus() => Ok(_agent.GetStatus());

        [HttpGet("sessions")]
        public async Task<ActionResult<List<AdminAgentSessionDto>>> GetSessions()
            => Ok(await _agent.ListSessionsAsync(GetCurrentUserId()));

        [HttpPost("sessions")]
        public async Task<ActionResult<AdminAgentSessionDto>> CreateSession([FromBody] AdminAgentCreateSessionDto? dto)
            => Ok(await _agent.CreateSessionAsync(GetCurrentUserId(), dto?.Title));

        [HttpGet("sessions/{sessionId:int}")]
        public async Task<ActionResult<AdminAgentSessionDetailDto>> GetSession(int sessionId)
        {
            var detail = await _agent.GetSessionAsync(GetCurrentUserId(), sessionId);
            return detail == null ? NotFound(new { message = "Chat not found." }) : Ok(detail);
        }

        [HttpPut("sessions/{sessionId:int}")]
        public async Task<ActionResult<AdminAgentSessionDto>> RenameSession(int sessionId, [FromBody] AdminAgentRenameSessionDto dto)
        {
            try
            {
                var session = await _agent.RenameSessionAsync(GetCurrentUserId(), sessionId, dto?.Title ?? string.Empty);
                return session == null ? NotFound(new { message = "Chat not found." }) : Ok(session);
            }
            catch (AdminAgentRequestException ex)
            {
                return StatusCode(ex.StatusCode, new { message = ex.Message });
            }
        }

        [HttpDelete("sessions/{sessionId:int}")]
        public async Task<IActionResult> DeleteSession(int sessionId)
            => await _agent.DeleteSessionAsync(GetCurrentUserId(), sessionId)
                ? NoContent()
                : NotFound(new { message = "Chat not found." });

        /// <summary>
        /// Asks a question. Returns at once with the stored question and a "running" answer; the
        /// answer arrives over SignalR (AdminAgentProgress / AdminAgentTurnCompleted) and can always
        /// be re-read with the message endpoint below. <c>inputMode</c> is "text" today — a later
        /// voice front end posts its transcript here with "voice".
        /// </summary>
        [HttpPost("sessions/{sessionId:int}/messages")]
        [EnableRateLimiting(RateLimitPolicy)]
        public async Task<ActionResult<AdminAgentTurnStartedDto>> Ask(int sessionId, [FromBody] AdminAgentAskDto dto)
        {
            try
            {
                return Ok(await _agent.StartTurnAsync(GetCurrentUserId(), sessionId, dto?.Text ?? string.Empty, dto?.InputMode));
            }
            catch (AdminAgentRequestException ex)
            {
                return StatusCode(ex.StatusCode, new { message = ex.Message });
            }
        }

        [HttpGet("sessions/{sessionId:int}/messages/{messageId:long}")]
        public async Task<ActionResult<AdminAgentMessageDto>> GetMessage(int sessionId, long messageId)
        {
            var message = await _agent.GetMessageAsync(GetCurrentUserId(), sessionId, messageId);
            return message == null ? NotFound(new { message = "Message not found." }) : Ok(message);
        }
    }

    public class AdminAgentCreateSessionDto
    {
        public string? Title { get; set; }
    }

    public class AdminAgentRenameSessionDto
    {
        public string Title { get; set; } = string.Empty;
    }

    public class AdminAgentAskDto
    {
        public string Text { get; set; } = string.Empty;

        /// <summary>"text" (default) or "voice" (a speech transcript - reserved for later).</summary>
        public string? InputMode { get; set; }
    }
}

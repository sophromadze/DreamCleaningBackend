using DreamCleaningBackend.Services.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// Admin → Claude Connector (2026-10): the connector's URL, the live connector sessions (user,
    /// client, created, last used) and Revoke. SuperAdmin only, under the same policy as the
    /// analytics agent it exposes.
    /// </summary>
    [Route("api/admin/mcp")]
    [ApiController]
    [Authorize(Policy = AdminAgentController.Policy)]
    public class AdminMcpSessionsController : AdminControllerBase
    {
        private readonly IMcpOAuthService _oauth;
        private readonly IConfiguration _configuration;

        public AdminMcpSessionsController(IMcpOAuthService oauth, IConfiguration configuration)
        {
            _oauth = oauth;
            _configuration = configuration;
        }

        [HttpGet("sessions")]
        public async Task<ActionResult<object>> GetSessions() => Ok(new
        {
            serverUrl = McpOAuthPolicy.ResourceUrl(_configuration),
            sessions = await _oauth.ListActiveSessionsAsync()
        });

        [HttpDelete("sessions/{grantId:int}")]
        public async Task<IActionResult> Revoke(int grantId)
            => await _oauth.RevokeGrantAsync(grantId, GetCurrentUserId(), "Revoked in the admin panel")
                ? NoContent()
                : NotFound(new { message = "Session not found or already ended." });
    }
}

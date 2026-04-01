using DreamCleaningBackend.Hubs;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace DreamCleaningBackend.Controllers;

[ApiController]
[Route("api/livechat")]
public class LiveChatAdminController : ControllerBase
{
    private readonly LiveChatSessionManager _sessionManager;
    private readonly IHubContext<LiveChatHub> _hubContext;

    public LiveChatAdminController(
        LiveChatSessionManager sessionManager,
        IHubContext<LiveChatHub> hubContext)
    {
        _sessionManager = sessionManager;
        _hubContext = hubContext;
    }

    /// <summary>
    /// Returns whether the chat widget is currently enabled.
    /// Anonymous — called by the widget on every page load.
    /// </summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public IActionResult GetStatus()
    {
        return Ok(new { isEnabled = _sessionManager.IsChatEnabled });
    }

    /// <summary>
    /// Toggles the chat widget on/off. Admin+ only.
    /// Broadcasts the new state to all connected LiveChatHub clients.
    /// </summary>
    [HttpPost("toggle")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> Toggle()
    {
        var isEnabled = _sessionManager.ToggleChatEnabled();

        // Notify all visitors who are currently connected to LiveChatHub
        var eventName = isEnabled ? "ChatWidgetEnabled" : "ChatWidgetDisabled";
        await _hubContext.Clients.All.SendAsync(eventName);

        return Ok(new { isEnabled });
    }
}

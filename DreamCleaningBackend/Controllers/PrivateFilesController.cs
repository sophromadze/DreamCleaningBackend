using System.Security.Claims;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// The only way private uploads leave the server (see Helpers/PrivateFiles.cs). Every request
    /// is checked against the signed-in account, and EVERY refusal is a plain 404 — anonymous,
    /// wrong account, unknown id and missing file all look the same, so the endpoint never says
    /// whether a file exists.
    ///
    /// Deliberately no [Authorize]: that would answer anonymous callers 401 and staff-only routes
    /// 403. Authentication still runs (the JWT comes from the access_token cookie in production,
    /// which a same-origin &lt;img src&gt; sends), and each action decides for itself.
    ///
    /// Who may see what — each staff rule is the gate of the page that shows the file today:
    ///   cleaner photo / document → staff holding View (Cleaners dashboard: Admin, SuperAdmin,
    ///                              Moderator), or the cleaner whose login account it is
    ///   customer cleaning photo  → staff holding View (Users / Orders panels), or the customer
    ///   chat photo               → Admin / SuperAdmin (Chats tab), or a signed transcript link;
    ///                              the visitor's own copy is ChatController's session route
    /// </summary>
    [Route("api/files")]
    [ApiController]
    public class PrivateFilesController : ControllerBase
    {
        private static readonly string[] PanelRoles = { "Admin", "SuperAdmin", "Moderator" };
        private static readonly string[] ChatRoles = { "Admin", "SuperAdmin" };

        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IPermissionService _permissions;

        public PrivateFilesController(ApplicationDbContext context, IConfiguration configuration, IPermissionService permissions)
        {
            _context = context;
            _configuration = configuration;
            _permissions = permissions;
        }

        [HttpGet("cleaners/{cleanerId:int}/photo")]
        public Task<IActionResult> CleanerPhoto(int cleanerId) => ServeCleanerFile(cleanerId, document: false);

        [HttpGet("cleaners/{cleanerId:int}/document")]
        public Task<IActionResult> CleanerDocument(int cleanerId) => ServeCleanerFile(cleanerId, document: true);

        [HttpGet("cleaning-photos/{photoId:int}")]
        public async Task<IActionResult> CleaningPhoto(int photoId)
        {
            var userId = CurrentUserId();
            if (userId == null) return NotFound();

            var photo = await _context.UserCleaningPhotos
                .AsNoTracking()
                .Where(p => p.Id == photoId)
                .Select(p => new { p.UserId, p.PhotoUrl })
                .FirstOrDefaultAsync();
            if (photo == null) return NotFound();
            if (!IsStaffWithView() && photo.UserId != userId) return NotFound();

            var full = PrivateFileStore.Resolve(UploadRoot, photo.PhotoUrl, PrivateFileUrls.CleaningPhotosFolder);
            return full == null
                ? NotFound()
                : PrivateFileResponse.Serve(this, full, $"cleaning-photo-{photoId}", PrivateFileResponse.NoStore);
        }

        /// <summary>
        /// Staff view of a chat photo (admin Chats tab), or the expiring link printed in the
        /// escalation transcript (Telegram topic / company email — no cookie there).
        /// </summary>
        [HttpGet("chat/{fileName}")]
        public async Task<IActionResult> ChatPhoto(string fileName, [FromQuery] long? exp, [FromQuery] string? sig)
        {
            if (!PrivateFileUrls.IsChatFileName(fileName)) return NotFound();

            var allowed = HasAnyRole(ChatRoles)
                || PrivateFileLinkSigner.IsValidChatLink(_configuration["AppSettings:Token"], fileName, exp, sig, DateTime.UtcNow);
            if (!allowed) return NotFound();

            // Only files that belong to a stored message — an uploaded-but-never-sent file is nobody's.
            var stored = PrivateFileUrls.StoredChatPath(fileName);
            var referenced = await _context.ChatAgentMessages.AsNoTracking().AnyAsync(m => m.ImagePath == stored);
            if (!referenced) return NotFound();

            var full = PrivateFileStore.Resolve(UploadRoot, stored, PrivateFileUrls.ChatPhotosFolder);
            return full == null
                ? NotFound()
                : PrivateFileResponse.Serve(this, full, "chat-photo", PrivateFileResponse.NoStore);
        }

        private async Task<IActionResult> ServeCleanerFile(int cleanerId, bool document)
        {
            var userId = CurrentUserId();
            if (userId == null) return NotFound();

            var cleaner = await _context.Cleaners
                .AsNoTracking()
                .Where(c => c.Id == cleanerId)
                .Select(c => new { c.UserId, c.PhotoUrl, c.DocumentUrl })
                .FirstOrDefaultAsync();
            if (cleaner == null) return NotFound();
            if (!IsStaffWithView() && cleaner.UserId != userId) return NotFound();

            var full = document
                ? PrivateFileStore.Resolve(UploadRoot, cleaner.DocumentUrl, PrivateFileUrls.CleanerDocumentsFolder)
                : PrivateFileStore.Resolve(UploadRoot, cleaner.PhotoUrl, PrivateFileUrls.CleanerPhotosFolder);
            if (full == null) return NotFound();

            return document
                ? PrivateFileResponse.Serve(this, full, $"cleaner-{cleanerId}-document", PrivateFileResponse.NoStore)
                : PrivateFileResponse.Serve(this, full, $"cleaner-{cleanerId}-photo", PrivateFileResponse.ShortPrivateCache);
        }

        private string? UploadRoot => _configuration["FileUpload:Path"];

        private int? CurrentUserId()
        {
            if (User.Identity?.IsAuthenticated != true) return null;
            var id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("UserId")?.Value;
            return int.TryParse(id, out var parsed) ? parsed : null;
        }

        private bool HasAnyRole(string[] roles)
            => User.Identity?.IsAuthenticated == true && roles.Any(User.IsInRole);

        /// <summary>The panels' own gate: an Admin/SuperAdmin/Moderator role AND RequirePermission(View).</summary>
        private bool IsStaffWithView()
        {
            if (!HasAnyRole(PanelRoles)) return false;
            var roleClaim = User.FindFirst("Role")?.Value;
            return Enum.TryParse<UserRole>(roleClaim, out var role) && _permissions.HasPermission(role, Permission.View);
        }
    }
}

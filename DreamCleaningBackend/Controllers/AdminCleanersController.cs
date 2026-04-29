using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/admin/cleaners")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminCleanersController : ControllerBase
    {
        private readonly ICleanerManagementService _service;

        public AdminCleanersController(ICleanerManagementService service)
        {
            _service = service;
        }

        [HttpGet]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<CleanerListItemDto>>> GetAll(
            [FromQuery] bool includeInactive = false,
            [FromQuery] string? search = null)
        {
            var result = await _service.GetAllAsync(includeInactive, search);
            return Ok(result);
        }

        [HttpGet("{id}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<CleanerDetailDto>> GetById(int id)
        {
            var result = await _service.GetByIdAsync(id);
            if (result == null)
                return NotFound();
            return Ok(result);
        }

        [HttpPost]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<CleanerDetailDto>> Create([FromBody] CreateCleanerDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var adminId = GetUserId();
            var result = await _service.CreateAsync(dto, adminId);
            return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
        }

        [HttpPut("{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<CleanerDetailDto>> Update(int id, [FromBody] UpdateCleanerDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var result = await _service.UpdateAsync(id, dto);
            if (result == null)
                return NotFound();
            return Ok(result);
        }

        [HttpDelete("{id}")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> Delete(int id)
        {
            var success = await _service.DeleteAsync(id);
            if (!success)
                return NotFound();
            return NoContent();
        }

        [HttpPost("{id}/notes")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<CleanerNoteDto>> AddNote(int id, [FromBody] CreateCleanerNoteDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var adminId = GetUserId();
            var adminDisplayName = GetUserDisplayName();
            var result = await _service.AddNoteAsync(id, dto, adminId, adminDisplayName);
            return Ok(result);
        }

        [HttpPut("notes/{noteId}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<CleanerNoteDto>> UpdateNote(int noteId, [FromBody] UpdateCleanerNoteDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var result = await _service.UpdateNoteAsync(noteId, dto);
            if (result == null)
                return NotFound();
            return Ok(result);
        }

        [HttpDelete("notes/{noteId}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> DeleteNote(int noteId)
        {
            var success = await _service.DeleteNoteAsync(noteId);
            if (!success)
                return NotFound();
            return NoContent();
        }

        [HttpPost("{id}/order-performance")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<CleanerNoteDto>> UpsertOrderPerformance(int id, [FromBody] UpsertOrderPerformanceDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var adminId = GetUserId();
            var adminDisplayName = GetUserDisplayName();
            var result = await _service.UpsertOrderPerformanceAsync(id, dto, adminId, adminDisplayName);
            return Ok(result);
        }

        [HttpPost("{id}/photo")]
        [RequirePermission(Permission.Update)]
        [RequestSizeLimit(15 * 1024 * 1024)]
        public async Task<ActionResult<CleanerImageUploadResultDto>> UploadPhoto(int id, IFormFile file)
        {
            try
            {
                var result = await _service.UploadPhotoAsync(id, file);
                if (result == null)
                    return NotFound();
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{id}/document")]
        [RequirePermission(Permission.Update)]
        [RequestSizeLimit(15 * 1024 * 1024)]
        public async Task<ActionResult<CleanerImageUploadResultDto>> UploadDocument(int id, IFormFile file)
        {
            try
            {
                var result = await _service.UploadDocumentAsync(id, file);
                if (result == null)
                    return NotFound();
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        private int GetUserId()
        {
            var id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(id, out var parsed) ? parsed : 0;
        }

        private string GetUserDisplayName()
        {
            var first = User.FindFirst(ClaimTypes.GivenName)?.Value ?? User.FindFirst("FirstName")?.Value;
            var last = User.FindFirst(ClaimTypes.Surname)?.Value ?? User.FindFirst("LastName")?.Value;
            var combined = $"{first} {last}".Trim();
            if (!string.IsNullOrWhiteSpace(combined))
                return combined;

            var name = User.FindFirst(ClaimTypes.Name)?.Value;
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            return User.FindFirst(ClaimTypes.Email)?.Value ?? "Admin";
        }
    }
}

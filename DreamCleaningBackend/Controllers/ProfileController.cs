using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Data;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Services;

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
        private readonly ApplicationDbContext _context;

        public ProfileController(IProfileService profileService, IAuditService auditService, ISpecialOfferService specialOfferService, ApplicationDbContext context)
        {
            _profileService = profileService;
            _auditService = auditService;
            _specialOfferService = specialOfferService;
            _context = context; 
        }

        [HttpGet]
        public async Task<ActionResult<ProfileDto>> GetProfile()
        {
            try
            {
                var userId = GetUserId();
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
                        Console.WriteLine($"Audit logging failed: {ex.Message}");
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
                    Console.WriteLine($"Audit logging failed: {ex.Message}");
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
                        Console.WriteLine($"Audit logging failed: {ex.Message}");
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
                    Console.WriteLine($"Audit logging failed: {ex.Message}");
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
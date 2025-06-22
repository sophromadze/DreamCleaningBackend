using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Attributes;
using System.Security.Claims;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/admin/special-offers")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public class SpecialOffersAdminController : ControllerBase
    {
        private readonly ISpecialOfferService _specialOfferService;
        private readonly ApplicationDbContext _context;

        public SpecialOffersAdminController(ISpecialOfferService specialOfferService, ApplicationDbContext context)
        {
            _specialOfferService = specialOfferService;
            _context = context;
        }

        [HttpGet]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<SpecialOfferAdminDto>>> GetAllSpecialOffers()
        {
            var offers = await _specialOfferService.GetAllSpecialOffers();
            return Ok(offers);
        }

        [HttpGet("{id}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<SpecialOfferAdminDto>> GetSpecialOffer(int id)
        {
            var offer = await _specialOfferService.GetSpecialOfferById(id);
            if (offer == null)
                return NotFound();

            return Ok(offer);
        }

        [HttpPost]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<SpecialOfferAdminDto>> CreateSpecialOffer(CreateSpecialOfferDto dto)
        {
            try
            {
                var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                var offer = await _specialOfferService.CreateSpecialOffer(dto, userId);
                return CreatedAtAction(nameof(GetSpecialOffer), new { id = offer.Id }, offer);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut("{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult<SpecialOfferAdminDto>> UpdateSpecialOffer(int id, UpdateSpecialOfferDto dto)
        {
            try
            {
                var offer = await _specialOfferService.UpdateSpecialOffer(id, dto);
                return Ok(offer);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeleteSpecialOffer(int id)
        {
            try
            {
                var result = await _specialOfferService.DeleteSpecialOffer(id);
                if (!result)
                    return NotFound();

                return NoContent();
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{id}/grant-to-all")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> GrantOfferToAllUsers(int id)
        {
            try
            {
                var count = await _specialOfferService.GrantOfferToAllEligibleUsers(id);
                return Ok(new { message = $"Offer granted to {count} users" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{offerId}/grant-to-user/{userId}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> GrantOfferToUser(int offerId, int userId)
        {
            try
            {
                var result = await _specialOfferService.GrantOfferToUser(offerId, userId);
                if (!result)
                    return BadRequest(new { message = "Could not grant offer to user" });

                return Ok(new { message = "Offer granted successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{id}/enable")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> EnableSpecialOffer(int id)
        {
            try
            {
                var result = await _specialOfferService.EnableSpecialOffer(id);
                if (!result)
                    return NotFound();

                return Ok(new { message = "Special offer enabled successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{id}/disable")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> DisableSpecialOffer(int id)
        {
            try
            {
                var result = await _specialOfferService.DisableSpecialOffer(id);
                if (!result)
                    return NotFound();

                return Ok(new { message = "Special offer disabled successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
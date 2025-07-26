using Microsoft.AspNetCore.Mvc;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Controllers
{
    [Route("api/special-offers")]
    [ApiController]
    public class SpecialOffersController : ControllerBase
    {
        private readonly ISpecialOfferService _specialOfferService;
        private readonly ApplicationDbContext _context;

        public SpecialOffersController(ISpecialOfferService specialOfferService, ApplicationDbContext context)
        {
            _specialOfferService = specialOfferService;
            _context = context;
        }

        [HttpGet("public")]
        public async Task<ActionResult<List<PublicSpecialOfferDto>>> GetPublicSpecialOffers()
        {
            try
            {
                var now = DateTime.UtcNow;

                var offers = await _context.SpecialOffers
                    .Where(o => o.IsActive &&
                               (o.ValidFrom == null || o.ValidFrom <= now) &&
                               (o.ValidTo == null || o.ValidTo > now))
                    .OrderBy(o => o.DisplayOrder)
                    .ThenBy(o => o.Name)
                    .Select(o => new PublicSpecialOfferDto
                    {
                        Id = o.Id,
                        Name = o.Name,
                        Description = o.Description,
                        IsPercentage = o.IsPercentage,
                        DiscountValue = o.DiscountValue,
                        Type = o.Type.ToString(),
                        Icon = o.Icon ?? string.Empty,
                        BadgeColor = o.BadgeColor ?? "#28a745",
                        MinimumOrderAmount = o.MinimumOrderAmount,
                        RequiresFirstTimeCustomer = o.RequiresFirstTimeCustomer
                    })
                    .ToListAsync();

                return Ok(offers);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }

    public class PublicSpecialOfferDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsPercentage { get; set; }
        public decimal DiscountValue { get; set; }
        public string Type { get; set; }
        public string Icon { get; set; }
        public string BadgeColor { get; set; }
        public decimal? MinimumOrderAmount { get; set; }
        public bool RequiresFirstTimeCustomer { get; set; }
    }
} 
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class ValidatePromoCodeDto
    {
        [Required]
        public string Code { get; set; }

        // Optional — only sent from booking flows that know the cart total. When set, the
        // server enforces PromoCode.MinimumOrderAmount; when omitted (e.g. gift-card balance
        // checks from order-edit), the minimum-order check is skipped.
        public decimal? SubTotal { get; set; }
    }
}

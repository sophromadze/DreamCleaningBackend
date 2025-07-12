using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class CreateBookingDto
    {
        [Required]
        public int ServiceTypeId { get; set; }

        [Required]
        public List<BookingServiceDto> Services { get; set; } = new List<BookingServiceDto>();

        public List<BookingExtraServiceDto> ExtraServices { get; set; } = new List<BookingExtraServiceDto>();

        [Required]
        public int SubscriptionId { get; set; }

        [Required]
        public DateTime ServiceDate { get; set; }

        [Required]
        public string ServiceTime { get; set; }

        [Required]
        public string EntryMethod { get; set; }

        public string? SpecialInstructions { get; set; }

        [Required]
        public string ContactFirstName { get; set; }

        [Required]
        public string ContactLastName { get; set; }

        [Required]
        [EmailAddress]
        public string ContactEmail { get; set; }

        [Required]
        [Phone]
        public string ContactPhone { get; set; }

        [Required]
        public string ServiceAddress { get; set; }

        public string? AptSuite { get; set; }

        [Required]
        public string City { get; set; }

        [Required]
        public string State { get; set; }

        [Required]
        public string ZipCode { get; set; }

        public int? ApartmentId { get; set; }
        public string? ApartmentName { get; set; }
        public string? PromoCode { get; set; }
        public string? GiftCardCode { get; set; }
        public decimal GiftCardAmountToUse { get; set; }
        public int? UserSpecialOfferId { get; set; }

        [Range(0, double.MaxValue)]
        public decimal Tips { get; set; }

        [Range(0, double.MaxValue)]
        public decimal CompanyDevelopmentTips { get; set; }
        public int MaidsCount { get; set; }
        public decimal TotalDuration { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal SubscriptionDiscountAmount { get; set; } = 0;
        public decimal SubTotal { get; set; }
        public bool IsCustomPricing { get; set; } = false;
        public decimal? CustomAmount { get; set; }
        public int? CustomCleaners { get; set; }
        public decimal? CustomDuration { get; set; }
        public List<PhotoUploadDto> UploadedPhotos { get; set; } = new List<PhotoUploadDto>();
    }
}

using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    // Snapshot of a user's loyalty discount as seen by admin tooling and the booking page.
    // `Status` is a derived label, not a stored column — computed from the User row's fields.
    public class LoyaltyDiscountDto
    {
        public decimal Percentage { get; set; }
        public bool IsManualOverride { get; set; }
        public DateTime? ActivatedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public string Status { get; set; } = "None"; // "None" | "Auto" | "Manual" | "Used"
    }

    public class SetLoyaltyDiscountDto
    {
        [Range(0, 100, ErrorMessage = "Percentage must be between 0 and 100")]
        public decimal Percentage { get; set; }
    }

    public class LoyaltyDiscountSettingsDto
    {
        public bool LoyaltyDiscountEnabled { get; set; }

        [Range(0, 100)]
        public decimal LoyaltyDay60Percentage { get; set; }

        [Range(0, 100)]
        public decimal LoyaltyDay90Percentage { get; set; }

        [Range(1, 3650)]
        public int DaysUntilFirstReminder { get; set; }

        [Range(1, 3650)]
        public int DaysUntilDiscountActivation { get; set; }

        [Range(1, 3650)]
        public int DaysUntilDiscountUpgrade { get; set; }

        [Range(0, 3650)]
        public int MinDaysFromLastUseBeforeReActivation { get; set; }
    }
}

using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class Referral
    {
        public int Id { get; set; }

        public int ReferrerUserId { get; set; }
        public virtual User Referrer { get; set; } = null!;

        public int ReferredUserId { get; set; }
        public virtual User Referred { get; set; } = null!;

        [StringLength(20)]
        public string Status { get; set; } = "Registered";
        // Registered, OrderCompleted

        public bool RegistrationBonusGiven { get; set; } = false;
        public bool OrderBonusGiven { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
    }
}

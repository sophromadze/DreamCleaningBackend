using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class BubblePointsHistory
    {
        public int Id { get; set; }

        public int UserId { get; set; }
        public virtual User User { get; set; } = null!;

        public int Points { get; set; }

        [Required]
        [StringLength(50)]
        public string Type { get; set; } = string.Empty;
        // OrderEarned, WelcomeBonus, ReferralRegistration, ReferralOrderCompleted,
        // ReviewBonus, StreakBonus, NextOrderBonus, Redeemed, AdminAdjustment, Expired

        [StringLength(500)]
        public string? Description { get; set; }

        public int? OrderId { get; set; }
        public virtual Order? Order { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

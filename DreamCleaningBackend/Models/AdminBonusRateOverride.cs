using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// A SuperAdmin's per-person bonus rates, overriding the matching company default on
    /// <see cref="AdminBonusSetting"/>. One row per staff member at most.
    ///
    /// TWO pairs, because a manager earns on two different slots and they are not the same job:
    /// what they get for a booking they took themselves, and what they get as a share of one of
    /// their administrators' bookings. An Administrator only ever fills the first slot, so the
    /// team pair simply stays null on their row and the panel does not offer it.
    ///
    /// Each rate is independently nullable, and NULL means "follow the company default" rather than
    /// "zero" — so a person can be given a custom new-customer rate while their repeat-customer rate
    /// keeps tracking the company default. Clearing an override sends nulls rather than re-sending
    /// the default's current value, which would freeze that person at today's figure.
    /// </summary>
    public class AdminBonusRateOverride
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User User { get; set; } = null!;

        /// <summary>Orders this person books themselves, first-time customer. Null = company default.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal? OwnBookingNewCustomerRate { get; set; }

        /// <summary>Orders this person books themselves, returning customer. Null = company default.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal? OwnBookingExistingCustomerRate { get; set; }

        /// <summary>
        /// This person's share of an order one of their administrators booked, first-time customer.
        /// Only meaningful on a Manager row. Null = company default.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal? TeamBookingNewCustomerRate { get; set; }

        /// <summary>
        /// This person's share of an order one of their administrators booked, returning customer.
        /// Only meaningful on a Manager row. Null = company default.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal? TeamBookingExistingCustomerRate { get; set; }

        public int? UpdatedByUserId { get; set; }

        [ForeignKey("UpdatedByUserId")]
        public virtual User? UpdatedByUser { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}

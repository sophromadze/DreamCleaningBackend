using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    // Single-row configuration for the per-order staff bonus. SIX rates, because the amount owed
    // depends on two independent things: WHICH SLOT the person is filling on that order, and
    // whether the customer was booking for the first time.
    //
    // There are three slots, and the owner sets all three by hand (2026-08-31):
    //
    //   1. An ADMINISTRATOR books the order             -> 10 / 10
    //   2. A MANAGER books the order themselves         -> 15 / 25
    //   3. A manager's share of a booking one of their
    //      administrators took                          ->  5 / 15
    //
    // Slot 2 is deliberately its OWN pair rather than "slot 1 + slot 3", even though the defaults
    // happen to add up that way. They are independent figures: raising what an administrator earns
    // is not an instruction to raise what a manager earns for doing the booking themselves, and a
    // derived value would move on its own the next time either of the other two changed.
    //
    // Rates are read LIVE when a month is computed, so changing one restates every month on screen.
    // OrderAdminAssignmentHistory.BonusRateAtChange keeps a snapshot of what was in force when an
    // order was assigned, for disputes.
    public class AdminBonusSetting
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Slot 1 — an administrator books the order, first-time customer.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal AdministratorNewCustomerRate { get; set; } = 10m;

        /// <summary>Slot 1 — an administrator books the order, returning customer.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal AdministratorExistingCustomerRate { get; set; } = 10m;

        /// <summary>Slot 2 — a manager books the order themselves, first-time customer.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal ManagerOwnBookingNewCustomerRate { get; set; } = 15m;

        /// <summary>Slot 2 — a manager books the order themselves, returning customer.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal ManagerOwnBookingExistingCustomerRate { get; set; } = 25m;

        /// <summary>Slot 3 — a manager's share of their administrator's booking, first-time customer.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal ManagerTeamNewCustomerRate { get; set; } = 5m;

        /// <summary>Slot 3 — a manager's share of their administrator's booking, returning customer.</summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal ManagerTeamExistingCustomerRate { get; set; } = 15m;

        [Required]
        [StringLength(10)]
        public string Currency { get; set; } = "GEL";

        public int? UpdatedByUserId { get; set; }

        [ForeignKey("UpdatedByUserId")]
        public virtual User? UpdatedByUser { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}

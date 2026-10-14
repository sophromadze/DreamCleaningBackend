using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>What a percentage bonus is a percentage OF.</summary>
    public enum AdminPercentBonusBasis
    {
        /// <summary>The order's price without tips (after discounts and refunds, sales tax included).</summary>
        FullOrder = 0,

        /// <summary>
        /// The order's net: price without tips, minus sales tax, minus the cleaners' wages, minus
        /// the order's fixed per-order staff bonuses. See <see cref="Helpers.AdminOrderEarnings"/>.
        /// </summary>
        NetIncome = 1
    }

    /// <summary>
    /// A percentage bonus an Admin earns on the orders they book (the booker slot), ON TOP of the
    /// fixed per-order GEL bonus. Set from the shifts panel by a SuperAdmin (2026-10).
    ///
    /// Rows are a HISTORY, never edited in place: each one applies from its <see cref="StartDate"/>
    /// until the next row for the same person starts. An order is priced by the row in force on its
    /// SERVICE date, so setting a new percentage — or stopping it, which is a row with
    /// both percents 0 — never restates orders before the date the owner picked. That is
    /// the owner's rule and the opposite of the fixed GEL rates, which are read live.
    /// </summary>
    public class AdminPercentBonusRule
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        [ForeignKey(nameof(UserId))]
        public virtual User User { get; set; } = null!;

        public AdminPercentBonusBasis Basis { get; set; } = AdminPercentBonusBasis.FullOrder;

        // Two percents, like the per-order rates: one for an order whose customer was booking for
        // the first time (Order.IsNewCustomerOrder), one for a returning customer. 0–100 each; both
        // zero means "no percentage bonus from this date" (a stop).
        [Column(TypeName = "decimal(5,2)")]
        public decimal NewCustomerPercent { get; set; }

        [Column(TypeName = "decimal(5,2)")]
        public decimal ExistingCustomerPercent { get; set; }

        /// <summary>Whether this rule pays anything at all.</summary>
        [NotMapped]
        public bool PaysAnything => NewCustomerPercent > 0m || ExistingCustomerPercent > 0m;

        /// <summary>First service date (inclusive, date only) this rule applies to.</summary>
        public DateTime StartDate { get; set; }

        public int? CreatedByUserId { get; set; }

        [ForeignKey(nameof(CreatedByUserId))]
        public virtual User? CreatedByUser { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

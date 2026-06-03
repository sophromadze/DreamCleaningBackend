using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Models
{
    // One locked row per calendar month, used by the SuperAdmin statistics page to convert
    // GEL-denominated admin bonuses into USD and to freeze the bonus rate for that month.
    //
    // Why this exists: admin bonuses are paid in Georgian Lari (GEL). Statistics are in USD.
    // The owner wants each month converted at the FX rate "in that month" and then FROZEN —
    // changing the rate in July must not retroactively alter June. So the first time a month
    // is calculated we snapshot both the FX rate (UsdPerGel) and the GEL bonus rate, and from
    // then on the values are reused. Past (finalized) months never auto-change; the ongoing
    // month may refresh its auto FX until it rolls over. SuperAdmin can manually override FX.
    [Index(nameof(Year), nameof(Month), IsUnique = true)]
    public class MonthlyFinancialSnapshot
    {
        [Key]
        public int Id { get; set; }

        public int Year { get; set; }

        // 1-12.
        public int Month { get; set; }

        // USD value of 1 GEL. usdAmount = gelAmount * UsdPerGel. (e.g. ~0.37 for 1 GEL.)
        [Column(TypeName = "decimal(18,6)")]
        public decimal UsdPerGel { get; set; }

        // Snapshot of the admin bonus rate (GEL paid per eligible order) effective for this
        // month. Frozen so a later rate change doesn't rewrite history.
        [Column(TypeName = "decimal(18,2)")]
        public decimal AdminBonusRatePerOrderGel { get; set; }

        // "auto" — UsdPerGel came from the FX API and may refresh while the month is ongoing.
        // "manual" — a SuperAdmin set it explicitly; never auto-refreshed.
        // "fallback" — the API was unreachable and a default was used; treat as needs-attention.
        [Required]
        [StringLength(20)]
        public string FxSource { get; set; } = "auto";

        // True once the month is in the past — values are locked forever and never auto-refresh.
        public bool IsFinalized { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public int? UpdatedByUserId { get; set; }

        [ForeignKey("UpdatedByUserId")]
        public virtual User? UpdatedByUser { get; set; }
    }
}

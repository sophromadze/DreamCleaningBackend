using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    // The record that one of a staff member's two monthly salary instalments has been paid.
    //
    // Rows are created LAZILY — only when somebody is actually marked paid. What is OWED is derived
    // from the salary expenses (see SalaryPaymentSchedule), so this table holds decisions, not
    // arithmetic; the same arrangement OrderUnassignedPayout uses for cleaner staffing slots. An
    // absent row means "not paid yet", never "no such instalment".
    //
    // Unpaying DELETES the row rather than flagging it, so an undone mistake leaves no trace of a
    // payment that never happened. The audit log is where the reversal is recorded.
    public class AdminSalaryPayment
    {
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// Who was paid, as <see cref="Helpers.SalaryExpenseRules.GroupingKey"/> — the same key the
        /// expenses page groups a person's salary rows under. A STRING rather than a user id
        /// because a salary can be owed to somebody with no account at all, and because it has to
        /// keep pointing at the right person after that account is deleted.
        /// </summary>
        [Required]
        [StringLength(220)]
        public string PayeeKey { get; set; } = string.Empty;

        /// <summary>
        /// The staff member, when there is one. Like <see cref="Expense.StaffUserId"/> this carries
        /// NO foreign key: a payment already made must survive the deletion of the account it was
        /// made to. Null for a salary recorded against a typed name.
        /// </summary>
        public int? StaffUserId { get; set; }

        /// <summary>Their name when the payment was recorded — what the row is labelled with if the account goes.</summary>
        [Required]
        [StringLength(200)]
        public string PayeeName { get; set; } = string.Empty;

        public int Year { get; set; }

        /// <summary>1-12.</summary>
        public int Month { get; set; }

        /// <summary>1 = first payment of the month, 2 = second. See SalaryPaymentSchedule.</summary>
        public int Half { get; set; }

        /// <summary>
        /// What was handed over, in <see cref="Currency"/>, frozen at pay time. Never re-derived:
        /// editing the salary afterwards changes what is owed NEXT month, not what was already
        /// paid — the same rule that freezes a paid cleaner payout line.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal PaidAmount { get; set; }

        /// <summary>
        /// How much of <see cref="PaidAmount"/> was staff bonuses rather than salary, frozen the
        /// same way. Kept so a settled instalment can still explain itself — without it, a second
        /// payment that came to more than half the salary looks like an error rather than a month
        /// with bonuses in it.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal PaidBonusAmount { get; set; }

        [Required]
        [StringLength(3)]
        public string Currency { get; set; } = Helpers.ExpenseCurrency.Usd;

        /// <summary>
        /// The same payment in USD, at the rate in force when it was recorded. Frozen alongside the
        /// amount so a later FX correction cannot restate money that has already left.
        /// </summary>
        [Column(TypeName = "decimal(18,2)")]
        public decimal PaidAmountUsd { get; set; }

        /// <summary>The rate used. Null when the salary was already in USD — nothing was converted.</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal? UsdPerGel { get; set; }

        public DateTime PaidAt { get; set; } = DateTime.UtcNow;

        public int PaidByUserId { get; set; }

        [ForeignKey(nameof(PaidByUserId))]
        public virtual User? PaidByUser { get; set; }

        [StringLength(500)]
        public string? PaymentNote { get; set; }
    }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    // Where an employee's salary is actually SENT — an IBAN, a bank card or an ID number, whatever
    // the person is paid against. One row per payee, created the first time somebody fills it in.
    //
    // Its own table rather than a column on User, for the same reason the salary itself is keyed by
    // PayeeKey: a salary can be owed to somebody with no account at all, and the destination has to
    // stay readable after an account is deleted — that is precisely when you are settling up.
    //
    // Deliberately NOT the same thing as Cleaner.PaymentMethod/PaymentDetails. Those belong to a
    // Cleaner row and describe how a cleaner's WAGES are sent; this is the office-staff side, and
    // the two are paid from different tabs by different rules.
    public class AdminSalaryPayee
    {
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// Who this belongs to, as <see cref="Helpers.SalaryExpenseRules.GroupingKey"/> — the same
        /// key the salary and its payments are addressed by. Unique.
        /// </summary>
        [Required]
        [StringLength(220)]
        public string PayeeKey { get; set; } = string.Empty;

        /// <summary>
        /// The staff member, when there is one. Carries NO foreign key, same rule as
        /// <see cref="Expense.StaffUserId"/>: the destination must outlive the account.
        /// </summary>
        public int? StaffUserId { get; set; }

        /// <summary>
        /// The destination itself — free text because it is an IBAN in one country, a card number
        /// in another and an ID number for somebody else, and validating a format we cannot know
        /// would block a real payment. Copied verbatim; never parsed.
        /// </summary>
        [StringLength(200)]
        public string? PaymentDetails { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public int UpdatedByUserId { get; set; }

        [ForeignKey(nameof(UpdatedByUserId))]
        public virtual User? UpdatedByUser { get; set; }
    }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// A manual, admin-applied label on a customer (User), e.g. "Pet owner", "Allergy",
    /// "Gate code 1234". Distinct from CRM <i>segments</i>, which are computed on the fly from
    /// order history / spend / subscription. Tags are free-form and overlap freely.
    /// </summary>
    public class CustomerTag
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User User { get; set; } = null!;

        [Required]
        [StringLength(50)]
        public string Label { get; set; } = string.Empty;

        /// <summary>Optional hex color for the pill (e.g. "#22c55e"). Null = default neutral.</summary>
        [StringLength(10)]
        public string? Color { get; set; }

        public int? CreatedByAdminId { get; set; }

        [StringLength(200)]
        public string? CreatedByAdminName { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

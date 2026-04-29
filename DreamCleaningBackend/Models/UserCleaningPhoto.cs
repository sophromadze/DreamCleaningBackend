using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Admin-only cleaning photos attached to a user, grouped by the order they belong to.
    /// Only photos for the user's two most recent orders are kept; older ones are pruned
    /// on each upload to avoid unbounded storage growth.
    /// </summary>
    public class UserCleaningPhoto
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User User { get; set; } = null!;

        public int? OrderId { get; set; }

        [ForeignKey("OrderId")]
        public virtual Order? Order { get; set; }

        [Required]
        [StringLength(500)]
        public string PhotoUrl { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public int? UploadedByAdminId { get; set; }

        [ForeignKey("UploadedByAdminId")]
        public virtual User? UploadedByAdmin { get; set; }

        [StringLength(100)]
        public string? UploadedByAdminName { get; set; }

        [StringLength(255)]
        public string? Caption { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

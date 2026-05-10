using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// A before/after photo pair shown in the homepage "See the difference"
    /// section. Uploaded and managed by admins via the Admin → Before & After tab.
    /// </summary>
    public class BeforeAfterPhoto
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(120)]
        public string Title { get; set; } = string.Empty;

        [StringLength(255)]
        public string? Subtitle { get; set; }

        [Required]
        [StringLength(500)]
        public string BeforePhotoUrl { get; set; } = string.Empty;

        [Required]
        [StringLength(500)]
        public string AfterPhotoUrl { get; set; } = string.Empty;

        /// <summary>
        /// Optional internal route the "View more results" link points to,
        /// e.g. "/services/residential-cleaning/kitchen". When null the
        /// link is hidden on the public site.
        /// </summary>
        [StringLength(500)]
        public string? LinkUrl { get; set; }

        public int DisplayOrder { get; set; } = 0;

        public bool IsActive { get; set; } = true;

        public int? UploadedByAdminId { get; set; }

        [ForeignKey(nameof(UploadedByAdminId))]
        public virtual User? UploadedByAdmin { get; set; }

        [StringLength(100)]
        public string? UploadedByAdminName { get; set; }

        public long BeforeSizeBytes { get; set; }
        public long AfterSizeBytes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }
}

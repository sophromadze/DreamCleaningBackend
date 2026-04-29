using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DreamCleaningBackend.Helpers;

namespace DreamCleaningBackend.Models
{
    public class AdminTask
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(500)]
        public string Title { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? Description { get; set; }

        [Required]
        [StringLength(20)]
        public string Priority { get; set; } = "Normal";

        [Required]
        [StringLength(20)]
        public string Status { get; set; } = "Todo";

        public DateTime? DueDate { get; set; }

        [StringLength(200)]
        public string? ClientName { get; set; }

        [StringLength(255)]
        public string? ClientEmail { get; set; }

        private string? _clientPhone;
        [StringLength(50)]
        public string? ClientPhone
        {
            get => _clientPhone;
            set => _clientPhone = PhoneHelper.NormalizeToDigits(value);
        }

        public int? ClientId { get; set; }

        public int? OrderId { get; set; }

        public int CreatedByAdminId { get; set; }

        [ForeignKey("CreatedByAdminId")]
        public virtual User CreatedByAdmin { get; set; } = null!;

        [StringLength(2000)]
        public string? CompletionNote { get; set; }

        public DateTime? CompletedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}

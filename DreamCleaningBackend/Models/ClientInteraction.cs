using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DreamCleaningBackend.Helpers;

namespace DreamCleaningBackend.Models
{
    public class ClientInteraction
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string ClientName { get; set; } = string.Empty;

        private string? _clientPhone;
        [StringLength(50)]
        public string? ClientPhone
        {
            get => _clientPhone;
            set => _clientPhone = PhoneHelper.NormalizeToDigits(value);
        }

        [StringLength(255)]
        public string? ClientEmail { get; set; }

        public int? ClientId { get; set; }

        public DateTime InteractionDate { get; set; } = DateTime.UtcNow;

        public int AdminId { get; set; }

        [ForeignKey("AdminId")]
        public virtual User Admin { get; set; } = null!;

        [Required]
        [StringLength(100)]
        public string Type { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? Notes { get; set; }

        [Required]
        [StringLength(20)]
        public string Status { get; set; } = "Pending";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

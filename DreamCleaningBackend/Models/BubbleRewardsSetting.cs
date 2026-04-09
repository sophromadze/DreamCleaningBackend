using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class BubbleRewardsSetting
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string SettingKey { get; set; } = string.Empty;

        [Required]
        [StringLength(500)]
        public string SettingValue { get; set; } = string.Empty;

        [StringLength(500)]
        public string? Description { get; set; }

        [StringLength(50)]
        public string Category { get; set; } = "General";

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}

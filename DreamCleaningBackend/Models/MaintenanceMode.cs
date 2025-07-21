using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class MaintenanceMode
    {
        [Key]
        public int Id { get; set; }
        
        public bool IsEnabled { get; set; } = false;
        
        public string? Message { get; set; }
        
        public DateTime? StartedAt { get; set; }
        
        public DateTime? EndedAt { get; set; }
        
        public string? StartedBy { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public DateTime? UpdatedAt { get; set; }
    }
} 
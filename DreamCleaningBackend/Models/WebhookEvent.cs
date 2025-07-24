using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class WebhookEvent
    {
        [Key]
        public string EventId { get; set; } = string.Empty;
        
        public string EventType { get; set; } = string.Empty;
        
        public DateTime ProcessedAt { get; set; }
        
        public bool IsProcessed { get; set; }
        
        public string? ErrorMessage { get; set; }
    }
} 
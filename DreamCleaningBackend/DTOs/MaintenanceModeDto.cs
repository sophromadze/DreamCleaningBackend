namespace DreamCleaningBackend.DTOs
{
    public class MaintenanceModeDto
    {
        public bool IsEnabled { get; set; }
        public string? Message { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public string? StartedBy { get; set; }
    }

    public class ToggleMaintenanceModeDto
    {
        public bool IsEnabled { get; set; }
        public string? Message { get; set; }
    }
} 
namespace DreamCleaningBackend.DTOs
{
    public class CleanerCalendarDto
    {
        public int OrderId { get; set; }
        public string ClientName { get; set; }
        public DateTime ServiceDate { get; set; }
        public string ServiceTime { get; set; }
        public string ServiceAddress { get; set; }
        public string ServiceTypeName { get; set; }
        public decimal TotalDuration { get; set; }
        public string? TipsForCleaner { get; set; }
        public bool IsAssignedToCleaner { get; set; }
        public string Status { get; set; } // Add status to distinguish completed orders
    }

    public class AvailableCleanerDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public bool IsAvailable { get; set; }
    }

    public class AssignCleanersDto
    {
        public int OrderId { get; set; }
        public List<int> CleanerIds { get; set; } = new();
        public string? TipsForCleaner { get; set; }
    }

    public class CleanerOrderDetailDto
    {
        public int OrderId { get; set; }
        public string ClientName { get; set; }
        public string ClientEmail { get; set; }
        public string ClientPhone { get; set; }
        public DateTime ServiceDate { get; set; }
        public string ServiceTime { get; set; }
        public string ServiceAddress { get; set; }
        public string? AptSuite { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string ZipCode { get; set; }
        public string ServiceTypeName { get; set; }
        public List<string> Services { get; set; } = new();
        public List<string> ExtraServices { get; set; } = new();
        public decimal TotalDuration { get; set; }
        public int MaidsCount { get; set; }
        public string? EntryMethod { get; set; }
        public string? SpecialInstructions { get; set; }
        public string Status { get; set; }
        public decimal TipsAmount { get; set; } // ADD: The actual tips amount
        public string? TipsForCleaner { get; set; } // Additional admin instructions
        public List<string> AssignedCleaners { get; set; } = new();
    }
}
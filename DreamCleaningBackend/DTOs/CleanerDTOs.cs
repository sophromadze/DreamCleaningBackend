using DreamCleaningBackend.Models;

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

        // True when nothing about this cleaner's day argues against assigning them. Since
        // 2026-08-31 a schedule conflict no longer BLOCKS the assignment, so this is a sorting
        // and labelling signal only — never a permission.
        public bool IsAvailable { get; set; }

        public string? Location { get; set; }
        public CleanerRanking Ranking { get; set; }
        public string? Experience { get; set; }

        // Soft: marked busy on this order's date via a recurring weekday or a vacation.
        // Hidden by default in the assign modal; revealed by "Show busy"; still assignable.
        public bool IsBusyDay { get; set; }
        public string? BusyDayReason { get; set; }

        // Another Active/Pending job that day within 1 hour of this one. A WARNING, not a block
        // (2026-08-31): the admin is the one who knows the two addresses are next door, or that
        // the earlier job is running short. The modal surfaces it loudly and makes the admin
        // acknowledge it; the server still refuses without that acknowledgement — see
        // AssignCleanersDto.AcknowledgeScheduleConflicts.
        public bool HasScheduleConflict { get; set; }
        public string? ConflictReason { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    public class AssignCleanersDto
    {
        public int OrderId { get; set; }
        public List<int> CleanerIds { get; set; } = new();
        public string? TipsForCleaner { get; set; }
        public decimal? CleanerHourlyRate { get; set; }

        /// <summary>
        /// The admin has SEEN the same-day 1-hour-gap conflicts on the cleaners they picked and
        /// wants them assigned regardless.
        ///
        /// Defaults to false, so a conflicting assignment arriving without one is still refused —
        /// a direct API call, or a client that never put the warning in front of anybody. The gap
        /// rule is still real scheduling advice; what changed is who gets to overrule it.
        /// </summary>
        public bool AcknowledgeScheduleConflicts { get; set; }
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
        public bool IsCustomServiceType { get; set; }
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

    public class SendCleanerAssignmentMailsResultDto
    {
        public int EmailsSent { get; set; }
        public string Message { get; set; } = "";
    }
}
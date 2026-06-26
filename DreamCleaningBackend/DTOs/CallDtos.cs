namespace DreamCleaningBackend.DTOs
{
    // ── Call tracking DTOs ──

    public class CallRecordDto
    {
        public int Id { get; set; }
        public string Provider { get; set; } = string.Empty;
        public string ExternalId { get; set; } = string.Empty;
        public string? SessionId { get; set; }
        public string Direction { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public string? FromNumber { get; set; }
        public string? FromName { get; set; }
        public string? ToNumber { get; set; }
        public DateTime StartTimeUtc { get; set; }
        public int DurationSeconds { get; set; }
        public string? RecordingUrl { get; set; }
        public int? LeadId { get; set; }
        public string? LeadName { get; set; }
        public string Category { get; set; } = "Unknown";
        /// <summary>Independent of Category: the call was placed to the dedicated ad tracking number.</summary>
        public bool IsAdCall { get; set; }
        public int? MatchedCleanerId { get; set; }
        public string? MatchedCleanerName { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class CallListResultDto
    {
        public List<CallRecordDto> Items { get; set; } = new();
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
    }

    public class CallDayCountDto
    {
        /// <summary>Date (UTC) in yyyy-MM-dd form.</summary>
        public string Date { get; set; } = string.Empty;
        public int Total { get; set; }
        public int Answered { get; set; }
        public int Missed { get; set; }
    }

    public class CallSummaryDto
    {
        public int Total { get; set; }
        public int Answered { get; set; }
        public int Missed { get; set; }
        public int Inbound { get; set; }
        public double AnswerRate { get; set; }

        // Category breakdown (reflects the same filters applied to the summary request).
        public int Customer { get; set; }
        public int Cleaner { get; set; }
        public int Spam { get; set; }
        public int Unknown { get; set; }

        // Independent ad-call dimension (overlaps the categories above, not additive with them).
        public int AdCall { get; set; }

        public List<CallDayCountDto> PerDay { get; set; } = new();
    }
}

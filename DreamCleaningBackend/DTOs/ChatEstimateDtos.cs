namespace DreamCleaningBackend.DTOs
{
    // DTOs for the public chat-agent pricing endpoints (ChatController).
    //
    // The catalog DTOs deliberately expose STRUCTURE ONLY — no Cost / BasePrice /
    // Price / PriceMultiplier fields — so a chat agent can never quote from stale
    // cached prices. The only way to get a number is POST /api/chat/estimate,
    // which resolves prices fresh from the DB through the shared calculator.

    public class ChatServiceTypeDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public List<ChatServiceDto> Services { get; set; } = new();
        public List<ChatExtraServiceDto> ExtraServices { get; set; } = new();
    }

    public class ChatServiceDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? ServiceKey { get; set; }
        public string? ServiceRelationType { get; set; } // "cleaner", "hours", null for regular
        public string? InputType { get; set; }
        public int? MinValue { get; set; }
        public int? MaxValue { get; set; }
        public int? StepValue { get; set; }
        public bool IsRangeInput { get; set; }
        public string? Unit { get; set; }
    }

    public class ChatExtraServiceDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool HasQuantity { get; set; }
        public bool HasHours { get; set; }
        public bool IsDeepCleaning { get; set; }
        public bool IsSuperDeepCleaning { get; set; }
        public bool IsSameDayService { get; set; }
        public int? ServiceTypeId { get; set; }
        public bool IsAvailableForAll { get; set; }
    }

    /// <summary>
    /// Estimate request: IDs and quantities only. Costs/prices are never accepted
    /// from the caller — they are resolved fresh from the DB server-side.
    /// Reuses the booking line DTOs so the shape matches real bookings 1:1.
    /// </summary>
    public class ChatEstimateRequestDto
    {
        public int ServiceTypeId { get; set; }
        public List<BookingServiceDto> Services { get; set; } = new();
        public List<BookingExtraServiceDto> ExtraServices { get; set; } = new();
    }

    public class ChatEstimateResponseDto
    {
        public decimal SubTotal { get; set; }
        public decimal EstimatedTax { get; set; }
        public decimal EstimatedTotal { get; set; }
        public decimal DisplayDurationMinutes { get; set; }
        // MaidsCount was removed 2026-07: cleaner count is decided by the team per
        // order and must never be quoted in chat (see CLEANER COUNT rule in the prompt).
        public decimal DeepCleaningFee { get; set; }
        public string Note { get; set; } = string.Empty;
    }
}

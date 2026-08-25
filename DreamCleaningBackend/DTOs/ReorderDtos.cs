namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// One service / extra line whose CHARGE or DURATION has moved since the source order was
    /// booked. Only lines that actually moved are emitted — a reorder whose catalogue is
    /// unchanged produces an empty list, and the preview screen renders nothing for it.
    ///
    /// The "original" side is the persisted <see cref="Models.OrderService.Cost"/> /
    /// <see cref="Models.OrderService.Duration"/> — the line total that was really charged, not a
    /// re-derivation. The "new" side comes from re-pricing the SAME quantities through the shared
    /// calculator against today's catalogue, so the difference is genuinely "the price changed"
    /// and never "we compute it differently now".
    /// </summary>
    public class ReorderLineChangeDto
    {
        /// <summary>"Service" or "Extra".</summary>
        public string Kind { get; set; } = "Service";
        /// <summary>ServiceId or ExtraServiceId.</summary>
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Quantity { get; set; }
        public decimal Hours { get; set; }
        public decimal OriginalCost { get; set; }
        public decimal NewCost { get; set; }
        public decimal OriginalDuration { get; set; }
        public decimal NewDuration { get; set; }
    }

    /// <summary>
    /// A line on the source order whose catalogue row no longer exists or is deactivated. It is
    /// DROPPED from the recreated order — the admin is told before the recreate, not after, which
    /// is the whole reason the preview step exists.
    /// </summary>
    public class ReorderUnavailableLineDto
    {
        /// <summary>"Service" or "Extra".</summary>
        public string Kind { get; set; } = "Service";
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Quantity { get; set; }
        public decimal OriginalCost { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// One discount slot that was on the source order, and what becomes of it on the recreated
    /// one. Every slot the source order used is reported even when the answer is a flat "not
    /// carried over" — "why is this cheaper/dearer than last time" is the question the screen
    /// exists to answer, and a silently missing discount is exactly what makes an admin distrust
    /// the number.
    /// </summary>
    public class ReorderDiscountChangeDto
    {
        /// <summary>PromoCode | FirstTime | SpecialOffer | GiftCard | BubblePoints | RewardBalance | Loyalty | Subscription</summary>
        public string Kind { get; set; } = "";
        public string Label { get; set; } = "";
        /// <summary>What this slot took off the SOURCE order.</summary>
        public decimal OriginalAmount { get; set; }
        /// <summary>What it would take off the recreated order — 0 for everything except
        /// Loyalty/Subscription, and 0 for those too unless the admin opts in.</summary>
        public decimal AvailableAmount { get; set; }
        /// <summary>True only for Loyalty / Subscription, and only when the customer is still
        /// entitled — i.e. the two the admin may opt back in to.</summary>
        public bool CanReapply { get; set; }
        /// <summary>Plain-English reason, written for an admin explaining the difference to a
        /// customer on the phone. Never empty.</summary>
        public string Reason { get; set; } = "";
    }

    /// <summary>Money + duration snapshot, used for both sides of the comparison.</summary>
    public class ReorderTotalsDto
    {
        public decimal SubTotal { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal SubscriptionDiscountAmount { get; set; }
        public decimal LoyaltyDiscountAmount { get; set; }
        public decimal GiftCardAmountUsed { get; set; }
        public decimal PointsRedeemedDiscount { get; set; }
        public decimal RewardBalanceUsed { get; set; }
        public decimal Tax { get; set; }
        public decimal Tips { get; set; }
        public decimal Total { get; set; }
        public decimal TotalDuration { get; set; }
        public int MaidsCount { get; set; }
    }

    /// <summary>
    /// Everything the admin "recreate this order" flow needs: the diff screen's content and the
    /// starting state for the mini booking form behind it, in ONE round trip. The prefill is a
    /// real <see cref="CreateBookingDto"/> so the modal can mutate it and post it straight back
    /// to create-for-user — there is no second mapping to keep in step.
    /// </summary>
    public class ReorderPreviewDto
    {
        public int SourceOrderId { get; set; }
        public int CustomerUserId { get; set; }
        public string CustomerName { get; set; } = "";
        public DateTime OriginalServiceDate { get; set; }
        public string ServiceTypeName { get; set; } = "";
        public bool IsCustomServiceType { get; set; }

        /// <summary>What the source order actually charged.</summary>
        public ReorderTotalsDto Original { get; set; } = new ReorderTotalsDto();

        /// <summary>What the same job costs today with NO discounts carried over — the default
        /// the modal opens on.</summary>
        public ReorderTotalsDto Recreated { get; set; } = new ReorderTotalsDto();

        public List<ReorderLineChangeDto> LineChanges { get; set; } = new List<ReorderLineChangeDto>();
        public List<ReorderUnavailableLineDto> Unavailable { get; set; } = new List<ReorderUnavailableLineDto>();
        public List<ReorderDiscountChangeDto> Discounts { get; set; } = new List<ReorderDiscountChangeDto>();

        /// <summary>False when nothing at all differs — the screen then says so in one line
        /// instead of rendering four empty sections.</summary>
        public bool HasChanges { get; set; }

        /// <summary>Starting state for the mini booking form. Carries no promo code, gift card,
        /// special offer, points or credits — those slots are cleared here, not in the UI, so a
        /// client that skipped the preview still cannot resurrect a stale discount.</summary>
        public CreateBookingDto Prefill { get; set; } = new CreateBookingDto();

        /// <summary>Where a confirmation would land if the admin ticks "notify". Null means that
        /// channel is unavailable and the modal disables the toggle rather than offering a send
        /// that would be silently skipped.</summary>
        public string? NotificationEmail { get; set; }
        public string? NotificationPhone { get; set; }
        public bool CustomerHasNoAccountEmail { get; set; }
    }
}

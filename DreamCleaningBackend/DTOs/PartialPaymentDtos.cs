namespace DreamCleaningBackend.DTOs
{
    /// <summary>One part-payment request on an order, as the admin panel and the payment page see it.</summary>
    public class OrderPartialPaymentDto
    {
        public int Id { get; set; }
        public int OrderId { get; set; }

        /// <summary>What the admin asked the customer for.</summary>
        public decimal RequestedAmount { get; set; }

        /// <summary>What actually arrived — can exceed <see cref="RequestedAmount"/> when the payer
        /// chose to settle the whole balance instead. Null until paid.</summary>
        public decimal? PaidAmount { get; set; }

        /// <summary>"Pending" | "Paid" | "Cancelled" — the string form so the frontend never has to
        /// know the enum's numbers.</summary>
        public string Status { get; set; } = "Pending";

        public DateTime? PaidAt { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>When the payment link for this request last went out. Null = created but never
        /// sent, which the panel says rather than implying the customer has been told.</summary>
        public DateTime? NotificationSentAt { get; set; }

        /// <summary>Admin-facing note. Never shown to the customer.</summary>
        public string? Note { get; set; }

        /// <summary>Who asked for it, pre-formatted ("F. LastName") for the history list.</summary>
        public string? RequestedByName { get; set; }

        /// <summary>"Normal" (paid via the card link) or Cash/Zelle/Check/Other/Invoice when an
        /// admin recorded this slice as collected outside Stripe.</summary>
        public string PaymentMethod { get; set; } = "Normal";

        public string? PaymentReference { get; set; }
        public string? PaymentNotes { get; set; }

        /// <summary>Who recorded the manual payment, pre-formatted like <see cref="RequestedByName"/>.
        /// Null for a slice paid through the card link.</summary>
        public string? ManualPaymentRecordedByName { get; set; }
    }

    /// <summary>
    /// The state of an order's own balance. Deliberately separate from the order-edit top-up
    /// (<c>PendingUpdateAmount</c>): that is money owed on top of a settled order, this is what is
    /// left of the original total. A surface showing "still owed" adds them; nothing merges them.
    /// </summary>
    public class OrderPaymentBalanceDto
    {
        public decimal Total { get; set; }
        public decimal AmountPaid { get; set; }
        public decimal AmountDue { get; set; }
        public bool IsPartiallyPaid { get; set; }

        /// <summary>Money taken beyond the total — only reachable when an admin lowered the price
        /// after a deposit. Reported so a person can refund it; never refunded automatically.</summary>
        public decimal OverpaidAmount { get; set; }

        /// <summary>Whether an admin may ask for another part-payment, and why not when they can't.</summary>
        public bool CanRequestPartialPayment { get; set; }
        public string? CannotRequestReason { get; set; }

        /// <summary>The one live request, or null. At most one exists at a time.</summary>
        public OrderPartialPaymentDto? PendingRequest { get; set; }

        public List<OrderPartialPaymentDto> History { get; set; } = new();
    }

    /// <summary>Admin asks the customer for part of the outstanding balance.</summary>
    public class CreatePartialPaymentRequestDto
    {
        public decimal Amount { get; set; }
        public string? Note { get; set; }

        /// <summary>Send the payment link by email / SMS straight away. Both false creates the
        /// request without telling anyone — useful when the admin will paste the link into a chat.</summary>
        public bool SendEmail { get; set; } = true;
        public bool SendSms { get; set; } = true;
    }

    /// <summary>
    /// Records a live part-payment request as paid OUTSIDE Stripe — the customer handed over cash,
    /// Zelle'd it, wrote a check, or it's going on a commercial invoice. Mirrors
    /// <c>RecordManualAdditionalPaymentDto</c> (the order-edit top-up equivalent). PaymentMethod
    /// must be a non-Normal value; parsed case-insensitively.
    /// </summary>
    public class RecordPartialPaymentManuallyDto
    {
        public string PaymentMethod { get; set; } = string.Empty;
        public string? PaymentReference { get; set; }
        public string? PaymentNotes { get; set; }
    }

    /// <summary>What the payment page needs to charge one slice.</summary>
    public class PartialPaymentIntentDto
    {
        public int OrderId { get; set; }
        public int PartialPaymentId { get; set; }

        /// <summary>The amount this intent will actually charge — the requested slice, or the whole
        /// remaining balance when the payer chose to settle it in one go.</summary>
        public decimal Amount { get; set; }

        /// <summary>What the admin originally asked for, so the page can say "you're paying the
        /// full $2,743.65 instead of the $1,000.00 requested".</summary>
        public decimal RequestedAmount { get; set; }

        /// <summary>Everything still owed on the order right now.</summary>
        public decimal AmountDue { get; set; }

        /// <summary>What will still be owed after this payment lands. Zero on the final one.</summary>
        public decimal RemainingAfterPayment { get; set; }

        /// <summary>True when this payment settles the order — the page says "this completes your
        /// payment" instead of naming a next instalment.</summary>
        public bool IsFinalPayment { get; set; }

        public string? PaymentIntentId { get; set; }
        public string? PaymentClientSecret { get; set; }

        /// <summary>False only when the balance is already under Stripe's minimum, in which case
        /// confirming settles the order with no charge.</summary>
        public bool RequiresPayment { get; set; } = true;
    }

    /// <summary>Result of settling one slice.</summary>
    public class PartialPaymentConfirmationDto
    {
        public bool Success { get; set; }
        public int OrderId { get; set; }
        public decimal AmountPaid { get; set; }
        public decimal AmountDue { get; set; }

        /// <summary>True when this payment completed the order — the customer sees the booking
        /// confirmation rather than "thanks, $1,743.65 to go".</summary>
        public bool OrderFullyPaid { get; set; }

        public string Status { get; set; } = string.Empty;
        public string? Message { get; set; }
    }
}

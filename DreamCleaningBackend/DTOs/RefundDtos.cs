using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    /// <summary>Admin's refund request. Amount null = refund everything still refundable.</summary>
    public class IssueRefundDto
    {
        /// <summary>Dollars. Null means a full refund of the remaining refundable balance.</summary>
        [Range(0.01, 100000, ErrorMessage = "Refund amount must be greater than zero.")]
        public decimal? Amount { get; set; }

        /// <summary>Internal note. Stored on the refund record; never shown to the customer.</summary>
        [StringLength(500)]
        public string? Reason { get; set; }

        public bool SendEmail { get; set; } = true;
    }

    /// <summary>One recorded refund, as shown in the admin panel's refund history.</summary>
    public class OrderRefundDto
    {
        public int Id { get; set; }
        public decimal Amount { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public string? FailureReason { get; set; }
        public string RefundedByName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public bool EmailSent { get; set; }
    }

    /// <summary>
    /// Everything the admin order panel needs to render the refund section: what's still
    /// refundable (live from Stripe) and the history of what's been refunded through us.
    /// Admin-only — deliberately NOT folded into OrderDto, which is shared with the
    /// customer-facing order details endpoint and would leak admin names + internal notes.
    /// </summary>
    public class OrderRefundSummaryDto
    {
        public int OrderId { get; set; }

        /// <summary>Total this order settled through card payments (base + paid order edits).</summary>
        public decimal TotalCharged { get; set; }

        /// <summary>Already refunded, counting Stripe Dashboard refunds we never recorded.</summary>
        public decimal TotalRefunded { get; set; }

        /// <summary>The cap for the amount input. Zero disables the refund button.</summary>
        public decimal RemainingRefundable { get; set; }

        /// <summary>False when nothing on this order can be refunded by card.</summary>
        public bool CanRefund { get; set; }

        /// <summary>Why refunding is unavailable, in plain language for the admin.</summary>
        public string? UnavailableReason { get; set; }

        public List<OrderRefundDto> Refunds { get; set; } = new();
    }

    /// <summary>Result of a refund attempt. Never carries raw Stripe error text.</summary>
    public class RefundResultDto
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        /// <summary>Stripe refund ids created (plural — a refund can span several charges).</summary>
        public List<string> RefundIds { get; set; } = new();
        public decimal AmountRefunded { get; set; }
        public bool EmailSent { get; set; }
        /// <summary>Refreshed summary so the panel can re-render without a second round trip.</summary>
        public OrderRefundSummaryDto? Summary { get; set; }
    }
}

using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs.Commercial
{
    /// <summary>
    /// One cleaning offered to the admin on the "which visits does this invoice cover?" picker.
    ///
    /// Carries the reason it CANNOT be picked as well as the fact that it can, because an order
    /// silently missing from the list is the thing an admin cannot debug — "where is October 18?"
    /// has to have an answer on screen.
    /// </summary>
    public class InvoiceEligibleOrderDto
    {
        public int OrderId { get; set; }
        public DateTime ServiceDate { get; set; }
        public TimeSpan ServiceTime { get; set; }
        public string ServiceTypeName { get; set; } = string.Empty;
        public string ServiceAddress { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal Total { get; set; }

        /// <summary>The customer's own name on the order, for a client with several sites.</summary>
        public string ContactName { get; set; } = string.Empty;

        /// <summary>True when this order is on THIS invoice already.</summary>
        public bool IsOnThisInvoice { get; set; }

        public decimal? AllocatedAmount { get; set; }

        /// <summary>
        /// The invoice number already claiming this order, when another live one does —
        /// "Already included in DCI-2026-74521863". Null when it is free to bill.
        ///
        /// A VOID invoice never appears here: its links are kept for the audit trail but they are
        /// not a claim on the money, so the order can legitimately be billed again.
        /// </summary>
        public string? BilledOnInvoiceNumber { get; set; }

        public int? BilledOnInvoiceId { get; set; }

        /// <summary>False when the order may not be selected, for any reason.</summary>
        public bool CanSelect { get; set; }

        /// <summary>Why not. Rendered beside the row.</summary>
        public string? BlockedReason { get; set; }
    }

    public class InvoiceEligibleOrdersDto
    {
        public int ContractClientId { get; set; }
        public List<InvoiceEligibleOrderDto> Orders { get; set; } = new();
    }

    /// <summary>
    /// Which orders an invoice covers, and the agreed group total when one was negotiated.
    ///
    /// NOTE WHAT IS ABSENT: there is no per-order amount. Allocation is a SERVER rule (equal
    /// shares, remainder to the earliest service dates), so a caller cannot express "give this
    /// visit $2,000 and that one $500" — which is exactly the shape a mis-typed or hand-rolled
    /// request would take.
    /// </summary>
    public class SaveInvoiceOrdersDto
    {
        public List<int> OrderIds { get; set; } = new();

        /// <summary>
        /// The agreed TOTAL for the whole selected group — what the client pays. Null means "just
        /// bill what the orders cost", which is the default and the common case.
        /// </summary>
        [Range(typeof(decimal), "0", "10000000")]
        public decimal? NegotiatedGroupTotal { get; set; }
    }

    /// <summary>The allocation as the draft currently proposes it.</summary>
    public class InvoiceOrderAllocationDto
    {
        public string ServiceAddress { get; set; } = string.Empty;
        public int OrderId { get; set; }
        public DateTime ServiceDate { get; set; }
        public string Description { get; set; } = string.Empty;
        public decimal OriginalOrderTotal { get; set; }
        public decimal AllocatedAmount { get; set; }

        /// <summary>True while this is still a proposal — the order's own price is untouched.</summary>
        public bool IsProposal { get; set; }

        public DateTime? CommittedAt { get; set; }
        public DateTime? ActivatedOrderAt { get; set; }
        public string OrderStatus { get; set; } = string.Empty;
    }

    public class InvoiceOrdersResultDto
    {
        public List<DateTime> ServiceDates { get; set; } = new();
        public List<SaveInvoiceItemDto> Items { get; set; } = new();
        public int InvoiceId { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public decimal? NegotiatedGroupTotal { get; set; }

        /// <summary>Sum of what the selected orders charge today, before any negotiation.</summary>
        public decimal DefaultTotal { get; set; }

        /// <summary>What the invoice now totals.</summary>
        public decimal InvoiceTotal { get; set; }

        public List<InvoiceOrderAllocationDto> Allocations { get; set; } = new();

        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// Returned by "Create Next Invoice" when an unsent DRAFT already covers the period, instead
    /// of quietly making a second one. The UI turns it into "A draft invoice already exists for
    /// this period. [Open Draft]".
    /// </summary>
    public class ExistingDraftInvoiceDto
    {
        public bool IsUndated { get; set; }
        public int InvoiceId { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public DateTime? ServiceStartDate { get; set; }
        public DateTime? ServiceEndDate { get; set; }
        public string? ServicePeriodText { get; set; }
        public decimal Total { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}

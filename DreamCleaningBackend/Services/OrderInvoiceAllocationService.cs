using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// The residential half of the invoice ↔ order link: writing an allocated price onto an order,
    /// and activating it when the invoice is paid. See <see cref="IOrderInvoiceAllocationService"/>
    /// for why this lives on this side of the wall rather than inside the commercial services.
    /// </summary>
    public class OrderInvoiceAllocationService : IOrderInvoiceAllocationService
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditService _audit;
        private readonly ILogger<OrderInvoiceAllocationService> _logger;

        public OrderInvoiceAllocationService(
            ApplicationDbContext context,
            IAuditService audit,
            ILogger<OrderInvoiceAllocationService> logger)
        {
            _context = context;
            _audit = audit;
            _logger = logger;
        }

        public async Task<OrderAllocationSnapshot> ApplyAllocatedTotalAsync(
            int orderId, decimal allocatedTotal, int contractClientId,
            int invoiceId, string invoiceNumber, int? actingUserId)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId)
                ?? throw new InvalidOperationException($"Order #{orderId} not found.");

            var previousTotal = order.Total;
            var previousSubTotal = order.SubTotal;
            var previousTax = order.Tax;
            var previousPaymentMethod = order.PaymentMethod;
            var previousContractClientId = order.ContractClientId;

            // Tips sit OUTSIDE the taxed amount on every other pricing path in this codebase, so
            // an agreed per-visit figure is the service, and any tip rides on top exactly as it
            // does when an admin types a Total in the order editor.
            var taxable = Math.Max(0m, OrderPricingCalculator.Round2(allocatedTotal - order.Tips));

            // TAX-INCLUSIVE SPLIT BY SUBTRACTION — the same rule Custom Pricing uses. Re-deriving
            // the tax as round2(subTotal × rate) drifts a cent on roughly one amount in twenty
            // (no cent-valued subtotal satisfies S + round2(S × 8.875%) = 300.00), and the client
            // would then be invoiced $875.00 while the order charged $875.01.
            var (subTotal, tax) = OrderPricingCalculator.SplitTaxInclusiveAmount(taxable);

            // The agreed figure replaces the priced one outright: an invoice-level negotiation is
            // "this visit costs $875", not "$925.43 with $50.43 off". Discount columns are left
            // exactly as they are so the order still records what the customer was entitled to;
            // they are already inside the number that was agreed.
            order.SubTotal = subTotal;
            order.Tax = tax;
            order.Total = OrderPricingCalculator.Round2(subTotal + tax + order.Tips);

            // Captured ONCE, on the first allocation, so it always answers "what did this job cost
            // before any invoice renegotiated it" rather than "before the most recent one".
            order.PreInvoiceAllocationTotal ??= previousTotal;

            // THE ORDER IS ADOPTED BY THE INVOICE. A cleaning that is being billed on a commercial
            // invoice is not being collected any other way, so it is stamped with the client it is
            // billed to and switched to the Invoice method — which is what makes OrderPaymentFilter
            // treat it as unpaid until InvoicePaidAt, and what lets the invoice's settlement
            // activate it. Only reached when the invoice is SENT; an abandoned draft moves nothing.
            //
            // The order KEEPS ITS STATUS: a job already Done stays Done, exactly as the activation
            // path refuses to downgrade. Only the billing arrangement changes here.
            order.ContractClientId = contractClientId;
            order.PaymentMethod = PaymentMethod.Invoice;

            // An Invoice order is settled by its invoice, never by a manual record, so a stale
            // manual-payment reference would make it read as already collected on both paths.
            order.ManualPaymentRecordedAt = null;
            order.ManualPaymentRecordedByUserId = null;

            order.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _audit.LogActionAsync(
                AuditEntityTypes.OrderInvoiceAllocation, order.Id, "InvoiceAllocationApplied",
                oldValues: new
                {
                    SubTotal = previousSubTotal,
                    Tax = previousTax,
                    Total = previousTotal,
                    PaymentMethod = previousPaymentMethod.ToString(),
                    ContractClientId = previousContractClientId
                },
                newValues: new
                {
                    order.SubTotal,
                    order.Tax,
                    order.Total,
                    PaymentMethod = order.PaymentMethod.ToString(),
                    order.ContractClientId,
                    InvoiceId = invoiceId,
                    InvoiceNumber = invoiceNumber,
                    AllocatedAmount = allocatedTotal,
                    Reason = "Commercial invoice allocation"
                },
                actingUserId: actingUserId);

            _logger.LogInformation(
                "Order {OrderId} re-priced to {Total:C} and billed to client {ClientId} by invoice "
                + "{InvoiceNumber} (was {Previous:C}, {PreviousMethod}).",
                order.Id, order.Total, contractClientId, invoiceNumber, previousTotal, previousPaymentMethod);

            return new OrderAllocationSnapshot(
                previousTotal, (int)previousPaymentMethod, previousContractClientId);
        }

        public async Task<bool> ActivateAfterInvoicePaidAsync(
            int orderId, int invoiceId, string invoiceNumber, DateTime paidAtUtc)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return false;

            // Already settled by this (or an earlier) invoice payment. A retried webhook lands
            // here and does nothing, which is the whole point.
            if (order.InvoicePaidAt != null) return false;

            var previousStatus = order.Status;

            order.InvoicePaidAt = paidAtUtc;

            // NEVER DOWNGRADE. A cleaning that already happened is Done and an order somebody
            // cancelled is Cancelled; an invoice settling afterwards is bookkeeping catching up,
            // not a reason to reopen the job. Only a pending order becomes Active.
            if (OrderStatuses.Is(order.Status, OrderStatuses.Pending))
                order.Status = OrderStatuses.Active;

            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _audit.LogActionAsync(
                AuditEntityTypes.OrderInvoiceAllocation, order.Id, "InvoicePaymentActivatedOrder",
                oldValues: new { Status = previousStatus, InvoicePaidAt = (DateTime?)null },
                newValues: new
                {
                    order.Status,
                    order.InvoicePaidAt,
                    InvoiceId = invoiceId,
                    InvoiceNumber = invoiceNumber
                },
                actingUserId: null);

            _logger.LogInformation(
                "Order {OrderId} activated by payment of invoice {InvoiceNumber} (status {Before} → {After}).",
                order.Id, invoiceNumber, previousStatus, order.Status);

            return true;
        }

        public async Task RevertAllocatedTotalAsync(
            int orderId, decimal previousTotal, int? previousPaymentMethod,
            int? previousContractClientId, int invoiceId, string invoiceNumber, int? actingUserId)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return;

            var beforeSubTotal = order.SubTotal;
            var beforeTax = order.Tax;
            var beforeTotal = order.Total;
            var beforeMethod = order.PaymentMethod;
            var beforeClientId = order.ContractClientId;

            var taxable = Math.Max(0m, OrderPricingCalculator.Round2(previousTotal - order.Tips));
            var (subTotal, tax) = OrderPricingCalculator.SplitTaxInclusiveAmount(taxable);

            order.SubTotal = subTotal;
            order.Tax = tax;
            order.Total = OrderPricingCalculator.Round2(subTotal + tax + order.Tips);

            // The billing arrangement goes back with the price. Restored only when the snapshot
            // actually recorded one — a link row committed before this was tracked has nothing to
            // put back, and inventing a method would be worse than leaving the order as the voided
            // invoice left it. An unrecognised stored value is treated the same way.
            if (previousPaymentMethod.HasValue
                && Enum.IsDefined(typeof(PaymentMethod), previousPaymentMethod.Value))
            {
                order.PaymentMethod = (PaymentMethod)previousPaymentMethod.Value;
                order.ContractClientId = previousContractClientId;
            }

            order.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _audit.LogActionAsync(
                AuditEntityTypes.OrderInvoiceAllocation, order.Id, "InvoiceAllocationReverted",
                oldValues: new
                {
                    SubTotal = beforeSubTotal,
                    Tax = beforeTax,
                    Total = beforeTotal,
                    PaymentMethod = beforeMethod.ToString(),
                    ContractClientId = beforeClientId
                },
                newValues: new
                {
                    order.SubTotal,
                    order.Tax,
                    order.Total,
                    PaymentMethod = order.PaymentMethod.ToString(),
                    order.ContractClientId,
                    InvoiceId = invoiceId,
                    InvoiceNumber = invoiceNumber,
                    Reason = "Commercial invoice voided"
                },
                actingUserId: actingUserId);
        }
    }
}

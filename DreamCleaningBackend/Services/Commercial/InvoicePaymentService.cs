using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>
    /// Recording money received against a commercial invoice.
    ///
    /// ACH ARRIVES OUT OF BAND. Nothing here talks to a bank or to Stripe: the money lands in the
    /// company account, an authorized admin sees it on the statement and records it. There is
    /// deliberately no automated confirmation path, because inventing one without a real bank
    /// integration would mean marking invoices paid on evidence that does not exist.
    ///
    /// THE PAYMENT ROW IS THE RECORD, AND THE INVOICE FOLLOWS FROM IT. AmountPaid is recomputed by
    /// summing the rows every time, never incremented in place, so the invoice cannot drift away
    /// from its own payment history. Status is then re-derived by InvoiceStatusPolicy - which is
    /// why "Mark as Paid" is not a status assignment but a prefilled recording of a real payment.
    ///
    /// Everything runs inside ONE database transaction: payment row, recalculated totals, status,
    /// paid timestamp and activity entry commit together or not at all. A partial commit here is a
    /// payment that was received but is not reflected, or an invoice marked paid with nothing
    /// behind it.
    /// </summary>
    public class InvoicePaymentService
    {
        private readonly ApplicationDbContext _context;

        public InvoicePaymentService(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Records a payment and brings the invoice into line with it.
        ///
        /// Returns the invoice as it now stands, so the caller can tell whether this payment was
        /// the one that settled it (and should therefore trigger a receipt).
        /// </summary>
        public async Task<(CommercialInvoice Invoice, CommercialInvoicePayment Payment, bool BecamePaid)>
            RecordAsync(int invoiceId, RecordInvoicePaymentDto dto, int userId)
        {
            if (dto.Amount <= 0m)
                throw new InvoiceWorkflowException("A payment amount must be greater than zero.");

            var invoice = await _context.CommercialInvoices
                .Include(i => i.Items)
                .FirstOrDefaultAsync(i => i.Id == invoiceId)
                ?? throw new InvoiceWorkflowException("Invoice not found.");

            if (!InvoiceStatusPolicy.CanRecordPayment(invoice.Status))
                throw new InvoiceWorkflowException(invoice.Status == InvoiceStatus.Draft
                    ? "Send this invoice before recording a payment against it."
                    : "A voided invoice cannot take a payment.");

            var amount = InvoiceCalculator.Round2(dto.Amount);
            var wasPaid = invoice.Status == InvoiceStatus.Paid;

            // An overpayment is surfaced and REFUSED unless the admin explicitly accepts it. The
            // alternative - quietly banking more than was invoiced - hides a genuine mistake (a
            // duplicate transfer, a mistyped figure) at exactly the moment it can still be caught.
            var projectedPaid = InvoiceCalculator.Round2(invoice.AmountPaid + amount);
            if (projectedPaid > invoice.Total && !dto.AllowOverpayment)
            {
                var balance = InvoiceCalculator.ResolveBalance(invoice.Total, invoice.AmountPaid);
                throw new InvoiceWorkflowException(
                    $"That is more than the {balance:C} still owed on this invoice. "
                    + "Confirm the overpayment if the amount is correct.");
            }

            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var payment = new CommercialInvoicePayment
                {
                    CommercialInvoiceId = invoice.Id,
                    Amount = amount,
                    PaymentDate = (dto.PaymentDate ?? DateTime.UtcNow).Date,
                    PaymentMethod = dto.PaymentMethod,
                    TransactionReference = Trim(dto.TransactionReference),
                    InternalNote = Trim(dto.InternalNote),
                    RecordedByUserId = userId,
                    Provider = InvoicePaymentProvider.Manual,
                    CreatedAt = DateTime.UtcNow
                };

                _context.CommercialInvoicePayments.Add(payment);
                await _context.SaveChangesAsync();

                await ApplyPaymentTotalsAsync(invoice);
                await _context.SaveChangesAsync();

                var becamePaid = !wasPaid && invoice.Status == InvoiceStatus.Paid;

                await AddActivityAsync(invoice.Id, "payment_recorded",
                    $"{InvoiceService.PaymentMethodLabel(payment.PaymentMethod)} payment of "
                    + $"{payment.Amount:C} recorded"
                    + (string.IsNullOrWhiteSpace(payment.TransactionReference)
                        ? "."
                        : $" (reference {payment.TransactionReference})."),
                    userId);

                if (becamePaid)
                {
                    await AddActivityAsync(invoice.Id, "invoice_paid",
                        $"Invoice paid in full. Balance is now {0m:C}.", null, "System");
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return (invoice, payment, becamePaid);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        /// <summary>
        /// Reverses a recorded payment.
        ///
        /// The original row is NEVER edited or deleted - a reversing row is written for the
        /// negative amount instead, so the trail shows both the mistake and the correction. That
        /// is what makes the payment history usable as financial evidence rather than a mutable
        /// summary of someone's latest opinion.
        /// </summary>
        public async Task<CommercialInvoice> ReverseAsync(
            int invoiceId, int paymentId, string reason, int userId)
        {
            var invoice = await _context.CommercialInvoices
                .Include(i => i.Items)
                .FirstOrDefaultAsync(i => i.Id == invoiceId)
                ?? throw new InvoiceWorkflowException("Invoice not found.");

            var original = await _context.CommercialInvoicePayments
                .FirstOrDefaultAsync(p => p.Id == paymentId && p.CommercialInvoiceId == invoiceId)
                ?? throw new InvoiceWorkflowException("Payment not found on this invoice.");

            if (original.IsReversal)
                throw new InvoiceWorkflowException("A reversal cannot itself be reversed.");

            var alreadyReversed = await _context.CommercialInvoicePayments
                .AnyAsync(p => p.ReversesPaymentId == paymentId);

            if (alreadyReversed)
                throw new InvoiceWorkflowException("That payment has already been reversed.");

            if (string.IsNullOrWhiteSpace(reason))
                throw new InvoiceWorkflowException("A reason is required to reverse a payment.");

            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                _context.CommercialInvoicePayments.Add(new CommercialInvoicePayment
                {
                    CommercialInvoiceId = invoice.Id,
                    Amount = -original.Amount,
                    PaymentDate = DateTime.UtcNow.Date,
                    PaymentMethod = original.PaymentMethod,
                    TransactionReference = original.TransactionReference,
                    InternalNote = reason.Trim(),
                    IsReversal = true,
                    ReversesPaymentId = original.Id,
                    RecordedByUserId = userId,
                    CreatedAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                await ApplyPaymentTotalsAsync(invoice);
                await _context.SaveChangesAsync();

                await AddActivityAsync(invoice.Id, "payment_reversed",
                    $"Payment of {original.Amount:C} reversed. Reason: {reason.Trim()}", userId);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return invoice;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        /// <summary>
        /// Re-sums the payment rows onto the invoice and re-derives its status.
        ///
        /// The signed sum is deliberate: reversals are negative rows, so they subtract here
        /// without anything being deleted, and the arithmetic still reconciles.
        /// </summary>
        private async Task ApplyPaymentTotalsAsync(CommercialInvoice invoice)
        {
            var amounts = await _context.CommercialInvoicePayments
                .Where(p => p.CommercialInvoiceId == invoice.Id)
                .Select(p => p.Amount)
                .ToListAsync();

            invoice.AmountPaid = InvoiceCalculator.ResolveAmountPaid(amounts);
            invoice.UpdatedAt = DateTime.UtcNow;

            // Re-derives Paid / Partially Paid / Overdue and re-floors the balance. A reversal
            // that drops an invoice back below its total takes it out of Paid here, and clears
            // PaidAt with it.
            InvoiceService.RecalculateStatus(invoice);
        }

        /// <summary>
        /// Queues an activity row WITHOUT saving, so it commits inside the caller's transaction.
        /// InvoiceService.LogActivityAsync saves immediately, which would break the atomicity this
        /// service exists to provide.
        /// </summary>
        private async Task AddActivityAsync(
            int invoiceId, string action, string description, int? userId, string? actorOverride = null)
        {
            var actorName = actorOverride;

            if (actorName == null && userId.HasValue)
            {
                var user = await _context.Users
                    .Where(u => u.Id == userId.Value)
                    .Select(u => new { u.FirstName, u.LastName })
                    .FirstOrDefaultAsync();
                actorName = user == null ? null : $"{user.FirstName} {user.LastName}".Trim();
            }

            _context.CommercialInvoiceActivityLogs.Add(new CommercialInvoiceActivityLog
            {
                CommercialInvoiceId = invoiceId,
                Action = action,
                Description = description,
                UserId = userId,
                ActorName = actorName ?? "System",
                CreatedAt = DateTime.UtcNow
            });
        }

        private static string? Trim(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

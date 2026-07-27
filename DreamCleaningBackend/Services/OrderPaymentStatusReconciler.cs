using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Services
{
    /// <inheritdoc cref="IOrderPaymentStatusReconciler"/>
    public class OrderPaymentStatusReconciler : IOrderPaymentStatusReconciler
    {
        // An update row below this is a price DECREASE or a no-op; those are created already
        // marked paid and never represent money owed. Same threshold used everywhere else.
        private const decimal MinimumCollectableAmount = 0.01m;

        private readonly ApplicationDbContext _context;
        private readonly ILogger<OrderPaymentStatusReconciler> _logger;

        public OrderPaymentStatusReconciler(ApplicationDbContext context,
            ILogger<OrderPaymentStatusReconciler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<AdditionalPaymentResult> ApplyStripeAdditionalPaymentAsync(
            int orderId, string paymentIntentId, CancellationToken cancellationToken = default)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
            if (order == null)
            {
                _logger.LogWarning("Order {OrderId} not found while applying additional payment {PaymentIntentId}",
                    orderId, paymentIntentId);
                return new AdditionalPaymentResult(false, 0, 0m, false, null);
            }

            // Rows explicitly linked to this intent. The intent id is stamped onto every unpaid row
            // when the intent is created, so one payment normally settles all of them at once.
            var historiesToMarkPaid = await _context.OrderUpdateHistories
                .Where(h => h.OrderId == orderId && !h.IsPaid && h.PaymentIntentId == paymentIntentId)
                .ToListAsync(cancellationToken);

            if (historiesToMarkPaid.Count == 0)
            {
                // Nothing carries the id — either it was never stamped, or a racer already settled
                // these rows. Only the first case has something to collect, so look for real debt.
                var latest = await _context.OrderUpdateHistories
                    .Where(h => h.OrderId == orderId && !h.IsPaid && h.AdditionalAmount > MinimumCollectableAmount)
                    .OrderByDescending(h => h.UpdatedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (latest != null)
                {
                    latest.PaymentIntentId = paymentIntentId;
                    historiesToMarkPaid.Add(latest);
                }
            }

            var amountNewlyPaid = 0m;
            foreach (var history in historiesToMarkPaid)
            {
                history.IsPaid = true;
                history.PaidAt = DateTime.UtcNow;
                amountNewlyPaid += history.AdditionalAmount;
            }

            // Save BEFORE reconciling — the remaining-unpaid check runs in the database.
            await _context.SaveChangesAsync(cancellationToken);

            if (historiesToMarkPaid.Count > 0)
            {
                _logger.LogInformation(
                    "Marked {Count} OrderUpdateHistory row(s) (${Amount}) as paid for order {OrderId} via PaymentIntent {PaymentIntentId}",
                    historiesToMarkPaid.Count, amountNewlyPaid, orderId, paymentIntentId);
            }
            else
            {
                _logger.LogInformation(
                    "No unpaid additional amounts left on order {OrderId} for PaymentIntent {PaymentIntentId} — already settled",
                    orderId, paymentIntentId);
            }

            var statusReactivated = await ReconcileStatusAfterAdditionalPaymentAsync(orderId, cancellationToken);

            return new AdditionalPaymentResult(true, historiesToMarkPaid.Count, amountNewlyPaid,
                statusReactivated, order.Status);
        }

        public async Task<bool> ReconcileStatusAfterAdditionalPaymentAsync(
            int orderId, CancellationToken cancellationToken = default)
        {
            // Persist whatever the caller staged, so the check below cannot read stale rows.
            if (_context.ChangeTracker.HasChanges())
            {
                await _context.SaveChangesAsync(cancellationToken);
            }

            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
            if (order == null)
                return false;

            var hasRemainingUnpaid = await _context.OrderUpdateHistories.AnyAsync(
                h => h.OrderId == orderId && !h.IsPaid && h.AdditionalAmount > MinimumCollectableAmount,
                cancellationToken);

            // Only an order that went Pending purely to await this top-up comes back. An unpaid base
            // order stays Pending, and Done/Cancelled/Refunded are never rewritten.
            if (hasRemainingUnpaid ||
                !order.IsPaid ||
                !string.Equals(order.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            order.Status = "Active";
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Order {OrderId} reactivated (Pending -> Active) — no additional amounts outstanding", orderId);
            return true;
        }
    }
}

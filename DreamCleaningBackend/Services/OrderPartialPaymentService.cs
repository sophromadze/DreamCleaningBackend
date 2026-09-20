using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <inheritdoc cref="IOrderPartialPaymentService"/>
    public class OrderPartialPaymentService : IOrderPartialPaymentService
    {
        /// <summary>
        /// The Stripe metadata "type" every part-payment intent carries. Deliberately NOT
        /// "booking": the webhook's booking handler marks the WHOLE order paid from that
        /// discriminator alone, so a $1,000 deposit arriving under it would settle a $2,743.65
        /// order. Mirrors RecurringCustomerPaymentService.StripeMetadataType in shape and purpose.
        /// </summary>
        public const string StripeMetadataType = "order_partial";

        private readonly ApplicationDbContext _context;
        private readonly IAuditService _auditService;
        private readonly ILogger<OrderPartialPaymentService> _logger;

        public OrderPartialPaymentService(
            ApplicationDbContext context,
            IAuditService auditService,
            ILogger<OrderPartialPaymentService> logger)
        {
            _context = context;
            _auditService = auditService;
            _logger = logger;
        }

        public async Task<OrderPaymentBalanceDto> GetBalanceAsync(int orderId, CancellationToken ct = default)
        {
            var order = await _context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct)
                ?? throw new PartialPaymentException("Order not found.");

            var rows = await _context.OrderPartialPayments
                .AsNoTracking()
                .Include(p => p.RequestedByUser)
                .Include(p => p.ManualPaymentRecordedByUser)
                .Where(p => p.OrderId == orderId)
                .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
                .ToListAsync(ct);

            var canRequest = OrderBalance.CanRequestPartialPayment(order, out var refusal);
            var pending = rows.LastOrDefault(p => p.Status == OrderPartialPaymentStatus.Pending);

            // A live request is itself a reason not to open a second one — stated here rather than
            // in OrderBalance, which answers questions about the order and knows nothing about rows.
            if (canRequest && pending != null)
            {
                canRequest = false;
                refusal = "There is already a payment request waiting for this order. Cancel it first.";
            }

            return new OrderPaymentBalanceDto
            {
                Total = order.Total,
                AmountPaid = order.AmountPaid,
                AmountDue = OrderBalance.AmountDue(order),
                IsPartiallyPaid = OrderBalance.IsPartiallyPaid(order),
                OverpaidAmount = OrderBalance.OverpaidAmount(order),
                CanRequestPartialPayment = canRequest,
                CannotRequestReason = refusal,
                PendingRequest = pending == null ? null : ToDto(pending),
                History = rows.Select(ToDto).ToList()
            };
        }

        public async Task<OrderPartialPayment?> GetPendingRequestAsync(int orderId, CancellationToken ct = default) =>
            await _context.OrderPartialPayments
                .Where(p => p.OrderId == orderId && p.Status == OrderPartialPaymentStatus.Pending)
                .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
                .FirstOrDefaultAsync(ct);

        public async Task<OrderPartialPayment> CreateRequestAsync(
            int orderId, decimal amount, string? note, int adminUserId, CancellationToken ct = default)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct)
                ?? throw new PartialPaymentException("Order not found.");

            if (!OrderBalance.CanRequestPartialPayment(order, out var refusal))
                throw new PartialPaymentException(refusal!);

            var existing = await GetPendingRequestAsync(orderId, ct);
            if (existing != null)
                throw new PartialPaymentException(
                    $"There is already a payment request for ${existing.RequestedAmount:F2} waiting on this order. Cancel it before asking for a different amount.");

            amount = OrderPricingCalculator.Round2(amount);
            if (amount < OrderBalance.StripeMinimumChargeAmount)
                throw new PartialPaymentException($"The amount must be at least ${OrderBalance.StripeMinimumChargeAmount:F2}.");

            var due = OrderBalance.AmountDue(order);
            if (amount > due)
                throw new PartialPaymentException($"That is more than the ${due:F2} still owed on this order.");

            // A slice that would leave an uncollectable stub behind (less than Stripe's minimum) is
            // refused rather than silently rounded up: the admin agreed a figure with the customer,
            // and changing it here would charge something nobody discussed.
            var remainder = OrderPricingCalculator.Round2(due - amount);
            if (remainder > 0m && remainder < OrderBalance.StripeMinimumChargeAmount)
                throw new PartialPaymentException(
                    $"That would leave ${remainder:F2} owing, which is below the ${OrderBalance.StripeMinimumChargeAmount:F2} minimum Stripe can charge. Ask for ${due:F2} instead.");

            var row = new OrderPartialPayment
            {
                OrderId = orderId,
                RequestedAmount = amount,
                Status = OrderPartialPaymentStatus.Pending,
                RequestedByUserId = adminUserId,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                CreatedAt = DateTime.UtcNow
            };

            _context.OrderPartialPayments.Add(row);
            await _context.SaveChangesAsync(ct);

            await LogAsync(orderId, "PartialPaymentRequested", new
            {
                PartialPaymentId = row.Id,
                RequestedAmount = amount,
                OrderTotal = order.Total,
                AmountPaid = order.AmountPaid,
                AmountDueBefore = due,
                RemainingAfterThisPayment = remainder,
                Note = row.Note
            }, adminUserId);

            return row;
        }

        public async Task<OrderPartialPayment> CancelRequestAsync(
            int orderId, int requestId, int adminUserId, CancellationToken ct = default)
        {
            var row = await _context.OrderPartialPayments
                .FirstOrDefaultAsync(p => p.Id == requestId && p.OrderId == orderId, ct)
                ?? throw new PartialPaymentException("Payment request not found.");

            if (row.Status == OrderPartialPaymentStatus.Paid)
                throw new PartialPaymentException("That payment has already been made. Refund it on the order instead.");
            if (row.Status == OrderPartialPaymentStatus.Cancelled)
                return row;

            row.Status = OrderPartialPaymentStatus.Cancelled;
            row.CancelledByUserId = adminUserId;
            row.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            await LogAsync(orderId, "PartialPaymentCancelled", new
            {
                PartialPaymentId = row.Id,
                RequestedAmount = row.RequestedAmount,
                Note = row.Note
            }, adminUserId);

            return row;
        }

        public async Task MarkNotificationSentAsync(int requestId, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            await _context.OrderPartialPayments
                .Where(p => p.Id == requestId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.NotificationSentAt, now)
                    .SetProperty(p => p.UpdatedAt, now), ct);
        }

        public async Task<PartialPaymentSettlement> SettleAsync(
            int orderId, string paymentIntentId, decimal amountReceived, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(paymentIntentId))
                throw new PartialPaymentException("A payment reference is required to record a payment.");

            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct)
                ?? throw new PartialPaymentException("Order not found.");

            var amount = OrderPricingCalculator.Round2(amountReceived);
            if (amount < OrderBalance.MinimumMeaningfulAmount)
                throw new PartialPaymentException("Stripe reported no money received for this payment.");

            // The row this intent was created for. Stamped when the intent was created, so a webhook
            // that beats the browser's confirm still finds it.
            var row = await _context.OrderPartialPayments
                .FirstOrDefaultAsync(p => p.PaymentIntentId == paymentIntentId, ct);

            if (row != null && row.Status == OrderPartialPaymentStatus.Paid)
            {
                // Already recorded — a retried webhook, or the webhook and the browser arriving
                // together. Report the current state; moving the money again would double-credit it.
                var alreadyDue = OrderBalance.AmountDue(order);
                return new PartialPaymentSettlement(
                    Applied: false,
                    PartialPaymentId: row.Id,
                    AmountApplied: 0m,
                    AmountPaidTotal: order.AmountPaid,
                    AmountDue: alreadyDue,
                    OrderNowFullyPaid: order.IsPaid || OrderBalance.SettlesOrder(alreadyDue));
            }

            if (row == null)
            {
                // Nothing carries the id. Either the stamp never happened, or money arrived for an
                // order with no request at all. Record it against the live request when there is
                // one, and invent a row when there is not — losing track of money that has actually
                // arrived is far worse than an unexplained row in the history.
                row = await GetPendingRequestAsync(orderId, ct);

                if (row == null)
                {
                    _logger.LogWarning(
                        "Partial payment {PaymentIntentId} of {Amount} arrived for order {OrderId} with no matching request; recording it anyway.",
                        paymentIntentId, amount, orderId);

                    row = new OrderPartialPayment
                    {
                        OrderId = orderId,
                        RequestedAmount = amount,
                        RequestedByUserId = null,
                        Note = "Recorded from Stripe — no matching payment request was open.",
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.OrderPartialPayments.Add(row);
                    await _context.SaveChangesAsync(ct);
                }
            }

            var now = DateTime.UtcNow;

            // Conditional update, not a tracked save: the browser's confirm and the webhook can run
            // this concurrently, and only one of them may add to AmountPaid. Whoever loses moves
            // zero rows and reports that nothing was applied.
            //
            // A CANCELLED row is deliberately included. An admin can withdraw a request while the
            // customer is already on the payment page; if the charge then succeeds the money is
            // real, and refusing to record it would leave it invisible on the order.
            var claimed = await _context.OrderPartialPayments
                .Where(p => p.Id == row.Id && p.Status != OrderPartialPaymentStatus.Paid)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, OrderPartialPaymentStatus.Paid)
                    .SetProperty(p => p.PaidAmount, amount)
                    .SetProperty(p => p.PaidAt, now)
                    .SetProperty(p => p.PaymentIntentId, paymentIntentId)
                    .SetProperty(p => p.UpdatedAt, now), ct);

            if (claimed == 0)
            {
                await _context.Entry(order).ReloadAsync(ct);
                var settledDue = OrderBalance.AmountDue(order);
                return new PartialPaymentSettlement(
                    Applied: false,
                    PartialPaymentId: row.Id,
                    AmountApplied: 0m,
                    AmountPaidTotal: order.AmountPaid,
                    AmountDue: settledDue,
                    OrderNowFullyPaid: order.IsPaid || OrderBalance.SettlesOrder(settledDue));
            }

            // Increment in the database rather than from a value read earlier, so two slices
            // settling at once cannot each write "old + mine" and lose one of them.
            await _context.Orders
                .Where(o => o.Id == orderId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.AmountPaid, o => o.AmountPaid + amount)
                    .SetProperty(o => o.UpdatedAt, now), ct);

            await _context.Entry(order).ReloadAsync(ct);
            var amountDue = OrderBalance.AmountDue(order);
            var fullyPaid = OrderBalance.SettlesOrder(amountDue);

            await LogAsync(orderId, "PartialPaymentReceived", new
            {
                PartialPaymentId = row.Id,
                RequestedAmount = row.RequestedAmount,
                PaidAmount = amount,
                PaymentIntentId = paymentIntentId,
                OrderTotal = order.Total,
                AmountPaidTotal = order.AmountPaid,
                AmountDue = amountDue,
                OrderFullyPaid = fullyPaid
            }, actingUserId: null);

            _logger.LogInformation(
                "Partial payment of {Amount} recorded on order {OrderId}; {Due} still due (fully paid: {FullyPaid}).",
                amount, orderId, amountDue, fullyPaid);

            return new PartialPaymentSettlement(
                Applied: true,
                PartialPaymentId: row.Id,
                AmountApplied: amount,
                AmountPaidTotal: order.AmountPaid,
                AmountDue: amountDue,
                OrderNowFullyPaid: fullyPaid);
        }

        public async Task<PartialPaymentSettlement> RecordManualPaymentAsync(
            int orderId, int requestId, PaymentMethod method, string? paymentReference, string? paymentNotes,
            int adminUserId, CancellationToken ct = default)
        {
            if (method == PaymentMethod.Normal)
                throw new PartialPaymentException("Choose the method this slice was actually paid with — Cash, Zelle, Check, Other or Invoice.");

            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct)
                ?? throw new PartialPaymentException("Order not found.");

            var row = await _context.OrderPartialPayments
                .FirstOrDefaultAsync(p => p.Id == requestId && p.OrderId == orderId, ct)
                ?? throw new PartialPaymentException("Payment request not found.");

            if (row.Status == OrderPartialPaymentStatus.Paid)
                throw new PartialPaymentException("That payment has already been recorded.");
            if (row.Status == OrderPartialPaymentStatus.Cancelled)
                throw new PartialPaymentException("That request was cancelled. Create a new one for this amount instead.");

            // The order may have been edited down since the request was created (or another slice
            // settled in the meantime) — never let a stale request collect more than is actually
            // owed right now. Unlike SettleAsync there is no Stripe charge to reconcile against, so
            // this is the only guard against overcollecting a manual slice.
            var due = OrderBalance.AmountDue(order);
            if (row.RequestedAmount > due + OrderBalance.MinimumMeaningfulAmount)
                throw new PartialPaymentException(
                    $"This order now owes only ${due:F2}, less than the ${row.RequestedAmount:F2} requested. Cancel this request and ask for the right amount instead.");

            var reference = string.IsNullOrWhiteSpace(paymentReference) ? null : paymentReference.Trim();
            var notes = string.IsNullOrWhiteSpace(paymentNotes) ? null : paymentNotes.Trim();
            var amount = row.RequestedAmount;
            var now = DateTime.UtcNow;

            // Conditional update, not a tracked save — same reasoning as SettleAsync: two admins
            // (or a double-click) racing to record the same request must not both credit it.
            var claimed = await _context.OrderPartialPayments
                .Where(p => p.Id == row.Id && p.Status == OrderPartialPaymentStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, OrderPartialPaymentStatus.Paid)
                    .SetProperty(p => p.PaidAmount, amount)
                    .SetProperty(p => p.PaidAt, now)
                    .SetProperty(p => p.PaymentMethod, method)
                    .SetProperty(p => p.PaymentReference, reference)
                    .SetProperty(p => p.PaymentNotes, notes)
                    .SetProperty(p => p.ManualPaymentRecordedAt, now)
                    .SetProperty(p => p.ManualPaymentRecordedByUserId, adminUserId)
                    .SetProperty(p => p.UpdatedAt, now), ct);

            if (claimed == 0)
                throw new PartialPaymentException("This request was just handled by someone else. Refresh and check its status.");

            // Increment in the database rather than from a value read earlier, matching SettleAsync.
            await _context.Orders
                .Where(o => o.Id == orderId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.AmountPaid, o => o.AmountPaid + amount)
                    .SetProperty(o => o.UpdatedAt, now), ct);

            await _context.Entry(order).ReloadAsync(ct);
            var amountDue = OrderBalance.AmountDue(order);
            var fullyPaid = OrderBalance.SettlesOrder(amountDue);

            await LogAsync(orderId, "PartialPaymentManuallyRecorded", new
            {
                PartialPaymentId = row.Id,
                RequestedAmount = row.RequestedAmount,
                PaidAmount = amount,
                PaymentMethod = method.ToString(),
                PaymentReference = reference,
                PaymentNotes = notes,
                OrderTotal = order.Total,
                AmountPaidTotal = order.AmountPaid,
                AmountDue = amountDue,
                OrderFullyPaid = fullyPaid
            }, adminUserId);

            _logger.LogInformation(
                "Partial payment of {Amount} recorded manually ({Method}) on order {OrderId}; {Due} still due (fully paid: {FullyPaid}).",
                amount, method, orderId, amountDue, fullyPaid);

            return new PartialPaymentSettlement(
                Applied: true,
                PartialPaymentId: row.Id,
                AmountApplied: amount,
                AmountPaidTotal: order.AmountPaid,
                AmountDue: amountDue,
                OrderNowFullyPaid: fullyPaid);
        }

        public async Task<bool> RecordCombinedPaymentSliceAsync(
            int orderId, decimal amount, decimal expectedTotal, decimal expectedAmountPaid,
            string paymentIntentId, int batchId, CancellationToken ct = default)
        {
            amount = OrderPricingCalculator.Round2(amount);
            if (amount < OrderBalance.MinimumMeaningfulAmount) return true;
            var now = DateTime.UtcNow;

            // Conditional on the figures the share was computed against, and incremented in the
            // database — same reasoning as SettleAsync.
            var moved = await _context.Orders
                .Where(o => o.Id == orderId && !o.IsPaid && o.InvoicePaidAt == null
                            && o.Total == expectedTotal && o.AmountPaid == expectedAmountPaid)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.AmountPaid, o => o.AmountPaid + amount)
                    .SetProperty(o => o.UpdatedAt, now), ct);
            if (moved == 0) return false;

            var row = new OrderPartialPayment
            {
                OrderId = orderId,
                RequestedAmount = amount,
                PaidAmount = amount,
                Status = OrderPartialPaymentStatus.Paid,
                PaidAt = now,
                PaymentMethod = PaymentMethod.Normal,
                PaymentReference = paymentIntentId,
                Note = $"Share of combined payment #{batchId} (Pay all upcoming).",
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.OrderPartialPayments.Add(row);
            await _context.SaveChangesAsync(ct);

            await LogAsync(orderId, "PartialPaymentReceived", new
            {
                PartialPaymentId = row.Id,
                PaidAmount = amount,
                CombinedPaymentBatchId = batchId,
                PaymentIntentId = paymentIntentId,
                OrderTotal = expectedTotal,
                AmountPaidTotal = expectedAmountPaid + amount
            }, actingUserId: null);
            return true;
        }

        public OrderPartialPaymentDto ToDto(OrderPartialPayment row) => new()
        {
            Id = row.Id,
            OrderId = row.OrderId,
            RequestedAmount = row.RequestedAmount,
            PaidAmount = row.PaidAmount,
            Status = row.Status.ToString(),
            PaidAt = row.PaidAt,
            CreatedAt = row.CreatedAt,
            NotificationSentAt = row.NotificationSentAt,
            Note = row.Note,
            RequestedByName = FormatAdminName(row.RequestedByUser),
            PaymentMethod = row.PaymentMethod.ToString(),
            PaymentReference = row.PaymentReference,
            PaymentNotes = row.PaymentNotes,
            ManualPaymentRecordedByName = FormatAdminName(row.ManualPaymentRecordedByUser)
        };

        /// <summary>"F. LastName", matching the assigned-admin pill the orders panel already uses.</summary>
        private static string? FormatAdminName(User? user)
        {
            if (user == null) return null;
            var first = user.FirstName?.Trim();
            var last = user.LastName?.Trim();
            if (!string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(last))
                return $"{char.ToUpper(first[0])}. {last}";
            return !string.IsNullOrEmpty(first) ? first : last;
        }

        private async Task LogAsync(int orderId, string action, object payload, int? actingUserId)
        {
            try
            {
                await _auditService.LogActionAsync(
                    AuditEntityTypes.OrderPaymentAction, orderId, action, null, payload, null, actingUserId);
            }
            catch (Exception ex)
            {
                // The money movement is the thing that must not fail. A missing audit row is worth
                // a log line, never a rolled-back payment.
                _logger.LogError(ex, "Failed to audit {Action} on order {OrderId}", action, orderId);
            }
        }
    }
}

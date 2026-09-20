using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Billing;
using DreamCleaningBackend.Models.Commercial;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Billing
{
    public interface IBillingHistoryService
    {
        Task<BillingHistoryPageDto> GetHistoryAsync(int userId, int page, int pageSize);
        Task<List<OutstandingObligationDto>> GetOutstandingAsync(int userId);
        Task<List<AdminBillingAttemptDto>> GetRecentAttemptsAsync(int userId, int take);
    }

    /// <summary>
    /// ONE billing history across residential orders, recurring cleanings and commercial invoices —
    /// READ from the records each domain already keeps, never a second ledger.
    ///
    /// Rules that keep it honest:
    ///  • An order billed through a commercial invoice (<c>PaymentMethod.Invoice</c>) is NOT listed
    ///    as its own payment: the invoice payment that covered it is. Listing both would show the
    ///    same money twice.
    ///  • Several orders paid by ONE charge ("Pay all upcoming") appear as ONE row, grouped by the
    ///    PaymentIntent they share.
    ///  • Part-payments, additional (top-up) payments and refunds are their own rows, from their own
    ///    tables. Declined / pending saved-card attempts come from BillingPaymentAttempts.
    ///  • Commercial invoices are shown only for business clients linked to THIS account.
    /// Statuses are only ones the underlying model can actually express.
    /// </summary>
    public class BillingHistoryService : IBillingHistoryService
    {
        private readonly ApplicationDbContext _context;

        public BillingHistoryService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<BillingHistoryPageDto> GetHistoryAsync(int userId, int page, int pageSize)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 5, 50);

            var items = new List<BillingHistoryItemDto>();

            var orders = await _context.Orders.AsNoTracking()
                .Include(o => o.ServiceType)
                .Where(o => o.UserId == userId)
                .ToListAsync();
            var orderIds = orders.Select(o => o.Id).ToList();
            var byId = orders.ToDictionary(o => o.Id);

            var partials = await _context.OrderPartialPayments.AsNoTracking()
                .Where(p => orderIds.Contains(p.OrderId) && p.Status == OrderPartialPaymentStatus.Paid)
                .ToListAsync();
            var partialIntentIds = partials.Where(p => p.PaymentIntentId != null).Select(p => p.PaymentIntentId!).ToHashSet();

            var topUps = await _context.OrderUpdateHistories.AsNoTracking()
                .Where(h => orderIds.Contains(h.OrderId) && h.IsPaid && h.AdditionalAmount > 0.01m)
                .ToListAsync();

            var refunds = await _context.OrderRefunds.AsNoTracking()
                .Where(r => orderIds.Contains(r.OrderId))
                .ToListAsync();

            var attempts = await _context.BillingPaymentAttempts.AsNoTracking()
                .Where(a => a.UserId == userId)
                .ToListAsync();
            var attemptByIntent = attempts.Where(a => a.StripePaymentIntentId != null)
                .GroupBy(a => a.StripePaymentIntentId!)
                .ToDictionary(g => g.Key, g => g.First());

            // ── Order base payments, grouped by the charge that paid them ──
            var paidOrders = orders
                .Where(o => o.PaymentMethod != PaymentMethod.Invoice
                            && (o.IsPaid || (o.PaymentMethod != PaymentMethod.Normal && o.ManualPaymentRecordedAt != null)))
                .ToList();

            // "Pay all upcoming" links its orders through the batch items, never through
            // Order.PaymentIntentId (UNIQUE — one charge cannot be stamped on several orders).
            var batchIntentByOrder = (await _context.OrderPaymentBatchItems.AsNoTracking()
                    .Where(i => orderIds.Contains(i.OrderId) && i.AppliedToOrder
                                && i.Batch!.Status == OrderPaymentBatchStatus.Paid && i.Batch.PaymentIntentId != null)
                    .Select(i => new { i.OrderId, i.Batch!.PaymentIntentId })
                    .ToListAsync())
                .GroupBy(x => x.OrderId)
                .ToDictionary(g => g.Key, g => g.First().PaymentIntentId!);

            string ChargeKey(Order o) =>
                o.PaymentMethod == PaymentMethod.Normal && batchIntentByOrder.TryGetValue(o.Id, out var batchIntent) ? batchIntent
                : o.PaymentMethod == PaymentMethod.Normal && !string.IsNullOrEmpty(o.PaymentIntentId) ? o.PaymentIntentId!
                : $"order-{o.Id}";

            foreach (var group in paidOrders.GroupBy(ChargeKey))
            {
                var groupOrders = group.OrderBy(o => o.ServiceDate).ToList();
                var first = groupOrders[0];

                // What the base charge itself collected: the snapshot taken when it was paid, less
                // any part-payments already listed as their own rows.
                decimal BaseAmount(Order o)
                {
                    var paidTotal = o.InitialTotal > 0 ? o.InitialTotal : o.Total;
                    var viaPartials = partials.Where(p => p.OrderId == o.Id).Sum(p => p.PaidAmount ?? 0m);
                    return Math.Max(0m, OrderPricingCalculator.Round2(paidTotal - viaPartials));
                }

                var amount = groupOrders.Sum(BaseAmount);
                if (amount <= 0m) continue;

                // The final slice of a part-paid order IS its partial row; don't list it twice.
                if (partialIntentIds.Contains(group.Key)) continue;

                var isManual = first.PaymentMethod != PaymentMethod.Normal;
                var isGift = first.PaymentIntentId?.StartsWith("giftcard_full_") == true;
                attemptByIntent.TryGetValue(group.Key, out var viaAttempt);

                items.Add(new BillingHistoryItemDto
                {
                    Key = $"order-payment:{group.Key}",
                    Date = (isManual ? first.ManualPaymentRecordedAt : first.PaidAt) ?? first.UpdatedAt ?? first.CreatedAt,
                    Kind = "order",
                    Reference = groupOrders.Count == 1 ? $"Order #{first.Id}" : $"{groupOrders.Count} cleanings",
                    Description = groupOrders.Count == 1
                        ? $"{first.GetDisplayServiceTypeName("Cleaning")} on {first.ServiceDate:MMM d, yyyy}"
                          + (first.RecurringSeriesId.HasValue ? " (recurring)" : "")
                          + (viaAttempt != null && viaAttempt.Trigger == BillingAttemptTrigger.AutoPayRecurring ? " — automatic payment" : "")
                        : "Combined payment: " + string.Join(", ", groupOrders.Select(o => $"#{o.Id} ({o.ServiceDate:MMM d})")),
                    Amount = amount,
                    Status = "Paid",
                    PaymentMethodLabel = isManual ? $"Paid by {first.PaymentMethod}"
                        : isGift ? "Gift card"
                        : viaAttempt != null ? AttemptCardLabel(viaAttempt)
                        : "Card",
                    OrderId = groupOrders.Count == 1 ? first.Id : null,
                    ActionUrl = groupOrders.Count == 1 ? $"/order/{first.Id}" : null,
                    ActionLabel = groupOrders.Count == 1 ? "View order" : null
                });
            }

            foreach (var p in partials)
            {
                byId.TryGetValue(p.OrderId, out var o);
                items.Add(new BillingHistoryItemDto
                {
                    Key = $"partial:{p.Id}",
                    Date = p.PaidAt ?? p.CreatedAt,
                    Kind = "order",
                    Reference = $"Order #{p.OrderId}",
                    Description = $"Part payment{(o != null ? $" — cleaning on {o.ServiceDate:MMM d, yyyy}" : "")}",
                    Amount = p.PaidAmount ?? 0m,
                    Status = "Paid",
                    PaymentMethodLabel = "Card",
                    OrderId = p.OrderId,
                    ActionUrl = $"/order/{p.OrderId}",
                    ActionLabel = "View order"
                });
            }

            foreach (var h in topUps)
            {
                items.Add(new BillingHistoryItemDto
                {
                    Key = $"topup:{h.Id}",
                    Date = h.PaidAt ?? h.UpdatedAt,
                    Kind = "order",
                    Reference = $"Order #{h.OrderId}",
                    Description = "Additional payment after an order change",
                    Amount = h.AdditionalAmount,
                    Status = "Paid",
                    PaymentMethodLabel = h.PaymentMethod == PaymentMethod.Normal ? "Card" : $"Paid by {h.PaymentMethod}",
                    OrderId = h.OrderId,
                    ActionUrl = $"/order/{h.OrderId}",
                    ActionLabel = "View order"
                });
            }

            foreach (var r in refunds)
            {
                var status = r.Status?.ToLowerInvariant() switch
                {
                    "succeeded" => "Refunded",
                    "pending" => "Pending",
                    _ => "Failed"
                };
                // A refund that never went through is not money back; only show it while pending.
                if (status == "Failed") continue;
                items.Add(new BillingHistoryItemDto
                {
                    Key = $"refund:{r.Id}",
                    Date = r.CreatedAt,
                    Kind = "order",
                    Reference = $"Order #{r.OrderId}",
                    Description = "Refund",
                    Amount = -r.Amount,
                    Status = status,
                    PaymentMethodLabel = "Card",
                    OrderId = r.OrderId,
                    ActionUrl = $"/order/{r.OrderId}",
                    ActionLabel = "View order"
                });
            }

            // ── Saved-card attempts that did NOT succeed (successes are the payment rows above) ──
            foreach (var a in attempts.Where(a => a.Status is BillingAttemptStatus.Failed or BillingAttemptStatus.RequiresAction
                                                   or BillingAttemptStatus.Unknown or BillingAttemptStatus.Processing
                                                   or BillingAttemptStatus.Pending))
            {
                var status = a.Status switch
                {
                    BillingAttemptStatus.Failed => "Failed",
                    BillingAttemptStatus.RequiresAction => "Requires Action",
                    _ => "Pending"
                };
                items.Add(new BillingHistoryItemDto
                {
                    Key = $"attempt:{a.Id}",
                    Date = a.CreatedAt,
                    Kind = "attempt",
                    Reference = a.OrderId.HasValue ? $"Order #{a.OrderId}" : $"Invoice #{a.CommercialInvoiceId}",
                    Description = (a.Trigger switch
                    {
                        BillingAttemptTrigger.AutoPayRecurring or BillingAttemptTrigger.AutoPayCommercial => "Automatic payment",
                        BillingAttemptTrigger.AdminCharge => "Card charge by our office",
                        _ => "Card payment"
                    }) + (a.CardRole == BillingCardRole.Backup ? " (Backup card)" : ""),
                    Amount = a.Amount,
                    Status = status,
                    PaymentMethodLabel = AttemptCardLabel(a),
                    OrderId = a.OrderId,
                    InvoiceId = a.CommercialInvoiceId
                });
            }

            // ── Commercial invoices of business clients linked to this account ──
            var clientIds = await _context.ContractClients.AsNoTracking()
                .Where(c => c.SourceUserId == userId)
                .Select(c => c.Id)
                .ToListAsync();
            if (clientIds.Count > 0)
            {
                var payments = await _context.CommercialInvoicePayments.AsNoTracking()
                    .Include(p => p.Invoice)
                    .Where(p => p.Invoice != null && clientIds.Contains(p.Invoice.ContractClientId)
                                && p.Invoice.Status != InvoiceStatus.Draft)
                    .ToListAsync();

                foreach (var p in payments)
                {
                    items.Add(new BillingHistoryItemDto
                    {
                        Key = $"invoice-payment:{p.Id}",
                        // An invoice payment carries a DATE (no time), so it used to sort at
                        // midnight — below every card payment made the same day, however much
                        // later it was actually recorded. When the payment date IS the day it was
                        // recorded, the recorded timestamp is the truthful position; a deliberately
                        // back-dated payment (an ACH credited last week, entered today) keeps its
                        // own date, where the customer looks for it.
                        Date = p.PaymentDate == default
                            ? p.CreatedAt
                            : NyTimeHelper.ToNy(p.CreatedAt).Date == p.PaymentDate.Date ? p.CreatedAt : p.PaymentDate,
                        Kind = "invoice",
                        Reference = $"Invoice {p.Invoice!.InvoiceNumber}",
                        Description = p.IsReversal ? "Payment reversed" : "Invoice payment",
                        Amount = p.Amount,
                        Status = p.IsReversal ? "Refunded" : "Paid",
                        PaymentMethodLabel = MethodLabel(p.PaymentMethod),
                        InvoiceId = p.CommercialInvoiceId,
                        ActionUrl = $"/invoice/{p.Invoice.PublicToken}",
                        ActionLabel = "View invoice"
                    });
                }
            }

            var ordered = items.OrderByDescending(i => i.Date).ThenByDescending(i => i.Key).ToList();
            return new BillingHistoryPageDto
            {
                Items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                Page = page,
                PageSize = pageSize,
                TotalCount = ordered.Count
            };
        }

        public async Task<List<OutstandingObligationDto>> GetOutstandingAsync(int userId)
        {
            var result = new List<OutstandingObligationDto>();

            var activeLocks = await _context.BillingPaymentAttempts.AsNoTracking()
                .Where(a => a.UserId == userId && a.ActiveLockKey != null)
                .Select(a => a.ObligationKey)
                .ToListAsync();
            var failedObligations = await _context.BillingNotifications.AsNoTracking()
                .Where(n => n.UserId == userId && n.ResolvedAt == null
                            && (n.Type == BillingNotificationType.AutoPayFailed || n.Type == BillingNotificationType.AuthenticationRequired))
                .Select(n => n.ObligationKey)
                .ToListAsync();

            var today = NyTimeHelper.NowNy.Date;
            var orders = await _context.Orders.AsNoTracking()
                .Include(o => o.ServiceType)
                .Where(o => o.UserId == userId && !o.IsPaid && o.InvoicePaidAt == null
                            && o.PaymentMethod == PaymentMethod.Normal
                            && o.Status != OrderStatuses.Cancelled && o.Status != OrderStatuses.Refunded
                            && o.Total > 0)
                .OrderBy(o => o.ServiceDate)
                .ToListAsync();

            foreach (var o in orders)
            {
                var due = OrderBalance.AmountDue(o);
                if (due < 0.50m) continue;

                // Future recurring cleanings nobody has asked for yet are not "outstanding" — they
                // are upcoming, and the recurring page handles them. Only the head of each queue
                // or a cleaning that has already happened is shown here.
                if (o.RecurringSeriesId.HasValue && o.ServiceDate.Date > today
                    && orders.Any(x => x.RecurringSeriesId == o.RecurringSeriesId && x.ServiceDate < o.ServiceDate))
                    continue;

                var key = BillingPaymentAttempt.OrderObligationKey(o.Id);
                result.Add(new OutstandingObligationDto
                {
                    Type = "order",
                    Id = o.Id,
                    Reference = $"Order #{o.Id}",
                    Description = $"{o.GetDisplayServiceTypeName("Cleaning")} on {o.ServiceDate:MMM d, yyyy}",
                    AmountDue = due,
                    DueDate = o.ServiceDate,
                    PayUrl = $"/order/{o.Id}/pay",
                    PaymentInProgress = activeLocks.Contains(key),
                    AutoPayFailed = failedObligations.Contains(key)
                });
            }

            var clientIds = await _context.ContractClients.AsNoTracking()
                .Where(c => c.SourceUserId == userId && c.IsActive)
                .Select(c => c.Id)
                .ToListAsync();
            if (clientIds.Count > 0)
            {
                var invoices = await _context.CommercialInvoices.AsNoTracking()
                    .Where(i => clientIds.Contains(i.ContractClientId)
                                && (i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.Viewed
                                    || i.Status == InvoiceStatus.PartiallyPaid || i.Status == InvoiceStatus.Overdue)
                                && i.BalanceDue > 0)
                    .OrderBy(i => i.DueDate)
                    .ToListAsync();

                var settlingInvoiceIds = await _context.CommercialInvoicePaymentAttempts.AsNoTracking()
                    .Where(a => a.Status == InvoicePaymentAttemptStatus.Processing)
                    .Select(a => a.CommercialInvoiceId)
                    .ToListAsync();

                foreach (var i in invoices)
                {
                    var key = BillingPaymentAttempt.InvoiceObligationKey(i.Id);
                    result.Add(new OutstandingObligationDto
                    {
                        Type = "invoice",
                        Id = i.Id,
                        Reference = $"Invoice {i.InvoiceNumber}",
                        Description = $"Commercial invoice due {i.DueDate:MMM d, yyyy}",
                        AmountDue = i.BalanceDue,
                        DueDate = i.DueDate,
                        PayUrl = $"/invoice/{i.PublicToken}",
                        PaymentInProgress = activeLocks.Contains(key) || settlingInvoiceIds.Contains(i.Id),
                        AutoPayFailed = failedObligations.Contains(key)
                    });
                }
            }

            return result;
        }

        public async Task<List<AdminBillingAttemptDto>> GetRecentAttemptsAsync(int userId, int take)
        {
            var rows = await _context.BillingPaymentAttempts.AsNoTracking()
                .Where(a => a.UserId == userId)
                .OrderByDescending(a => a.Id)
                .Take(Math.Clamp(take, 1, 100))
                .ToListAsync();

            return rows.Select(a => new AdminBillingAttemptDto
            {
                Id = a.Id,
                CreatedAt = a.CreatedAt,
                Obligation = a.OrderId.HasValue ? $"Order #{a.OrderId}" : $"Invoice #{a.CommercialInvoiceId}",
                OrderId = a.OrderId,
                InvoiceId = a.CommercialInvoiceId,
                Trigger = a.Trigger switch
                {
                    BillingAttemptTrigger.AdminCharge => "Admin charge",
                    BillingAttemptTrigger.AutoPayRecurring => "AutoPay (recurring)",
                    BillingAttemptTrigger.AutoPayCommercial => "AutoPay (invoice)",
                    _ => "Customer"
                },
                CardRole = a.CardRole.ToString(),
                CardLabel = AttemptCardLabel(a),
                Amount = a.Amount,
                Status = a.Status.ToString(),
                FailureCode = a.DeclineCode ?? a.FailureCode,
                FailureMessage = a.FailureMessage
            }).ToList();
        }

        private static string AttemptCardLabel(BillingPaymentAttempt a)
        {
            var brand = string.IsNullOrWhiteSpace(a.CardBrand) ? "Card" : char.ToUpperInvariant(a.CardBrand[0]) + a.CardBrand[1..];
            return string.IsNullOrWhiteSpace(a.CardLast4) ? brand : $"{brand} •••• {a.CardLast4}";
        }

        private static string MethodLabel(InvoicePaymentRecordMethod method) => method switch
        {
            InvoicePaymentRecordMethod.AchBankTransfer => "Bank (ACH)",
            InvoicePaymentRecordMethod.WireTransfer => "Wire transfer",
            InvoicePaymentRecordMethod.Check => "Check",
            InvoicePaymentRecordMethod.Card => "Card",
            InvoicePaymentRecordMethod.Cash => "Cash",
            _ => "Other"
        };
    }
}

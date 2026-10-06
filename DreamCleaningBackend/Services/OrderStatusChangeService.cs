using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace DreamCleaningBackend.Services
{
    /// <summary>A status change as the admin panel asks for it.</summary>
    public sealed record OrderStatusChange(
        string NewStatus,
        string? PaymentMethod = null,
        string? PaymentReference = null,
        string? PaymentNotes = null);

    public enum DoneRequestOutcome
    {
        /// <summary>The order was moved to Done with every side effect of a direct Done.</summary>
        Applied,
        /// <summary>Somebody already finished the order; nothing was applied twice.</summary>
        AlreadyDone,
        /// <summary>The order was cancelled or refunded since the request; nothing was applied.</summary>
        OrderNoLongerEligible,
        /// <summary>The order is gone.</summary>
        OrderNotFound
    }

    public interface IOrderStatusChangeService
    {
        /// <summary>
        /// Applies a status change with every side effect the admin panel's status change has
        /// always had. Returns false when the order does not exist. Does NOT check whether the
        /// caller is allowed to make the move — see <see cref="OrderDoneApprovalPolicy"/>.
        /// </summary>
        /// <param name="paymentRecordedByUserId">Who recorded a manual payment chosen in the Done
        /// modal: the caller for a direct change, the REQUESTER for an approved request (they
        /// took the cash, the reviewer only agreed).</param>
        Task<bool> ApplyAsync(int orderId, OrderStatusChange change, int paymentRecordedByUserId);

        /// <summary>
        /// The side effects of a status TRANSITION (bubble points on entering / leaving Done, and
        /// the loyalty / special-offer re-consumption on reactivating a cancelled order). Called by
        /// <see cref="ApplyAsync"/> and by the order-editor paths, which change the status through
        /// SuperAdminFullUpdateOrder and used to skip all of these.
        /// </summary>
        Task RunTransitionHooksAsync(int orderId, string? previousStatus, string? newStatus);

        /// <summary>Emails/texts the customer the review request and audits what went out.</summary>
        Task<(bool EmailSent, bool SmsSent)> SendReviewRequestAsync(int orderId, int actorUserId);

        /// <summary>The order's waiting Done request, if any (at most one is allowed).</summary>
        Task<PendingOrderEdit?> FindPendingDoneRequestAsync(int orderId);

        /// <summary>
        /// Queues a Done request. Throws <see cref="InvalidOperationException"/> when one is already
        /// waiting on the order, or the order is not in a status a Done request can come from.
        /// </summary>
        Task<PendingOrderEdit> SubmitDoneRequestAsync(int orderId, DoneRequestPayload payload, int requesterUserId);

        /// <summary>Applies an approved Done request. The caller records the decision itself.</summary>
        Task<DoneRequestOutcome> ApplyDoneRequestAsync(PendingOrderEdit pending, int reviewerUserId);

        /// <summary>Reads a Done request's payload; an unreadable one reads as "nothing chosen".</summary>
        DoneRequestPayload ReadDonePayload(PendingOrderEdit pending);
    }

    /// <summary>
    /// SINGLE implementation of an admin status change (2026-10). It is what used to be the body of
    /// <c>PUT api/admin/orders/{id}/status</c>, moved here so that the panel's direct change, an
    /// approved Done request and the order editor's status field all produce IDENTICAL results —
    /// before this, an order set to Done through the editor (or an approved edit) never earned the
    /// customer their bubble points, because only the status endpoint ran the hook.
    /// </summary>
    public class OrderStatusChangeService : IOrderStatusChangeService
    {
        /// <summary>The statuses an admin may set through a status change. Refunded is set only by
        /// a refund; anything else is a typo that would fall out of every filter.</summary>
        public static readonly IReadOnlyList<string> SettableStatuses = new[]
        {
            OrderStatuses.Pending, OrderStatuses.Active, OrderStatuses.Done, OrderStatuses.Cancelled
        };

        private readonly ApplicationDbContext _context;
        private readonly IBubblePointsService _bubblePointsService;
        private readonly ILoyaltyDiscountService _loyaltyDiscountService;
        private readonly IAuditService _auditService;
        private readonly IEmailService _emailService;
        private readonly ISmsService _smsService;
        private readonly ILogger<OrderStatusChangeService> _logger;

        public OrderStatusChangeService(
            ApplicationDbContext context,
            IBubblePointsService bubblePointsService,
            ILoyaltyDiscountService loyaltyDiscountService,
            IAuditService auditService,
            IEmailService emailService,
            ISmsService smsService,
            ILogger<OrderStatusChangeService> logger)
        {
            _context = context;
            _bubblePointsService = bubblePointsService;
            _loyaltyDiscountService = loyaltyDiscountService;
            _auditService = auditService;
            _emailService = emailService;
            _smsService = smsService;
            _logger = logger;
        }

        /// <summary>The canonical spelling of a settable status, or null for anything else.</summary>
        public static string? NormalizeSettableStatus(string? status) =>
            SettableStatuses.FirstOrDefault(s => OrderStatuses.Is(status, s));

        public async Task<bool> ApplyAsync(int orderId, OrderStatusChange change, int paymentRecordedByUserId)
        {
            var order = await _context.Orders.FindAsync(orderId);
            if (order == null)
                return false;

            // CREATE A COPY FOR AUDITING with only relevant fields
            var originalOrder = AuditCopy(order);

            var previousStatus = order.Status;

            order.Status = change.NewStatus;
            order.UpdatedAt = DateTime.UtcNow;

            // When admin reactivates a cancelled order, exempt it from auto-cancellation
            if (OrderStatuses.IsCancelled(previousStatus) && !OrderStatuses.IsCancelled(change.NewStatus))
            {
                order.IsAutoCancelExempt = true;
            }

            // Payment method recording on Done transition. The Done modal sends the selected
            // payment method; if the admin changed it (e.g. order was created expecting Stripe but
            // customer paid Zelle), we persist the correction here. ManualPaymentRecordedAt /
            // RecordedBy are only stamped when the method's money has ALREADY arrived — Invoice is
            // handled outside Stripe but is not settled by being chosen. When no method is sent,
            // the order's existing values are preserved (no clobber to null).
            if (OrderStatuses.Is(change.NewStatus, OrderStatuses.Done) &&
                !string.IsNullOrWhiteSpace(change.PaymentMethod) &&
                Enum.TryParse<PaymentMethod>(change.PaymentMethod, ignoreCase: true, out var pm))
            {
                order.PaymentMethod = pm;
                order.PaymentReference = PaymentMethodRules.IsOutsideStripe(pm) ? change.PaymentReference : null;
                order.PaymentNotes = PaymentMethodRules.IsOutsideStripe(pm) ? change.PaymentNotes : null;
                if (PaymentMethodRules.IsSettledOnRecord(pm))
                {
                    order.ManualPaymentRecordedAt = DateTime.UtcNow;
                    order.ManualPaymentRecordedByUserId = paymentRecordedByUserId;
                }
            }

            // Save changes FIRST: every hook below reads the committed row.
            await _context.SaveChangesAsync();

            await RunTransitionHooksAsync(orderId, previousStatus, change.NewStatus);

            try
            {
                await _auditService.LogUpdateAsync(originalOrder, AuditCopy(order));
            }
            catch (Exception auditEx)
            {
                // Log the audit failure but don't fail the main operation
                _logger.LogError(auditEx, "Audit logging failed");
            }

            return true;
        }

        public async Task RunTransitionHooksAsync(int orderId, string? previousStatus, string? newStatus)
        {
            var enteringDone = OrderStatuses.Is(newStatus, OrderStatuses.Done) &&
                               !OrderStatuses.Is(previousStatus, OrderStatuses.Done);
            var leavingDone = OrderStatuses.Is(previousStatus, OrderStatuses.Done) &&
                              !OrderStatuses.Is(newStatus, OrderStatuses.Done);

            // Bubble Rewards: process order completion when status changes to Done
            if (enteringDone)
            {
                try { await _bubblePointsService.ProcessOrderCompletion(orderId); }
                catch (Exception rewardsEx) { _logger.LogError(rewardsEx, "[BubbleRewards] ProcessOrderCompletion failed for order {OrderId}", orderId); }
            }

            // Bubble Rewards: reverse points when order moves away from Done
            if (leavingDone)
            {
                try { await _bubblePointsService.ReverseOrderCompletion(orderId); }
                catch (Exception rewardsEx) { _logger.LogError(rewardsEx, "[BubbleRewards] ReverseOrderCompletion failed for order {OrderId}", orderId); }
            }

            // Handle special offer re-marking when reactivating from cancelled status
            if (OrderStatuses.IsCancelled(previousStatus) && OrderStatuses.Is(newStatus, OrderStatuses.Active))
            {
                var order = await _context.Orders.FindAsync(orderId);
                if (order == null) return;

                // Loyalty Discount: symmetric to the reverse-on-cancel hook in CancelOrder.
                // Re-consume the order's loyalty snapshot via ApplyToOrderAsync, which zeros the
                // user's current % and stamps LastUsedAt.
                //
                // Trade-off (flagged for review): if the user's loyalty journey advanced after the
                // cancellation (e.g. cron upgraded them to 15% during the gap), reactivation
                // consumes that current % too. Reactivation is rare and admin-driven, and the
                // audit log makes the consumption traceable.
                if (order.LoyaltyDiscountAmount > 0m && order.LoyaltyDiscountPercentage > 0m)
                {
                    try
                    {
                        await _loyaltyDiscountService.ApplyToOrderAsync(orderId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Loyalty discount re-apply failed for order {OrderId} on admin reactivation — order is Active but user state may be stale", orderId);
                    }
                }

                // Check if order had a discount amount (indicating a special offer was used)
                if (order.DiscountAmount > 0)
                {
                    // Find any special offer for this user that might have been the one used
                    var userSpecialOffers = await _context.UserSpecialOffers
                        .Where(uso => uso.UserId == order.UserId && !uso.IsUsed)
                        .Include(uso => uso.SpecialOffer)
                        .ToListAsync();

                    // Try to find a special offer that matches the discount amount
                    var matchingOffer = userSpecialOffers.FirstOrDefault(uso =>
                        (uso.SpecialOffer.IsPercentage &&
                         Math.Round(order.SubTotal * (uso.SpecialOffer.DiscountValue / 100), 2) == order.DiscountAmount) ||
                        (!uso.SpecialOffer.IsPercentage &&
                         uso.SpecialOffer.DiscountValue == order.DiscountAmount));

                    if (matchingOffer != null)
                    {
                        matchingOffer.IsUsed = true;
                        matchingOffer.UsedAt = DateTime.UtcNow;
                        matchingOffer.UsedOnOrderId = orderId;
                        await _context.SaveChangesAsync();

                        _logger.LogInformation("Re-marked special offer {SpecialOfferId} as used for user {UserId} after reactivating order {OrderId}",
                            matchingOffer.SpecialOfferId, matchingOffer.UserId, orderId);
                    }
                }
            }
        }

        public async Task<(bool EmailSent, bool SmsSent)> SendReviewRequestAsync(int orderId, int actorUserId)
        {
            var order = await _context.Orders
                .Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == orderId)
                ?? throw new KeyNotFoundException($"Order {orderId} not found");

            var customerName = $"{order.ContactFirstName} {order.ContactLastName}".Trim();
            var email = order.ContactEmail;
            var phone = order.User?.Phone ?? order.ContactPhone;

            // Tracked, not assumed: both sends swallow their own exceptions, so the audit row has
            // to reflect what actually left rather than what was attempted.
            var emailSent = false;
            var smsSent = false;

            if (!string.IsNullOrWhiteSpace(email))
            {
                try
                {
                    await _emailService.SendReviewRequestEmailAsync(email, customerName);
                    emailSent = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send review email for order #{OrderId}", orderId);
                }
            }

            if (!string.IsNullOrWhiteSpace(phone) && _smsService.IsSmsEnabled())
            {
                try
                {
                    await _smsService.SendReviewRequestSmsAsync(phone, customerName);
                    smsSent = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send review SMS for order #{OrderId}", orderId);
                }
            }

            if (emailSent || smsSent)
            {
                var channels = emailSent && smsSent ? "email and SMS" : emailSent ? "email" : "SMS";
                try
                {
                    await _auditService.LogOrderNotificationAsync(
                        orderId, "ReviewRequestSent", $"Review request sent by {channels}", actorUserId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not audit notification ReviewRequestSent on order {OrderId}", orderId);
                }
            }

            return (emailSent, smsSent);
        }

        public Task<PendingOrderEdit?> FindPendingDoneRequestAsync(int orderId) =>
            _context.PendingOrderEdits
                .Where(p => p.OrderId == orderId && p.Status == "Pending" && p.Kind == PendingOrderEditKinds.Done)
                .OrderByDescending(p => p.RequestedAt)
                .FirstOrDefaultAsync();

        public async Task<PendingOrderEdit> SubmitDoneRequestAsync(int orderId, DoneRequestPayload payload, int requesterUserId)
        {
            var order = await _context.Orders.FindAsync(orderId)
                ?? throw new KeyNotFoundException($"Order {orderId} not found");

            if (OrderStatuses.Is(order.Status, OrderStatuses.Done))
                throw new InvalidOperationException($"Order #{orderId} is already Done.");

            if (await FindPendingDoneRequestAsync(orderId) != null)
                throw new InvalidOperationException(
                    $"A Done request for order #{orderId} is already waiting for approval.");

            var reason = string.IsNullOrWhiteSpace(payload.Reason) ? null : payload.Reason.Trim();
            if (reason != null && reason.Length > 1000) reason = reason[..1000];
            payload.Reason = null; // stored on the row's own Reason column, not twice

            var pending = new PendingOrderEdit
            {
                OrderId = orderId,
                Kind = PendingOrderEditKinds.Done,
                RequestedByUserId = requesterUserId,
                RequestedAt = DateTime.UtcNow,
                ProposedChangesJson = JsonConvert.SerializeObject(payload),
                FieldChangesJson = JsonConvert.SerializeObject(DescribeDoneRequest(order.Status, payload)),
                Reason = reason,
                Status = "Pending"
            };
            _context.PendingOrderEdits.Add(pending);
            await _context.SaveChangesAsync();
            return pending;
        }

        public async Task<DoneRequestOutcome> ApplyDoneRequestAsync(PendingOrderEdit pending, int reviewerUserId)
        {
            var order = await _context.Orders.AsNoTracking()
                .Where(o => o.Id == pending.OrderId)
                .Select(o => new { o.Status })
                .FirstOrDefaultAsync();
            if (order == null) return DoneRequestOutcome.OrderNotFound;
            if (OrderStatuses.Is(order.Status, OrderStatuses.Done)) return DoneRequestOutcome.AlreadyDone;
            if (OrderStatuses.IsCancelled(order.Status) || OrderStatuses.IsRefunded(order.Status))
                return DoneRequestOutcome.OrderNoLongerEligible;

            var payload = ReadDonePayload(pending);
            await ApplyAsync(pending.OrderId,
                new OrderStatusChange(OrderStatuses.Done, payload.PaymentMethod, payload.PaymentReference, payload.PaymentNotes),
                paymentRecordedByUserId: pending.RequestedByUserId);

            // The requester's "Yes, send review link" from the Done modal, honoured now that the
            // order really is Done — sending it at request time would thank the customer for a
            // job the business has not yet signed off.
            if (payload.SendReviewRequest)
            {
                try { await SendReviewRequestAsync(pending.OrderId, reviewerUserId); }
                catch (Exception ex) { _logger.LogError(ex, "Review request after approved Done request failed for order {OrderId}", pending.OrderId); }
            }

            return DoneRequestOutcome.Applied;
        }

        public DoneRequestPayload ReadDonePayload(PendingOrderEdit pending)
        {
            try
            {
                return JsonConvert.DeserializeObject<DoneRequestPayload>(pending.ProposedChangesJson) ?? new DoneRequestPayload();
            }
            catch
            {
                return new DoneRequestPayload();
            }
        }

        /// <summary>
        /// The readable half of a Done request, in the same field-change shape the edit requests
        /// store, so the review modal, the audit row and the requester's own view all read it the
        /// same way.
        /// </summary>
        public static List<PendingOrderEditFieldChangeDto> DescribeDoneRequest(string? currentStatus, DoneRequestPayload payload)
        {
            var rows = new List<PendingOrderEditFieldChangeDto>
            {
                new() { Field = "Status", Current = currentStatus ?? "", Proposed = OrderStatuses.Done, Emphasised = true }
            };
            if (!string.IsNullOrWhiteSpace(payload.PaymentMethod))
                rows.Add(new() { Field = "Payment method", Current = "", Proposed = PaymentMethodLabel(payload.PaymentMethod) });
            if (!string.IsNullOrWhiteSpace(payload.PaymentReference))
                rows.Add(new() { Field = "Payment reference", Current = "", Proposed = payload.PaymentReference });
            if (!string.IsNullOrWhiteSpace(payload.PaymentNotes))
                rows.Add(new() { Field = "Payment notes", Current = "", Proposed = payload.PaymentNotes });
            rows.Add(new() { Field = "Send review link", Current = "", Proposed = payload.SendReviewRequest ? "Yes" : "No" });
            return rows;
        }

        // The Done modal's own wording for each method ("Normal" is the online card payment).
        private static string PaymentMethodLabel(string method) =>
            Enum.TryParse<PaymentMethod>(method, ignoreCase: true, out var pm)
                ? pm switch
                {
                    PaymentMethod.Normal => "Card (online)",
                    PaymentMethod.Invoice => "Commercial invoice",
                    _ => pm.ToString()
                }
                : method;

        private static Order AuditCopy(Order order) => new()
        {
            Id = order.Id,
            Status = order.Status,
            UserId = order.UserId,
            Total = order.Total,
            ServiceDate = order.ServiceDate,
            ServiceTime = order.ServiceTime,
            OrderDate = order.OrderDate,
            ContactFirstName = order.ContactFirstName,
            ContactLastName = order.ContactLastName,
            ContactEmail = order.ContactEmail,
            UpdatedAt = order.UpdatedAt
        };
    }
}

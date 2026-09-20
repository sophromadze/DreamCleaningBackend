using System.Net;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Billing;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Billing
{
    /// <summary>What a charge run ended as, from the customer's point of view.</summary>
    public class ChargeNotice
    {
        public int UserId { get; set; }
        public BillingNotificationType Type { get; set; }
        public BillingObligationType ObligationType { get; set; }
        public int? OrderId { get; set; }
        public int? InvoiceId { get; set; }
        public string ObligationKey { get; set; } = string.Empty;

        /// <summary>The run, so the Primary and Backup outcomes of one run notify once.</summary>
        public string RunKey { get; set; } = string.Empty;
        public int? PaymentAttemptId { get; set; }
        public decimal Amount { get; set; }
        public string? PrimaryCardLabel { get; set; }
        public string? ChargedCardLabel { get; set; }
    }

    public interface IBillingNotificationService
    {
        Task NotifyChargeOutcomeAsync(ChargeNotice notice);
        Task NotifyCardRemovedAsync(int userId, int cardId, string cardLabel, string? promotedLabel, bool autoPayTurnedOff);
        Task<int> DeliverDueAsync(CancellationToken ct);
        Task TryDeliverAsync(int notificationId, CancellationToken ct = default);
        Task<List<BillingNotificationDto>> ListForUserAsync(int userId, int take = 30);
        Task MarkReadAsync(int userId, int notificationId);
        Task ResolveForObligationAsync(string obligationKey);
    }

    /// <summary>
    /// Billing notices: persistent in-app messages plus a small DURABLE outbox for their email and
    /// SMS copies.
    ///
    /// ══ WHY AN OUTBOX ══
    /// The row is written first; delivery happens afterwards, from the row, with retries. A mail
    /// server being down therefore delays a message and never loses it, and — the rule this
    /// codebase learned the hard way — a failed email or SMS can never reach back and change a
    /// payment's outcome. Nothing here throws into a payment path.
    ///
    /// ══ NO DUPLICATES, NO STALE "PAY NOW" ══
    /// Every notice carries a UNIQUE DedupeKey derived from the run it describes, so a duplicate
    /// webhook or a re-run sweep cannot send it twice. And before a "your payment failed" message
    /// is sent — and whenever notices are read — the obligation is re-checked: if it has been paid
    /// by any route since, the notice is marked resolved and its email/SMS are skipped. A stale
    /// failure message is exactly how a customer gets invited to pay twice.
    /// </summary>
    public class BillingNotificationService : IBillingNotificationService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _email;
        private readonly ISmsService _sms;
        private readonly IConfiguration _configuration;
        private readonly ILogger<BillingNotificationService> _logger;

        private const int MaxDeliveryAttempts = 4;

        public BillingNotificationService(
            ApplicationDbContext context,
            IEmailService email,
            ISmsService sms,
            IConfiguration configuration,
            ILogger<BillingNotificationService> logger)
        {
            _context = context;
            _email = email;
            _sms = sms;
            _configuration = configuration;
            _logger = logger;
        }

        private string FrontendUrl => (_configuration["Frontend:Url"] ?? "https://dreamcleaningnyc.com").TrimEnd('/');

        // ── Creating ──────────────────────────────────────────────────────────────────────────

        public async Task NotifyChargeOutcomeAsync(ChargeNotice notice)
        {
            try
            {
                var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == notice.UserId);
                if (user == null) return;

                var (reference, payUrl, emailPayLink) = await ResolveObligationLinksAsync(notice);
                var amount = notice.Amount.ToString("C");
                var name = FirstName(user);

                string title, message, emailSubject, smsBody;
                string? actionUrl = null, actionLabel = null;
                var severity = BillingNotificationSeverity.Info;
                var sendSms = true;

                switch (notice.Type)
                {
                    case BillingNotificationType.AutoPaySucceeded:
                        title = $"Automatic payment received — {reference}";
                        message = $"We charged {amount} to your {notice.ChargedCardLabel ?? "card"} for {reference}. No action is needed.";
                        emailSubject = $"Payment received for {reference}";
                        smsBody = $"Dream Cleaning: we received your automatic payment of {amount} for {reference} ({notice.ChargedCardLabel}). Thank you!";
                        // A receipt, not an alert — email only; Stripe also sends its own receipt.
                        sendSms = false;
                        break;

                    case BillingNotificationType.PrimaryFailedBackupSucceeded:
                        severity = BillingNotificationSeverity.Warning;
                        title = "Your Primary card could not be charged";
                        message = $"We couldn't charge your Primary card ({notice.PrimaryCardLabel}) for {reference}, so we charged your Backup card " +
                                  $"({notice.ChargedCardLabel}) {amount} instead. This payment is complete and nothing more is owed for it. " +
                                  "Please check or update your Primary card in Billing so future payments go through.";
                        emailSubject = "Action suggested: your Primary card was declined";
                        smsBody = $"Dream Cleaning: your Primary card ({notice.PrimaryCardLabel}) was declined for {reference}. Your Backup card paid {amount} — nothing more is owed. Please update your Primary card: {FrontendUrl}/profile?tab=billing";
                        actionUrl = "/profile?tab=billing";
                        actionLabel = "Update cards";
                        break;

                    case BillingNotificationType.AuthenticationRequired:
                        severity = BillingNotificationSeverity.Critical;
                        title = $"Your bank needs you to confirm a payment — {reference}";
                        message = $"Your bank asked for extra verification before charging {amount} for {reference}, which can only be done by you. " +
                                  "Nothing was charged. Please sign in and pay — you'll be able to complete the verification with your bank.";
                        emailSubject = $"Please confirm your payment for {reference}";
                        smsBody = $"Dream Cleaning: your bank needs you to confirm the {amount} payment for {reference}. Nothing was charged. Please sign in to pay: {emailPayLink}";
                        actionUrl = payUrl;
                        actionLabel = "Pay now";
                        break;

                    default: // AutoPayFailed
                        severity = BillingNotificationSeverity.Critical;
                        title = $"Payment failed — {reference}";
                        message = $"We couldn't charge {amount} for {reference}"
                                  + (notice.PrimaryCardLabel != null && notice.ChargedCardLabel != null && notice.PrimaryCardLabel != notice.ChargedCardLabel
                                      ? $" to your Primary card ({notice.PrimaryCardLabel}) or your Backup card ({notice.ChargedCardLabel})."
                                      : notice.PrimaryCardLabel != null ? $" to your {notice.PrimaryCardLabel}." : ".")
                                  + " The amount is still owed. Please sign in to Dream Cleaning NYC to update your card or pay now.";
                        emailSubject = $"Payment failed for {reference} — action needed";
                        smsBody = $"Dream Cleaning: we couldn't charge {amount} for {reference}. Please sign in to update your card or pay: {emailPayLink}";
                        actionUrl = payUrl;
                        actionLabel = "Pay now";
                        break;
                }

                var html = BuildEmailHtml(name, title, message,
                    actionLabel != null ? (emailPayLink ?? $"{FrontendUrl}{actionUrl}") : null,
                    actionLabel);

                var row = NewNotification(user, notice.Type, severity, title, message, actionUrl, actionLabel,
                    dedupeKey: $"charge:{notice.RunKey}:{notice.Type}",
                    emailSubject, html, sendSms ? smsBody : null);
                row.ObligationKey = notice.ObligationKey;
                row.OrderId = notice.OrderId;
                row.CommercialInvoiceId = notice.InvoiceId;
                row.PaymentAttemptId = notice.PaymentAttemptId;

                await InsertAsync(row);
            }
            catch (Exception ex)
            {
                // Never into the payment path.
                _logger.LogError(ex, "Could not record billing notice {Type} for user {UserId}.", notice.Type, notice.UserId);
            }
        }

        public async Task NotifyCardRemovedAsync(int userId, int cardId, string cardLabel, string? promotedLabel, bool autoPayTurnedOff)
        {
            try
            {
                var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null) return;

                var title = $"{cardLabel} was removed";
                var message = $"{cardLabel} is no longer saved to your account — it was removed at our payment processor."
                              + (promotedLabel != null ? $" {promotedLabel} is now your Primary card." : "")
                              + (autoPayTurnedOff ? " You have no saved cards left, so Automatic Payments were turned off." : "");

                var row = NewNotification(user,
                    autoPayTurnedOff ? BillingNotificationType.AutoPayPaused : BillingNotificationType.CardRemoved,
                    BillingNotificationSeverity.Warning, title, message, "/profile?tab=billing", "Review cards",
                    dedupeKey: $"card-removed:{cardId}",
                    $"{cardLabel} was removed from your account",
                    BuildEmailHtml(FirstName(user), title, message, $"{FrontendUrl}/profile?tab=billing", "Review your cards"),
                    smsBody: null);

                await InsertAsync(row);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not record card-removed notice for user {UserId}.", userId);
            }
        }

        private BillingNotification NewNotification(User user, BillingNotificationType type, BillingNotificationSeverity severity,
            string title, string message, string? actionUrl, string? actionLabel, string dedupeKey,
            string emailSubject, string emailHtml, string? smsBody)
        {
            var email = NoEmailHelper.ResolveRealEmail(user);
            var emailOk = user.IsActive && user.CanReceiveEmails && !string.IsNullOrWhiteSpace(email)
                          && !email!.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase);
            var smsOk = smsBody != null && user.IsActive && user.CanReceiveMessages && !string.IsNullOrWhiteSpace(user.Phone);

            return new BillingNotification
            {
                UserId = user.Id,
                Type = type,
                Severity = severity,
                Title = Truncate(title, 200)!,
                Message = Truncate(message, 2000)!,
                ActionUrl = actionUrl,
                ActionLabel = actionLabel,
                DedupeKey = Truncate(dedupeKey, 120)!,
                ShowInApp = true,
                EmailStatus = emailOk ? BillingDeliveryStatus.Pending : BillingDeliveryStatus.Skipped,
                EmailTo = emailOk ? email : null,
                EmailSubject = Truncate(emailSubject, 200),
                EmailHtml = emailOk ? emailHtml : null,
                SmsStatus = smsBody == null ? BillingDeliveryStatus.NotRequired
                    : smsOk ? BillingDeliveryStatus.Pending : BillingDeliveryStatus.Skipped,
                SmsTo = smsOk ? Truncate(user.Phone, 20) : null,
                SmsBody = smsOk ? Truncate(smsBody, 640) : null,
                NextDeliveryAttemptAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        private async Task InsertAsync(BillingNotification row)
        {
            // Cheap pre-check; the unique index is the real guard.
            if (await _context.BillingNotifications.AnyAsync(n => n.DedupeKey == row.DedupeKey)) return;

            _context.BillingNotifications.Add(row);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _context.Entry(row).State = EntityState.Detached;
                _logger.LogInformation("Billing notice {DedupeKey} already exists; not sending twice.", row.DedupeKey);
            }
        }

        // ── Delivering ────────────────────────────────────────────────────────────────────────

        public async Task<int> DeliverDueAsync(CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var ids = await _context.BillingNotifications.AsNoTracking()
                .Where(n => (n.EmailStatus == BillingDeliveryStatus.Pending || n.SmsStatus == BillingDeliveryStatus.Pending)
                            && (n.NextDeliveryAttemptAt == null || n.NextDeliveryAttemptAt <= now))
                .OrderBy(n => n.Id)
                .Select(n => n.Id)
                .Take(50)
                .ToListAsync(ct);

            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                await TryDeliverAsync(id, ct);
            }

            return ids.Count;
        }

        public async Task TryDeliverAsync(int notificationId, CancellationToken ct = default)
        {
            try
            {
                // Lease the row so the worker and an immediate send cannot deliver it together.
                if (_context.Database.IsRelational())
                {
                    var now = DateTime.UtcNow;
                    var lease = now.AddMinutes(10);
                    var claimed = await _context.BillingNotifications
                        .Where(n => n.Id == notificationId
                                    && (n.EmailStatus == BillingDeliveryStatus.Pending || n.SmsStatus == BillingDeliveryStatus.Pending)
                                    && (n.NextDeliveryAttemptAt == null || n.NextDeliveryAttemptAt <= now))
                        .ExecuteUpdateAsync(s => s.SetProperty(n => n.NextDeliveryAttemptAt, lease), ct);
                    if (claimed == 0) return;
                }

                var row = await _context.BillingNotifications.FirstOrDefaultAsync(n => n.Id == notificationId, ct);
                if (row == null) return;

                // A failure notice about an obligation that has since been paid is NOT sent.
                if (IsPaymentRequest(row.Type) && await IsObligationSettledAsync(row.ObligationKey))
                {
                    if (row.EmailStatus == BillingDeliveryStatus.Pending) row.EmailStatus = BillingDeliveryStatus.Skipped;
                    if (row.SmsStatus == BillingDeliveryStatus.Pending) row.SmsStatus = BillingDeliveryStatus.Skipped;
                    row.ResolvedAt ??= DateTime.UtcNow;
                    row.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync(ct);
                    return;
                }

                if (row.EmailStatus == BillingDeliveryStatus.Pending)
                {
                    try
                    {
                        await _email.SendEmailAsync(row.EmailTo!, row.EmailSubject ?? row.Title, row.EmailHtml ?? row.Message);
                        row.EmailStatus = BillingDeliveryStatus.Sent;
                    }
                    catch (Exception ex)
                    {
                        row.EmailAttempts++;
                        row.EmailLastError = Truncate(ex.Message, 300);
                        if (row.EmailAttempts >= MaxDeliveryAttempts) row.EmailStatus = BillingDeliveryStatus.Failed;
                        _logger.LogWarning(ex, "Billing notice {Id}: email attempt {Attempt} failed.", row.Id, row.EmailAttempts);
                    }
                    row.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync(ct); // record the send before trying SMS
                }

                if (row.SmsStatus == BillingDeliveryStatus.Pending)
                {
                    try
                    {
                        await _sms.SendSmsAsync(row.SmsTo!, row.SmsBody!);
                        row.SmsStatus = BillingDeliveryStatus.Sent;
                    }
                    catch (InvalidPhoneNumberException)
                    {
                        // A bad number is not transient — retrying would only fail again.
                        row.SmsStatus = BillingDeliveryStatus.Skipped;
                        row.SmsLastError = "invalid phone number";
                    }
                    catch (Exception ex)
                    {
                        row.SmsAttempts++;
                        row.SmsLastError = Truncate(ex.Message, 300);
                        if (row.SmsAttempts >= MaxDeliveryAttempts) row.SmsStatus = BillingDeliveryStatus.Failed;
                        _logger.LogWarning(ex, "Billing notice {Id}: SMS attempt {Attempt} failed.", row.Id, row.SmsAttempts);
                    }
                }

                var attempts = Math.Max(row.EmailAttempts, row.SmsAttempts);
                row.NextDeliveryAttemptAt = row.EmailStatus == BillingDeliveryStatus.Pending || row.SmsStatus == BillingDeliveryStatus.Pending
                    ? DateTime.UtcNow.Add(Backoff(attempts))
                    : null;
                row.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delivering billing notice {Id} failed; it will be retried.", notificationId);
            }
        }

        private static TimeSpan Backoff(int attempts) => attempts switch
        {
            <= 1 => TimeSpan.FromMinutes(5),
            2 => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromHours(2)
        };

        // ── Reading ───────────────────────────────────────────────────────────────────────────

        public async Task<List<BillingNotificationDto>> ListForUserAsync(int userId, int take = 30)
        {
            var rows = await _context.BillingNotifications
                .Where(n => n.UserId == userId && n.ShowInApp)
                .OrderByDescending(n => n.CreatedAt)
                .Take(take)
                .ToListAsync();

            // Resolve on read, so a notice paid through ANY path (payment link, admin, cash)
            // stops asking for money the moment it is looked at.
            var changed = false;
            foreach (var row in rows.Where(r => r.ResolvedAt == null && IsPaymentRequest(r.Type)))
            {
                if (await IsObligationSettledAsync(row.ObligationKey))
                {
                    row.ResolvedAt = DateTime.UtcNow;
                    row.UpdatedAt = DateTime.UtcNow;
                    changed = true;
                }
            }
            if (changed) await _context.SaveChangesAsync();

            return rows.Select(ToDto).ToList();
        }

        public static BillingNotificationDto ToDto(BillingNotification n) => new()
        {
            Id = n.Id,
            Type = n.Type.ToString(),
            Severity = n.Severity.ToString().ToLowerInvariant(),
            Title = n.ResolvedAt != null && IsPaymentRequest(n.Type) ? $"Resolved — {n.Title}" : n.Title,
            Message = n.ResolvedAt != null && IsPaymentRequest(n.Type)
                ? n.Message + " This has since been paid — no action is needed."
                : n.Message,
            // A resolved notice never offers "Pay now" again.
            ActionUrl = n.ResolvedAt != null && IsPaymentRequest(n.Type) ? null : n.ActionUrl,
            ActionLabel = n.ResolvedAt != null && IsPaymentRequest(n.Type) ? null : n.ActionLabel,
            CreatedAt = n.CreatedAt,
            IsRead = n.ReadAt != null,
            IsResolved = n.ResolvedAt != null
        };

        public async Task MarkReadAsync(int userId, int notificationId)
        {
            var row = await _context.BillingNotifications
                .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId);
            if (row == null || row.ReadAt != null) return;
            row.ReadAt = DateTime.UtcNow;
            row.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        public async Task ResolveForObligationAsync(string obligationKey)
        {
            try
            {
                var rows = await _context.BillingNotifications
                    .Where(n => n.ObligationKey == obligationKey && n.ResolvedAt == null)
                    .ToListAsync();
                foreach (var row in rows.Where(r => IsPaymentRequest(r.Type)))
                {
                    row.ResolvedAt = DateTime.UtcNow;
                    row.UpdatedAt = DateTime.UtcNow;
                }
                if (rows.Count > 0) await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve billing notices for {ObligationKey}.", obligationKey);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        /// <summary>Notices whose point is "you still owe this" — the ones that go stale.</summary>
        public static bool IsPaymentRequest(BillingNotificationType type) =>
            type is BillingNotificationType.AutoPayFailed or BillingNotificationType.AuthenticationRequired;

        private async Task<bool> IsObligationSettledAsync(string? obligationKey)
        {
            if (string.IsNullOrEmpty(obligationKey)) return false;

            if (obligationKey.StartsWith("order:") && int.TryParse(obligationKey[6..], out var orderId))
            {
                var order = await _context.Orders.AsNoTracking()
                    .Where(o => o.Id == orderId)
                    .Select(o => new { o.IsPaid, o.InvoicePaidAt, o.PaymentMethod, o.Status })
                    .FirstOrDefaultAsync();
                return order == null || order.IsPaid || order.InvoicePaidAt != null
                       || order.PaymentMethod != PaymentMethod.Normal
                       || OrderStatuses.IsCancelled(order.Status) || OrderStatuses.IsRefunded(order.Status);
            }

            if (obligationKey.StartsWith("invoice:") && int.TryParse(obligationKey[8..], out var invoiceId))
            {
                var invoice = await _context.CommercialInvoices.AsNoTracking()
                    .Where(i => i.Id == invoiceId)
                    .Select(i => new { i.Status, i.BalanceDue })
                    .FirstOrDefaultAsync();
                return invoice == null || invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Void || invoice.BalanceDue <= 0m;
            }

            return false;
        }

        private async Task<(string Reference, string PayUrl, string? EmailPayLink)> ResolveObligationLinksAsync(ChargeNotice notice)
        {
            if (notice.ObligationType == BillingObligationType.Order && notice.OrderId.HasValue)
            {
                var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == notice.OrderId.Value);
                var reference = order != null
                    ? $"your cleaning on {order.ServiceDate:MMM d, yyyy} (order #{order.Id})"
                    : $"order #{notice.OrderId}";

                string? link = null;
                if (order != null)
                {
                    try { link = await PaymentLinkHelper.BuildPaymentLinkAsync(_context, order, FrontendUrl); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Could not build a payment link for order {OrderId}.", order.Id); }
                }

                return (reference, $"/order/{notice.OrderId}/pay", link ?? $"{FrontendUrl}/order/{notice.OrderId}/pay");
            }

            if (notice.InvoiceId.HasValue)
            {
                var invoice = await _context.CommercialInvoices.AsNoTracking()
                    .Where(i => i.Id == notice.InvoiceId.Value)
                    .Select(i => new { i.InvoiceNumber, i.PublicToken })
                    .FirstOrDefaultAsync();
                var reference = invoice != null ? $"invoice {invoice.InvoiceNumber}" : $"invoice #{notice.InvoiceId}";
                return (reference, "/profile?tab=billing",
                    invoice != null ? $"{FrontendUrl}/invoice/{invoice.PublicToken}" : $"{FrontendUrl}/profile?tab=billing");
            }

            return ("your payment", "/profile?tab=billing", $"{FrontendUrl}/profile?tab=billing");
        }

        private static string FirstName(User user)
        {
            var first = user.FirstName?.Trim();
            return string.IsNullOrEmpty(first) ? "there" : char.ToUpperInvariant(first[0]) + first[1..];
        }

        /// <summary>Plain, self-contained HTML in the same visual family as the existing payment mails.
        /// Every dynamic value is HTML-encoded.</summary>
        private static string BuildEmailHtml(string name, string title, string message, string? link, string? linkLabel)
        {
            var e = (string s) => WebUtility.HtmlEncode(s);
            var button = link == null || linkLabel == null ? "" : $@"
                <p style='text-align:center;margin:28px 0;'>
                  <a href='{e(link)}' style='background-color:#2563eb;color:#ffffff;padding:14px 28px;text-decoration:none;border-radius:6px;font-weight:bold;display:inline-block;'>{e(linkLabel)}</a>
                </p>";

            return $@"<html><body style='font-family:Arial,sans-serif;line-height:1.6;color:#1e293b;background:#f8fafc;margin:0;padding:0;'>
  <div style='max-width:600px;margin:0 auto;padding:24px;'>
    <div style='background:#ffffff;border-radius:8px;padding:28px;border:1px solid #e2e8f0;'>
      <h2 style='margin-top:0;color:#0f172a;'>{e(title)}</h2>
      <p>Hi {e(name)},</p>
      <p>{e(message)}</p>
      {button}
      <p style='font-size:13px;color:#64748b;'>You can manage your saved cards and automatic payments any time from the Billing tab of your Dream Cleaning NYC profile. We never ask for your card number by email or text.</p>
    </div>
    <p style='text-align:center;font-size:12px;color:#94a3b8;margin-top:16px;'>Dream Cleaning NYC · hello@dreamcleaningnyc.com · (929) 930-1525</p>
  </div>
</body></html>";
        }

        private static bool IsUniqueViolation(DbUpdateException ex) =>
            ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;

        private static string? Truncate(string? value, int max) =>
            string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
    }
}

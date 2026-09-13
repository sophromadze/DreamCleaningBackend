using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Recurring;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Keeps every active recurring series populated through the rolling 30-day horizon, and — only
    /// where an admin has switched it on — sends the automatic payment request for an occurrence.
    ///
    /// ONE WORKER, NOT TWO. The shape is the codebase's existing background-service pattern
    /// (<c>InvoiceOverdueService</c>, <c>ContractRetentionService</c>, <c>LoyaltyReengagementService</c>):
    /// an hourly loop that does its real work once per calendar day, so a restart at any hour
    /// cannot skip a day and a long-running process cannot repeat one. No new scheduler, no cron,
    /// no second timer for the payment half — the two passes are the same daily tick because they
    /// are the same question asked a day apart.
    ///
    /// <b>Generation is bookkeeping and notifies nobody.</b> No cleaner mail, no cleaner SMS, no
    /// customer booking confirmation. The one message this service can emit is the payment request
    /// below, and it is off unless a series says otherwise.
    /// </summary>
    public class RecurringOrderGenerationService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<RecurringOrderGenerationService> _logger;
        private readonly IConfiguration _configuration;

        private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);
        private DateTime? _lastSweepDate;

        public RecurringOrderGenerationService(
            IServiceProvider services,
            ILogger<RecurringOrderGenerationService> logger,
            IConfiguration configuration)
        {
            _services = services;
            _logger = logger;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // A short settle before the first pass so startup migrations and seeding finish first.
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ContinueWith(_ => { }, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // NY, not UTC: a service date is a wall-clock date, and reading UTC after 8pm
                    // New York would roll the horizon forward every evening.
                    var today = NyTimeHelper.NowNy.Date;
                    if (_lastSweepDate != today)
                    {
                        await SweepAsync(stoppingToken);
                        _lastSweepDate = today;
                    }
                }
                catch (Exception ex)
                {
                    // Never let one bad pass kill the worker — it would stop every later day too.
                    _logger.LogError(ex, "Recurring order sweep failed.");
                }

                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// One pass: fill the horizon, then send whatever automatic payment requests are due.
        /// Public so an admin endpoint can run it on demand, which is also how it is exercised
        /// without waiting a day.
        /// </summary>
        public async Task<int> SweepAsync(CancellationToken ct = default)
        {
            using var scope = _services.CreateScope();
            var series = scope.ServiceProvider.GetRequiredService<IRecurringOrderSeriesService>();

            var created = await series.GenerateAllAsync(ct);
            if (created > 0)
                _logger.LogInformation("Recurring generation created {Count} order(s).", created);

            try
            {
                await SendDuePaymentRequestsAsync(scope.ServiceProvider, ct);
            }
            catch (Exception ex)
            {
                // A failed reminder must never roll back a successful generation pass.
                _logger.LogError(ex, "Recurring payment-request pass failed.");
            }

            return created;
        }

        /// <summary>
        /// The automatic payment request, governed by <see cref="RecurringPaymentPolicy"/>.
        ///
        /// THREE GATES, ALL OF THEM DELIBERATE:
        ///
        ///  1. <b>The series must have opted in</b> (<c>AutoRequestPayment</c>, default false). A
        ///     future recurring cleaning being VISIBLE and voluntarily payable is a customer
        ///     choice; the business asking for the money is a different act, and it is off until
        ///     somebody turns it on.
        ///  2. <b>At least 24 hours must have passed since the PREVIOUS cleaning in the series.</b>
        ///     Asking for next fortnight's money on the evening of today's clean is the fastest way
        ///     to make a standing arrangement feel like a trap.
        ///  3. <b>At most once per occurrence</b>, enforced by a NotificationLog row. It never
        ///     suppresses an admin pressing Send payment link — the rule restrains the machine.
        ///
        /// A paid occurrence is never chased, and an occurrence that is not the customer's NEXT
        /// unpaid one is never chased either: the queue is sequential, so asking for the third
        /// visit while the first is outstanding would be asking for the wrong money.
        /// </summary>
        private async Task SendDuePaymentRequestsAsync(IServiceProvider provider, CancellationToken ct)
        {
            var context = provider.GetRequiredService<ApplicationDbContext>();
            var email = provider.GetRequiredService<IEmailService>();
            var sms = provider.GetRequiredService<ISmsService>();

            var frontendUrl = _configuration["Frontend:Url"] ?? "https://dreamcleaningnyc.com";

            // NEW YORK, not UTC. ServiceDate + ServiceTime is a wall-clock moment in NY, so the
            // "24 hours after the previous cleaning" comparison has to happen in the same frame —
            // measuring a NY timestamp against DateTime.UtcNow would fire the request four or five
            // hours early, which is precisely the behaviour the rule exists to prevent.
            var nowNy = NyTimeHelper.NowNy;

            var seriesIds = await context.RecurringOrderSeries
                .Where(s => s.IsActive && s.StoppedAt == null && s.AutoRequestPayment)
                .Select(s => s.Id)
                .ToListAsync(ct);

            foreach (var seriesId in seriesIds)
            {
                ct.ThrowIfCancellationRequested();

                var orders = await context.Orders
                    .Include(o => o.User)
                    .Where(o => o.RecurringSeriesId == seriesId)
                    .OrderBy(o => o.ServiceDate)
                    .ToListAsync(ct);

                if (orders.Count == 0) continue;

                var occurrences = orders.Select(o => new RecurringPayableOccurrence
                {
                    IsOnlinePayable = o.PaymentMethod == PaymentMethod.Normal,
                    OrderId = o.Id,
                    ServiceDateTime = o.ServiceDate.Date.Add(o.ServiceTime),
                    IsPaid = o.IsPaid || o.InvoicePaidAt != null,
                    AmountDue = o.Total,
                    IsCancelled = OrderStatuses.IsCancelled(o.Status) || OrderStatuses.IsRefunded(o.Status)
                }).ToList();

                // Only the head of the queue is ever chased.
                var next = RecurringPaymentPolicy.ResolvePayability(occurrences)
                    .FirstOrDefault(r => r.IsPayable);
                if (next == null) continue;

                var order = orders.First(o => o.Id == next.OrderId);

                // Money handled outside the website has nothing to request here — a cash job is
                // settled on the day, and an Invoice order is chased through its invoice.
                if (order.PaymentMethod != PaymentMethod.Normal) continue;

                var previousEnd = ResolvePreviousCleaningEnd(orders, order);
                if (!RecurringPaymentPolicy.CanAutomaticallyRequestPayment(
                        previousEnd, order.ServiceDate.Date.Add(order.ServiceTime), nowNy))
                    continue;

                var alreadyAsked = await context.NotificationLogs.AnyAsync(
                    n => n.OrderId == order.Id
                         && n.NotificationType == NotificationTypes.RecurringPaymentRequest, ct);
                if (alreadyAsked) continue;

                var recipient = NoEmailHelper.ResolveOrderNotificationEmail(order.ContactEmail, order.User);
                var phone = !string.IsNullOrWhiteSpace(order.ContactPhone) ? order.ContactPhone : order.User?.Phone;

                var link = await PaymentLinkHelper.BuildPaymentLinkAsync(context, order, frontendUrl);
                var name = order.ContactFirstName;
                var sent = false;

                if (!string.IsNullOrWhiteSpace(recipient) && (order.User?.CanReceiveEmails ?? true))
                {
                    try
                    {
                        // The EXISTING payment-reminder mail, not a new template. There is one
                        // "please pay for this cleaning" message in this system and this is it.
                        await email.SendPaymentReminderEmailAsync(recipient!, name, order.Total, order.Id, link);
                        sent = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Recurring payment request email failed for order {OrderId}.", order.Id);
                    }
                }

                if (!string.IsNullOrWhiteSpace(phone) && (order.User?.CanReceiveMessages ?? true))
                {
                    try
                    {
                        await sms.SendPaymentReminderSmsAsync(phone!, name, order.Total, order.Id, link);
                        sent = true;
                    }
                    catch (InvalidPhoneNumberException)
                    {
                        // SmsService already logged the rejection and reason. This is an expected
                        // skipped delivery, not a worker failure or a successfully sent request.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Recurring payment request SMS failed for order {OrderId}.", order.Id);
                    }
                }

                if (!sent) continue;

                context.NotificationLogs.Add(new NotificationLog
                {
                    OrderId = order.Id,
                    CustomerId = order.UserId,
                    NotificationType = NotificationTypes.RecurringPaymentRequest,
                    SentAt = DateTime.UtcNow
                });

                await context.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Automatic recurring payment request sent for order {OrderId} (series {SeriesId}).",
                    order.Id, seriesId);
            }
        }

        /// <summary>
        /// When the cleaning BEFORE this one in the series finished, or null when there was none.
        ///
        /// End rather than start, because "24 hours after the cleaning" means after the work, and a
        /// long job that starts at 9am is not over at 9am. TotalDuration is the whole job across
        /// however many cleaners, which is the wall-clock figure every customer-facing surface
        /// shows — and erring long is the safe direction for a rule about not asking too soon.
        /// </summary>
        private static DateTime? ResolvePreviousCleaningEnd(List<Order> orders, Order current)
        {
            var previous = orders
                .Where(o => o.Id != current.Id
                            && !OrderStatuses.IsCancelled(o.Status)
                            && o.ServiceDate.Date.Add(o.ServiceTime) < current.ServiceDate.Date.Add(current.ServiceTime))
                .OrderByDescending(o => o.ServiceDate.Date.Add(o.ServiceTime))
                .FirstOrDefault();

            if (previous == null) return null;

            return previous.ServiceDate.Date
                .Add(previous.ServiceTime)
                .AddMinutes((double)previous.TotalDuration);
        }
    }
}

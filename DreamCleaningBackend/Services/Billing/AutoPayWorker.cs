using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models.Billing;
using DreamCleaningBackend.Models.Commercial;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Billing
{
    /// <summary>
    /// The commercial half of AutoPay: invoices of an authorised business client, charged on their
    /// due date. (The recurring half lives in <see cref="RecurringOrderGenerationService"/>, at the
    /// moment its automatic payment request would have gone out — owner's rule, one visit at a
    /// time.)
    /// </summary>
    public class CommercialAutoPaySweep
    {
        private readonly ApplicationDbContext _context;
        private readonly ISavedCardChargeService _charges;
        private readonly BillingFeatures _features;
        private readonly ILogger<CommercialAutoPaySweep> _logger;

        public CommercialAutoPaySweep(
            ApplicationDbContext context,
            ISavedCardChargeService charges,
            BillingFeatures features,
            ILogger<CommercialAutoPaySweep> logger)
        {
            _context = context;
            _charges = charges;
            _features = features;
            _logger = logger;
        }

        /// <summary>
        /// Charges every invoice that is due today or earlier, still owes money, and belongs to a
        /// client whose linked account has an active commercial authorisation.
        ///
        ///  • <b>The due date is the contract.</b> Nothing is charged before it, and the invoice's
        ///    Net terms are never touched.
        ///  • <b>Only invoices due on or after the day the customer authorised.</b> An invoice that
        ///    was already overdue when they signed up is theirs to pay; the authorisation promised
        ///    "on each invoice's due date", not "everything outstanding at once".
        ///  • <b>At most once per invoice.</b> An invoice that already had an automatic charge sent to
        ///    Stripe — whatever came of it — is never charged automatically again. A failure notified
        ///    the customer, who pays it themselves; retrying a card every day is exactly what the
        ///    issuer rules forbid.
        ///  • Drafts, void, paid, and invoices with an ACH debit settling are refused by the charge
        ///    service itself, inside its lock.
        /// </summary>
        public async Task<int> RunAsync(CancellationToken ct)
        {
            if (!_features.AutoPayEnabled) return 0;

            var today = NyTimeHelper.NowNy.Date;
            var authorizations = await _context.PaymentAuthorizations.AsNoTracking()
                .Where(a => a.Scope == PaymentAuthorizationScope.CommercialClient
                            && a.Status == PaymentAuthorizationStatus.Active && a.ContractClientId != null)
                .ToListAsync(ct);

            var charged = 0;
            foreach (var authorization in authorizations)
            {
                ct.ThrowIfCancellationRequested();

                var authorizedOn = NyTimeHelper.ToNy(authorization.AcceptedAt).Date;
                var invoices = await _context.CommercialInvoices.AsNoTracking()
                    .Where(i => i.ContractClientId == authorization.ContractClientId
                                && (i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.Viewed
                                    || i.Status == InvoiceStatus.PartiallyPaid || i.Status == InvoiceStatus.Overdue)
                                && i.BalanceDue >= 0.50m
                                && i.DueDate.Date <= today
                                && i.DueDate.Date >= authorizedOn)
                    .Select(i => i.Id)
                    .ToListAsync(ct);

                foreach (var invoiceId in invoices)
                {
                    var key = BillingPaymentAttempt.InvoiceObligationKey(invoiceId);
                    var alreadyRun = await _context.BillingPaymentAttempts.AnyAsync(a =>
                        a.ObligationKey == key && a.Trigger == BillingAttemptTrigger.AutoPayCommercial
                        && a.Status != BillingAttemptStatus.Canceled, ct);
                    if (alreadyRun) continue;

                    try
                    {
                        var result = await _charges.ChargeInvoiceAsync(invoiceId, authorization.UserId,
                            BillingAttemptTrigger.AutoPayCommercial, selectedCardId: null, initiatedByUserId: null, ct);
                        _logger.LogInformation("Commercial AutoPay for invoice {InvoiceId}: {Result} — {Message}",
                            invoiceId, result.Result, result.Message);
                        if (result.Charged) charged++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Commercial AutoPay for invoice {InvoiceId} failed unexpectedly.", invoiceId);
                    }
                }
            }

            return charged;
        }
    }

    /// <summary>
    /// The billing background worker. Every five minutes: deliver pending billing notices,
    /// reconcile saved-card attempts whose outcome is unknown, finish orphaned runs, and record any
    /// "Pay all upcoming" charge Stripe took that this database has not. Once a
    /// New York day (from 9 am, so nobody is charged overnight): the commercial AutoPay sweep.
    ///
    /// Same shape as the codebase's other workers — one bad pass is logged and never kills the loop.
    /// </summary>
    public class AutoPayWorker : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<AutoPayWorker> _logger;

        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
        private DateTime? _lastCommercialSweepDate;

        public AutoPayWorker(IServiceProvider services, ILogger<AutoPayWorker> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let startup migrations finish first.
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ContinueWith(_ => { }, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);

                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        public async Task RunOnceAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ISavedCardChargeService>().ReconcileStaleAttemptsAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Billing reconciliation pass failed.");
            }

            // "Pay all upcoming" charges Stripe took but this database has not recorded (a failed
            // settlement, a missed webhook). Not gated on the billing flags: combined payments
            // predate them and are live whatever the rollout state.
            try
            {
                using var scope = _services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IRecurringCustomerPaymentService>().ReconcileUnsettledBatchesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Combined-payment reconciliation pass failed.");
            }

            // ...and the post-payment follow-up (loyalty, subscription, confirmation) of every
            // order a combined payment settled — the durable backstop behind the immediate trigger.
            try
            {
                using var scope = _services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ICombinedPaymentFollowUpService>().ProcessAsync(null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Combined-payment follow-up pass failed.");
            }

            try
            {
                var nowNy = NyTimeHelper.NowNy;
                if (_lastCommercialSweepDate != nowNy.Date && nowNy.Hour >= 9)
                {
                    using var scope = _services.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<CommercialAutoPaySweep>().RunAsync(ct);
                    _lastCommercialSweepDate = nowNy.Date;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Commercial AutoPay sweep failed.");
            }

            try
            {
                using var scope = _services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IBillingNotificationService>().DeliverDueAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Billing notice delivery pass failed.");
            }
        }
    }
}

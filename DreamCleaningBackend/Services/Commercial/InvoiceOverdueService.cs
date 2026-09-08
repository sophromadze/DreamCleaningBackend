using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>
    /// Flips past-due invoices to Overdue once a day.
    ///
    /// THE BACKEND OWNS THIS STATE, not the browser. Rendering "overdue" from a date comparison in
    /// the UI would mean the database never actually said so - and everything that reads status
    /// without rendering it (the summary cards, a filter, a future reminder job, an export) would
    /// quietly disagree with what the admin sees on screen.
    ///
    /// Same shape as the other background workers in this codebase (ContractRetentionService,
    /// LoyaltyReengagementService): an hourly loop that does its real work once per calendar day,
    /// so a restart at any hour cannot skip a day and a long-running process cannot repeat one.
    ///
    /// Paid, Void and Draft are never touched - InvoiceStatusPolicy.ShouldBecomeOverdue is the
    /// single expression of that rule, shared with every other path that recomputes status.
    /// </summary>
    public class InvoiceOverdueService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<InvoiceOverdueService> _logger;

        private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);
        private DateTime? _lastSweepDate;

        public InvoiceOverdueService(IServiceProvider services, ILogger<InvoiceOverdueService> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // A short settle before the first pass so startup migrations and seeding finish first.
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ContinueWith(_ => { }, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var today = DateTime.UtcNow.Date;
                    if (_lastSweepDate != today)
                    {
                        await SweepAsync(stoppingToken);
                        _lastSweepDate = today;
                    }
                }
                catch (Exception ex)
                {
                    // Never let one bad pass kill the worker - it would stop every later day too.
                    _logger.LogError(ex, "Invoice overdue sweep failed.");
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
        /// One pass. Public so an admin endpoint can trigger it on demand, which is also how it
        /// gets exercised without waiting a day.
        /// </summary>
        public async Task<int> SweepAsync(CancellationToken ct = default)
        {
            using var scope = _services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var invoices = scope.ServiceProvider.GetRequiredService<InvoiceService>();

            var today = DateTime.UtcNow.Date;

            // Narrowed in SQL to the only statuses that can move, then decided per row by the
            // shared policy - the query is an index-friendly prefilter, not a second copy of the
            // rule.
            var candidates = await context.CommercialInvoices
                .Where(i => i.DueDate < today
                            && i.BalanceDue > 0m
                            && (i.Status == InvoiceStatus.Sent
                                || i.Status == InvoiceStatus.Viewed
                                || i.Status == InvoiceStatus.PartiallyPaid))
                .ToListAsync(ct);

            var changed = 0;

            foreach (var invoice in candidates)
            {
                if (!InvoiceStatusPolicy.ShouldBecomeOverdue(
                        invoice.Status, invoice.BalanceDue, invoice.DueDate, today))
                    continue;

                invoice.Status = InvoiceStatus.Overdue;
                invoice.UpdatedAt = DateTime.UtcNow;
                changed++;
            }

            if (changed > 0)
            {
                await context.SaveChangesAsync(ct);

                foreach (var invoice in candidates.Where(i => i.Status == InvoiceStatus.Overdue))
                {
                    await invoices.LogActivityAsync(invoice.Id, "invoice_overdue",
                        $"Invoice became overdue - it was due on {invoice.DueDate:MMMM d, yyyy} "
                        + $"with {invoice.BalanceDue:C} outstanding.",
                        null, "System");
                }

                _logger.LogInformation("Marked {Count} commercial invoice(s) overdue.", changed);
            }

            return changed;
        }
    }
}

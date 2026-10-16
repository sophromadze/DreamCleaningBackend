using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Commercial;

namespace DreamCleaningBackend.Services.Reporting
{
    /// <summary>
    /// The Stripe fee each order cost, estimated from the published rates — the ONE place it is
    /// worked out (2026-10). Statistics (totals card, daily chart, agent rows), Outgoing Payments →
    /// Admins, the Shifts orders view and the "% of net" admin bonus all read it, so the same order
    /// costs the same fee on every screen. Never alters the amounts customers/admins see on an order.
    /// </summary>
    public interface IStripeFeeEstimator
    {
        /// <summary>
        /// Fee per order id, rounded to cents. Only card-flow orders (IsPaid, PaymentMethod Normal)
        /// get a row; every other order costs nothing and is simply absent.
        /// </summary>
        Task<Dictionary<int, decimal>> ByOrderAsync(IQueryable<Order> orders);
    }

    public class StripeFeeEstimator : IStripeFeeEstimator
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration? _configuration;

        /// <param name="configuration">Null (tests) = Stripe's standard US rates below.</param>
        public StripeFeeEstimator(ApplicationDbContext context, IConfiguration? configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        private decimal Rate(string key, decimal fallback) =>
            _configuration == null ? fallback : _configuration.GetValue(key, fallback);

        // Stripe US standard processing fee: 2.9% of the charged amount + $0.30 per transaction.
        // Overridable via config without code changes.
        private decimal StripeFeePercent => Rate("Stripe:FeePercent", 0.029m);
        private decimal StripeFixedFee => Rate("Stripe:FixedFeePerOrder", 0.30m);

        // Stripe ACH Direct Debit pricing: 0.8% capped at $5.00 per debit (2026-09). A regular
        // invoice paid "from your bank online" costs this, not the card rate. Overridable too.
        private decimal StripeAchFeePercent => Rate("Stripe:AchFeePercent", 0.008m);
        private decimal StripeAchFeeCap => Rate("Stripe:AchFeeCap", 5.00m);

        /// <summary>
        /// The Stripe fee each card-flow order (IsPaid, PaymentMethod Normal) actually cost.
        /// Money that never went through a card is taken out of the card-fee base:
        ///  - order-edit top-ups paid outside Stripe (Zelle / cash / check / bank transfer),
        ///  - part-payment slices an admin RECORDED as paid outside Stripe (e.g. a regular
        ///    invoice paid by bank transfer) — no Stripe fee at all,
        ///  - regular-invoice slices paid by Stripe ACH — charged the ACH rate instead.
        /// The card fixed fee applies only when something was actually charged to a card.
        /// </summary>
        public async Task<Dictionary<int, decimal>> ByOrderAsync(IQueryable<Order> orders)
        {
            var paid = orders.Where(o => o.IsPaid && o.PaymentMethod == PaymentMethod.Normal);

            var totals = await paid.Select(o => new { o.Id, o.Total }).ToListAsync();
            if (totals.Count == 0) return new Dictionary<int, decimal>();

            var manualTopUps = await paid
                .SelectMany(o => o.UpdateHistory)
                .Where(h => h.IsPaid && h.PaymentMethod != PaymentMethod.Normal)
                .GroupBy(h => h.OrderId)
                .Select(g => new { OrderId = g.Key, Sum = g.Sum(x => x.AdditionalAmount) })
                .ToDictionaryAsync(x => x.OrderId, x => x.Sum);

            var ids = totals.Select(t => t.Id).ToList();

            var manualSlices = await _context.OrderPartialPayments
                .Where(p => ids.Contains(p.OrderId) && p.Status == OrderPartialPaymentStatus.Paid
                            && p.PaymentMethod != PaymentMethod.Normal)
                .GroupBy(p => p.OrderId)
                .Select(g => new { OrderId = g.Key, Sum = g.Sum(x => x.PaidAmount ?? x.RequestedAmount) })
                .ToDictionaryAsync(x => x.OrderId, x => x.Sum);

            var achDebits = (await _context.CustomerInvoicePaymentAttempts
                    .Where(a => ids.Contains(a.OrderId) && a.Status == CustomerInvoicePaymentAttemptStatus.Succeeded)
                    .Select(a => new { a.OrderId, a.Amount, a.ProcessingFee })
                    .ToListAsync())
                .ToLookup(a => a.OrderId);

            var fees = new Dictionary<int, decimal>();
            foreach (var t in totals)
            {
                var ach = achDebits[t.Id].ToList();
                var cardBase = t.Total
                    - (manualTopUps.TryGetValue(t.Id, out var top) ? top : 0m)
                    - (manualSlices.TryGetValue(t.Id, out var man) ? man : 0m)
                    - ach.Sum(a => a.Amount);

                var cardFee = cardBase >= 0.50m ? cardBase * StripeFeePercent + StripeFixedFee : 0m;
                // Stripe charges ACH on the whole debit (bill + any fee passed to the customer).
                var achFee = ach.Sum(a => Math.Min((a.Amount + a.ProcessingFee) * StripeAchFeePercent, StripeAchFeeCap));
                fees[t.Id] = decimal.Round(cardFee + achFee, 2);
            }
            return fees;
        }
    }
}

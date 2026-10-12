using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// The all-time work and pay figures on the cleaners-dashboard detail panel (2026-10): jobs,
    /// hours, average hours per working day, wages paid out, wages still owed, average wage per
    /// cleaning, and tips.
    ///
    /// Nothing here is new arithmetic. Every figure is read off the cleaner's OWN payroll line, as
    /// <see cref="CleanerPayrollCalculator"/> builds it, and the paid/owed split goes through
    /// <see cref="CleanerPayoutSettlement"/> — so the panel quotes exactly what Outgoing Payments
    /// would pay, overrides and withheld tips included. Rules that must hold:
    ///
    /// 1. **Completed = <c>Status == Done</c>** — the same orders the panel's "Jobs completed"
    ///    always counted. (Outgoing Payments additionally drops part-refunded Done orders; this
    ///    panel describes the work, so a refunded job still counts as worked.)
    /// 2. **Wages and tips are never mixed.** <c>PaidAmount</c> on a line is ONE running figure for
    ///    salary + tip, so it is split here: the payment covers the line's tip share FIRST and the
    ///    rest is wages. The routine reason a paid line is short is hours added afterwards (the
    ///    case <see cref="CleanerPayoutSettlement"/> was written for), and tip-first reports that
    ///    shortfall as wages owed, which is what it is. Paid wages are capped at the line's wages,
    ///    so Paid out + Remaining is always the cleaner's total earned wages — an overpayment is
    ///    reported by Outgoing Payments, not added here.
    /// 3. **Tips are the share the cleaner is PAID** (<c>CleanerLine.Tips</c>): a tip the office
    ///    withheld is not theirs and is not counted.
    /// 4. **Money is returned only to a caller allowed to see it** — the caller passes
    ///    <c>includePay</c>; without it the four money fields stay null and never leave the API.
    /// </summary>
    public static class CleanerWorkStats
    {
        /// <param name="completedOrders">
        /// The cleaner's Done orders, with <c>OrderCleaners</c> and <c>OrderServices → Service</c>
        /// loaded (the payroll split needs both).
        /// </param>
        public static CleanerWorkStatsDto Build(IEnumerable<Order> completedOrders, int cleanerId, bool includePay)
        {
            var jobs = 0;
            var minutes = 0m;
            var paidWages = 0m;
            var remainingWages = 0m;
            var tips = 0m;
            var days = new HashSet<DateTime>();

            foreach (var order in completedOrders)
            {
                if (order.Status != OrderStatuses.Done) continue;

                var payroll = CleanerPayrollCalculator.Build(
                    order, CleanerPayrollCalculator.HasCleanerHoursService(order), order.OrderCleaners);
                var line = payroll.Lines.FirstOrDefault(l => l.CleanerId == cleanerId);
                if (line == null) continue;

                jobs++;
                days.Add(order.ServiceDate.Date);
                minutes += line.BillableMinutes;
                tips += line.Tips;

                var assignment = order.OrderCleaners.First(oc => oc.Id == line.OrderCleanerId);
                var settlement = CleanerPayoutSettlement.Resolve(assignment.IsPaid, assignment.PaidAmount, line.Payout);

                var paidTowardWages = Math.Max(0m, settlement.PaidAmount - line.Tips);
                var wagesPaidOnLine = Math.Min(paidTowardWages, line.Salary);
                paidWages += wagesPaidOnLine;
                remainingWages += line.Salary - wagesPaidOnLine;
            }

            var stats = new CleanerWorkStatsDto
            {
                JobsCompleted = jobs,
                TotalMinutesWorked = minutes,
                DaysWorked = days.Count,
                AverageMinutesPerDay = days.Count == 0 ? 0m : Math.Round(minutes / days.Count, 2, MidpointRounding.AwayFromZero),
                IncludesPay = includePay
            };

            if (includePay)
            {
                stats.PaidOut = OrderPricingCalculator.Round2(paidWages);
                stats.RemainingToPay = OrderPricingCalculator.Round2(remainingWages);
                stats.AverageEarnedPerJob = jobs == 0
                    ? 0m
                    : OrderPricingCalculator.Round2((paidWages + remainingWages) / jobs);
                stats.Tips = OrderPricingCalculator.Round2(tips);
            }

            return stats;
        }
    }
}

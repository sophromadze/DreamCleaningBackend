using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Cleanings whose customer charge is a WEEKLY FLAT FEE contract's weekly invoice (2026-10).
    ///
    /// A recurring plan linked to such a contract generates OPERATIONAL orders — they exist for
    /// staffing, schedules, attendance and invoice linking — and they are created at $0 with no
    /// tip. Showing "$0.00" beside one reads as a free cleaning, so every price surface (admin
    /// list and panel, My Orders, order details, the upcoming list) shows this label instead.
    ///
    /// Decided from the order's <c>ContractId</c> and that contract's pricing basis, read from its
    /// current signed version (else its draft) — the same source billing reads. Lives on the
    /// residential side and parses the snapshot itself, because the residential order services
    /// may not depend on the commercial invoicing namespace (CommercialResidentialSeparationTests).
    /// </summary>
    public static class ContractBilledOrders
    {
        public const string LabelText = "Billed weekly by contract";

        public static string Label(string? contractNumber) =>
            string.IsNullOrWhiteSpace(contractNumber) ? LabelText : $"{LabelText} {contractNumber}";

        /// <summary>
        /// Order id → label, for the orders among <paramref name="orders"/> performed under a
        /// weekly-flat-fee contract. One query per distinct contract set, never per order.
        /// </summary>
        public static async Task<Dictionary<int, string>> LoadLabelsAsync(
            ApplicationDbContext context, IEnumerable<Order> orders)
        {
            var list = orders.Where(o => o.ContractId != null).ToList();
            if (list.Count == 0) return new Dictionary<int, string>();

            var numbers = await LoadWeeklyFlatFeeContractNumbersAsync(
                context, list.Select(o => o.ContractId!.Value));

            return list
                .Where(o => numbers.ContainsKey(o.ContractId!.Value))
                .ToDictionary(o => o.Id, o => Label(numbers[o.ContractId!.Value]));
        }

        public static async Task<string?> LoadLabelAsync(ApplicationDbContext context, Order order) =>
            (await LoadLabelsAsync(context, new[] { order })).GetValueOrDefault(order.Id);

        /// <summary>Contract id → number, for the weekly-flat-fee contracts among the ids.</summary>
        public static async Task<Dictionary<int, string>> LoadWeeklyFlatFeeContractNumbersAsync(
            ApplicationDbContext context, IEnumerable<int> contractIds)
        {
            var ids = contractIds.Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<int, string>();

            var contracts = await context.Contracts
                .Where(c => ids.Contains(c.Id))
                .Select(c => new { c.Id, c.ContractNumber, c.CurrentVersionId, c.DraftSnapshotJson })
                .AsNoTracking()
                .ToListAsync();

            var versionIds = contracts.Where(c => c.CurrentVersionId != null).Select(c => c.CurrentVersionId!.Value).ToList();
            var versions = versionIds.Count == 0
                ? new Dictionary<int, string>()
                : await context.ContractVersions
                    .Where(v => versionIds.Contains(v.Id))
                    .ToDictionaryAsync(v => v.Id, v => v.FullSnapshotJson);

            var result = new Dictionary<int, string>();
            foreach (var c in contracts)
            {
                var json = c.CurrentVersionId != null && versions.TryGetValue(c.CurrentVersionId.Value, out var v)
                    && !string.IsNullOrWhiteSpace(v) ? v : c.DraftSnapshotJson;
                if (ContractSnapshot.Parse(json).Pricing.PricingBasis == ContractPricingBasis.WeeklyFlatFee)
                    result[c.Id] = c.ContractNumber;
            }
            return result;
        }
    }
}

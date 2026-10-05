using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>
    /// The billing facts of ONE contract that recurring generation and invoice linking need:
    /// which client and location it belongs to, whether it charges per visit or a flat weekly
    /// fee, the weekly figures, and where its service week starts.
    ///
    /// Read from the CURRENT VERSION's frozen snapshot (falling back to the draft), the same
    /// source <see cref="RecurringInvoiceService.LoadSnapshotAsync"/> bills from, so the
    /// recurrence panel, the invoice editor and "Create Next Invoice" cannot disagree about what
    /// the contract charges.
    /// </summary>
    public sealed class ContractBillingProfile
    {
        public int ContractId { get; init; }
        public string ContractNumber { get; init; } = "";
        public int ContractClientId { get; init; }
        public int ServiceLocationId { get; init; }
        public string? ServiceAddress { get; init; }
        public ContractStatus Status { get; init; }
        public ContractPricingBasis PricingBasis { get; init; }
        public ContractPriceMode PriceMode { get; init; }

        /// <summary>Scheduled visits per service week (per fee period for a flat fee).</summary>
        public int VisitsPerWeek { get; init; }

        public decimal PreTaxPrice { get; init; }
        public decimal SalesTaxAmount { get; init; }
        public decimal TotalPrice { get; init; }
        public string WeekDefinition { get; init; } = "";
        public DayOfWeek WeekStart { get; init; } = ServiceWeekCalculator.DefaultWeekStart;

        public bool IsWeeklyFlatFee => PricingBasis == ContractPricingBasis.WeeklyFlatFee;

        public static async Task<ContractBillingProfile?> LoadAsync(ApplicationDbContext context, int contractId)
        {
            var contract = await context.Contracts
                .Include(c => c.ServiceLocation)
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == contractId);

            return contract == null ? null : await FromContractAsync(context, contract);
        }

        public static async Task<ContractBillingProfile> FromContractAsync(ApplicationDbContext context, Contract contract)
        {
            Contracts.ContractSnapshot? snapshot = null;
            try { snapshot = await RecurringInvoiceService.LoadSnapshotAsync(context, contract); }
            catch { /* a malformed snapshot reads as an unpriced per-visit contract, never a crash */ }

            var pricing = snapshot?.Pricing;
            var schedule = snapshot?.Schedule;

            return new ContractBillingProfile
            {
                ContractId = contract.Id,
                ContractNumber = contract.ContractNumber,
                ContractClientId = contract.ContractClientId,
                ServiceLocationId = contract.ContractServiceLocationId,
                ServiceAddress = contract.ServiceLocation == null ? null : InvoiceService.FormatLocation(contract.ServiceLocation),
                Status = contract.Status,
                PricingBasis = pricing?.PricingBasis ?? ContractPricingBasis.PerVisit,
                PriceMode = pricing?.PriceMode ?? ContractPriceMode.TaxInclusive,
                VisitsPerWeek = Math.Max(1, schedule?.VisitsPerPeriod ?? 1),
                PreTaxPrice = pricing?.PreTaxPrice ?? 0m,
                SalesTaxAmount = pricing?.SalesTaxAmount ?? 0m,
                TotalPrice = pricing?.TotalPrice ?? 0m,
                WeekDefinition = schedule?.WeekDefinition ?? "",
                WeekStart = ServiceWeekCalculator.ParseWeekStart(schedule?.WeekDefinition)
            };
        }
    }
}

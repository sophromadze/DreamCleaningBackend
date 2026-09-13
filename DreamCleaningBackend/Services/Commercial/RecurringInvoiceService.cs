using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>
    /// "Create Next Invoice" on a commercial contract.
    ///
    /// THE POINT IS TO REMOVE RETYPING, NOT TO REMOVE THE ADMIN. It produces a DRAFT and stops.
    /// Nothing is emailed, no number is exposed to the client, and the invoice does not appear in
    /// My Invoices until somebody presses Send - which is why the button is not called "Send New
    /// Invoice". Everything it gets wrong is therefore fixable on a screen before it reaches a
    /// client, and everything it is unsure about it says out loud instead of guessing.
    ///
    /// ══ WHAT IT CLONES, AND WHY THAT ONE ══
    ///
    /// The template is the most recent FINALIZED (sent) invoice for the contract - never a draft,
    /// which may be half-typed or abandoned, and never "the latest row". The new invoice should
    /// look as much as possible like the one the client already accepted, so line items,
    /// descriptions, quantities, rates, the discount, the tax mode AND ITS SNAPSHOTTED RATE, the
    /// payment method and the customer note all carry over verbatim.
    ///
    /// <b>The contract's current price is deliberately NOT applied.</b> A contract edited last
    /// month must not silently re-price a recurring invoice; if the two differ, that is a WARNING
    /// on the draft the admin is about to read, not an adjustment made behind their back.
    ///
    /// ══ WHAT IS NEVER CLONED ══
    ///
    /// The invoice number, the public token, the created date, the due date, the service dates,
    /// payment status, amount paid, balance, Stripe attempts, payment rows, email history and the
    /// activity trail. Those belong to the invoice that earned them; carrying any of them would
    /// claim the new invoice had already been seen or paid.
    ///
    /// ══ DATES ══
    ///
    /// Worked out by <see cref="ServiceScheduleCalculator"/> from the contract's service days, its
    /// billing cadence and the period the previous invoice covered. When there is not enough to go
    /// on, the dates are left EMPTY and the admin is told why - an invented "September 8-12" that
    /// no schedule supports is worse than a blank field, because it looks authoritative.
    /// </summary>
    public class RecurringInvoiceService
    {
        private readonly ApplicationDbContext _context;
        private readonly InvoiceService _invoices;
        private readonly InvoiceNumberService _numbers;
        private readonly BillingSettingsService _billing;
        private readonly ILogger<RecurringInvoiceService> _logger;

        public RecurringInvoiceService(
            ApplicationDbContext context,
            InvoiceService invoices,
            InvoiceNumberService numbers,
            BillingSettingsService billing,
            ILogger<RecurringInvoiceService> logger)
        {
            _context = context;
            _invoices = invoices;
            _numbers = numbers;
            _billing = billing;
            _logger = logger;
        }

        /// <summary>
        /// Builds the next draft invoice for a contract.
        ///
        /// Returns the invoice plus every warning worth an admin's attention. Warnings never block:
        /// a draft that exists and is flagged is more useful than a refusal, because the admin can
        /// see what the system thought and correct it in place.
        /// </summary>
        public async Task<(CommercialInvoice Invoice, CreateNextInvoiceResultDto Result)>
            CreateNextAsync(int contractId, CreateNextInvoiceDto dto, int userId)
        {
            var contract = await _context.Contracts
                .Include(c => c.ContractClient)
                .Include(c => c.ServiceLocation)
                .FirstOrDefaultAsync(c => c.Id == contractId)
                ?? throw new InvoiceWorkflowException("Contract not found.");

            // ONE ELIGIBILITY RULE, shared with the button. ContractInvoiceEligibility is what
            // both the frontend flag and this guard read, so a contract that cannot be invoiced
            // cannot be invoiced by somebody who knows the URL either. It also covers the deleted
            // case, which used to be checked separately here.
            var ineligible = ContractInvoiceEligibility.Check(contract.Status, contract.IsHidden);
            if (ineligible != null) throw new InvoiceWorkflowException(ineligible);

            // Determine the intended period before checking existing drafts.
            var snapshot = await LoadSnapshotAsync(_context, contract);
            var result = new CreateNextInvoiceResultDto();

            // ── The template ──
            var previous = await FindLastFinalizedInvoiceAsync(contractId);
            if (previous != null) result.ClonedFromInvoiceNumber = previous.InvoiceNumber;

            // ── The dates ──
            var schedule = ServiceScheduleCalculator.Next(BuildScheduleInput(snapshot, contract, previous));

            if (!dto.AllowDuplicatePeriod)
            {
                var openDraft = await FindOpenDraftAsync(contractId, schedule.PeriodStart, schedule.PeriodEnd);
                if (openDraft != null) throw ExistingDraftInvoiceException.For(openDraft);
            }
            if (!dto.AcknowledgeUndatedDraft)
            {
                var undated = await FindUndatedDraftAsync(contractId);
                if (undated != null) throw ExistingDraftInvoiceException.For(undated, undated: true);
            }

            if (!schedule.HasSchedule)
            {
                result.NeedsServiceDates = true;
                if (!string.IsNullOrWhiteSpace(schedule.Reason)) result.Warnings.Add(schedule.Reason!);
            }
            else if (!dto.AllowDuplicatePeriod)
            {
                // Generating twice for one month is easy to do and hard to spot: the two invoices
                // are identical apart from their numbers, and the client is billed twice. Checked
                // before anything is written so the override is a decision rather than a cleanup.
                var clash = await FindOverlappingInvoiceAsync(
                    contractId, schedule.PeriodStart!.Value, schedule.PeriodEnd!.Value);

                if (clash != null)
                {
                    var period = ServiceDateFormatter.Describe(
                        null, schedule.PeriodStart, schedule.PeriodEnd);

                    throw new InvoiceWorkflowException(
                        $"Invoice {clash.InvoiceNumber} already covers {period.Text}. "
                        + "Confirm that you want to raise a second invoice for the same period.");
                }
            }

            var settings = await _billing.GetOrCreateAsync();
            var today = NyTimeHelper.NowNy.Date;

            var invoice = new CommercialInvoice
            {
                ContractClientId = contract.ContractClientId,
                ContractId = contract.Id,
                ContractServiceLocationId = contract.ContractServiceLocationId,
                Status = InvoiceStatus.Draft,
                InvoiceDate = today,
                Currency = "USD",
                CreatedByUserId = userId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,

                // Fresh every time. Reusing any of these would tie the new invoice to money that
                // was received for a different period.
                AmountPaid = 0m,
                InternalNote = null,

                ServiceStartDate = schedule.PeriodStart,
                ServiceEndDate = schedule.PeriodEnd,
                ServiceAddress = contract.ServiceLocation == null
                    ? previous?.ServiceAddress
                    : InvoiceService.FormatLocation(contract.ServiceLocation)
            };

            ApplyServiceDates(invoice, schedule.ServiceDates);
            ApplyDueDate(invoice, schedule, previous, settings);

            if (previous != null) CloneFrom(previous, invoice);
            else ApplyContractPricing(snapshot, settings, invoice, result);

            InvoiceService.RecomputeTotalsFromRows(invoice);

            // Allocated last, and retried on the unique index like every other create path - the
            // existence check and the insert are not atomic.
            await SaveWithNumberAsync(invoice);

            if (previous != null)
            {
                AddPricingDriftWarning(snapshot, invoice, previous, result);
                AddTaxDriftWarning(snapshot, invoice, previous, result);
            }

            // THE WARNINGS ARE STORED ON THE DRAFT, not merely returned.
            //
            // Returning them alone was the bug: "Create Next Invoice" pressed from the CONTRACTS
            // LIST navigates immediately, so the banner rendered on a page the admin was already
            // leaving and the price drift went to the client unread. Persisted here, the draft
            // editor shows them however the admin arrived — from the contract detail page, from
            // the list, or from a bookmark opened tomorrow.
            PersistDraftWarnings(invoice, result);
            await _context.SaveChangesAsync();

            await _invoices.LogActivityAsync(invoice.Id, "invoice_created",
                $"Invoice {invoice.InvoiceNumber} created from contract {contract.ContractNumber}"
                + (previous == null
                    ? " using the contract's current pricing."
                    : $", modelled on {previous.InvoiceNumber}."),
                userId);

            return (invoice, result);
        }

        // ── The contract's own configuration ──────────────────────────────────────────────────

        /// <summary>
        /// The contract's CURRENT version snapshot, falling back to its draft.
        ///
        /// A signed contract bills from what was signed; a contract still in draft bills from what
        /// is being negotiated, which is the only thing that exists yet.
        /// </summary>
        public static async Task<ContractSnapshot> LoadSnapshotAsync(ApplicationDbContext context, Contract contract)
        {
            if (contract.CurrentVersionId.HasValue)
            {
                var json = await context.ContractVersions
                    .Where(v => v.Id == contract.CurrentVersionId.Value)
                    .Select(v => v.FullSnapshotJson)
                    .FirstOrDefaultAsync();

                if (!string.IsNullOrWhiteSpace(json)) return ContractSnapshot.Parse(json);
            }

            return ContractSnapshot.Parse(contract.DraftSnapshotJson);
        }

        private static ServiceScheduleInput BuildScheduleInput(
            ContractSnapshot snapshot, Contract contract, CommercialInvoice? previous)
        {
            var billing = snapshot.Billing ?? new BillingCadenceSnapshot();

            return new ServiceScheduleInput
            {
                ServiceDays = snapshot.Schedule.ResolveServiceDays(),
                FrequencyUnit = snapshot.Schedule.FrequencyUnit,
                VisitsPerPeriod = snapshot.Schedule.VisitsPerPeriod,
                BillingFrequency = billing.Frequency,
                BillingIntervalCount = billing.IntervalCount,
                PreviousPeriodEnd = previous?.ServiceEndDate ?? previous?.ServiceStartDate,
                PreviousFirstServiceDate = ResolvePreviousFirstServiceDate(previous),
                AnchorDate = billing.AnchorDate ?? snapshot.EffectiveDate,
                // The business runs on New York time, and a service date is a wall-clock date -
                // reading UTC here would roll the schedule a day forward every evening.
                Today = NyTimeHelper.NowNy.Date,
                PaymentDeadlineHours = snapshot.Pricing.PaymentDeadlineHours
            };
        }

        private static DateTime? ResolvePreviousFirstServiceDate(CommercialInvoice? previous)
        {
            if (previous == null) return null;

            var listed = InvoiceService.ParseServiceDates(previous.ServiceDatesJson);
            return listed.Count > 0 ? listed[0] : previous.ServiceStartDate;
        }

        // ── Finding what to clone ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The most recent invoice for this contract that was actually ISSUED.
        ///
        /// Drafts are excluded because a draft may be half-typed or abandoned and copying one would
        /// propagate a mistake nobody ever agreed to; void invoices because they were cancelled.
        /// Ordered by the period they covered rather than by id, so an invoice raised late for an
        /// earlier month does not become the template for the next one.
        /// </summary>
        public Task<CommercialInvoice?> FindLastFinalizedInvoiceAsync(int contractId) =>
            _context.CommercialInvoices
                .Include(i => i.Items)
                .Where(i => i.ContractId == contractId
                            && i.Status != InvoiceStatus.Draft
                            && i.Status != InvoiceStatus.Void
                            && i.FirstSentAt != null)
                .OrderByDescending(i => i.ServiceEndDate ?? i.ServiceStartDate ?? i.InvoiceDate)
                .ThenByDescending(i => i.Id)
                .FirstOrDefaultAsync();

        /// <summary>
        /// The newest unsent Draft whose known period overlaps the intended period.
        /// Different periods are independent; undated drafts use their own explicit warning.
        /// </summary>
        public async Task<CommercialInvoice?> FindOpenDraftAsync(int contractId, DateTime? start = null, DateTime? end = null)
        {
            if (start == null || end == null) return null;
            var drafts = await _context.CommercialInvoices
                .Where(i => i.ContractId == contractId
                            && i.Status == InvoiceStatus.Draft
                            && i.FirstSentAt == null)
                .OrderByDescending(i => i.CreatedAt)
                .ThenByDescending(i => i.Id)
                .ToListAsync();
            return drafts.FirstOrDefault(i => {
                var dates = InvoiceService.ParseServiceDates(i.ServiceDatesJson);
                var first = i.ServiceStartDate ?? (dates.Count > 0 ? dates.Min() : i.ServiceEndDate);
                var last = i.ServiceEndDate ?? (dates.Count > 0 ? dates.Max() : i.ServiceStartDate);
                return first.HasValue && last.HasValue && first.Value <= end.Value && last.Value >= start.Value;
            });
        }

        public async Task<CommercialInvoice?> FindUndatedDraftAsync(int contractId)
        {
            var drafts = await _context.CommercialInvoices.Where(i => i.ContractId == contractId
                && i.Status == InvoiceStatus.Draft && i.FirstSentAt == null
                && i.ServiceStartDate == null && i.ServiceEndDate == null)
                .OrderByDescending(i => i.CreatedAt).ToListAsync();
            return drafts.FirstOrDefault(i => InvoiceService.ParseServiceDates(i.ServiceDatesJson).Count == 0);
        }

        /// <summary>
        /// An issued invoice whose service period overlaps the one about to be generated.
        ///
        /// Overlap rather than exact equality: two invoices covering "October 1-15" and
        /// "October 10-31" are still double-billing ten days of cleaning, and an exact match would
        /// wave that through.
        /// </summary>
        public Task<CommercialInvoice?> FindOverlappingInvoiceAsync(
            int contractId, DateTime start, DateTime end) =>
            _context.CommercialInvoices
                .Where(i => i.ContractId == contractId
                            && i.Status != InvoiceStatus.Draft
                            && i.Status != InvoiceStatus.Void
                            && i.ServiceStartDate != null
                            && i.ServiceEndDate != null
                            && i.ServiceStartDate <= end
                            && i.ServiceEndDate >= start)
                .OrderByDescending(i => i.Id)
                .FirstOrDefaultAsync();

        // ── Building the draft ────────────────────────────────────────────────────────────────

        private static void ApplyServiceDates(CommercialInvoice invoice, List<DateTime> dates)
        {
            invoice.ServiceDatesJson = dates.Count == 0
                ? null
                : System.Text.Json.JsonSerializer.Serialize(dates.Select(d => d.Date).ToList());
        }

        /// <summary>
        /// The due date, from the contract's own payment deadline where the schedule supports it.
        ///
        /// Exhibit B promises payment a fixed number of hours BEFORE service, so the due date is
        /// anchored to the first visit the invoice covers - "48 hours before October 7" is
        /// October 5. With no schedule to anchor to it falls back to the previous invoice's terms,
        /// and then to the configured default, so the field is never left blank.
        /// </summary>
        private static void ApplyDueDate(
            CommercialInvoice invoice,
            ServiceScheduleResult schedule,
            CommercialInvoice? previous,
            BillingSettings settings)
        {
            if (schedule.DueDate.HasValue)
            {
                invoice.DueTerms = InvoiceDueTerms.Custom;
                invoice.DueDate = schedule.DueDate.Value;
                return;
            }

            // The previous invoice's TERMS carry over, but not if they were Custom: a custom due
            // date belongs to the period it was picked for, and re-resolving Custom without a date
            // to pick would silently make the new invoice due today.
            var terms = previous?.DueTerms ?? settings.DefaultDueTerms;
            if (terms == InvoiceDueTerms.Custom) terms = settings.DefaultDueTerms;
            if (terms == InvoiceDueTerms.Custom) terms = InvoiceDueTerms.Net15;

            invoice.DueTerms = terms;
            invoice.DueDate = InvoiceCalculator.ResolveDueDate(invoice.InvoiceDate, terms, null);
        }

        /// <summary>
        /// Copies the financial and content shape of the previous sent invoice.
        ///
        /// The TAX RATE comes across as a snapshot, not as a fresh lookup: the previous invoice
        /// recorded the rate it was issued under, and re-reading today's default would silently
        /// re-rate a recurring bill the client has been paying for a year.
        /// </summary>
        private static void CloneFrom(CommercialInvoice source, CommercialInvoice target)
        {
            target.DiscountType = source.DiscountType;
            target.DiscountValue = source.DiscountValue;
            target.TaxType = source.TaxType;
            target.TaxRate = source.TaxRate;
            target.PaymentMethod = source.PaymentMethod;
            target.CustomerNote = source.CustomerNote;
            target.PoNumber = source.PoNumber;
            target.ClientReference = source.ClientReference;

            if (string.IsNullOrWhiteSpace(target.ServiceAddress))
                target.ServiceAddress = source.ServiceAddress;

            foreach (var item in source.Items.OrderBy(i => i.SortOrder))
            {
                target.Items.Add(new CommercialInvoiceItem
                {
                    Description = item.Description,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    Amount = InvoiceCalculator.LineAmount(item.Quantity, item.UnitPrice),
                    SortOrder = item.SortOrder,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        /// <summary>
        /// The first invoice for a contract, built from the contract's own pricing.
        ///
        /// One line per scheduled visit in the period, because that is what the contract quotes -
        /// a price "per scheduled visit" billed monthly is four or five lines, and collapsing them
        /// into one would make the invoice impossible to check against the agreement.
        ///
        /// The contract's PRICE MODE decides the invoice's tax mode, so the two documents cannot
        /// disagree about whether the agreed figure already includes tax.
        /// </summary>
        private static void ApplyContractPricing(
            ContractSnapshot snapshot,
            BillingSettings settings,
            CommercialInvoice invoice,
            CreateNextInvoiceResultDto result)
        {
            var pricing = snapshot.Pricing;
            var inclusive = pricing.PriceMode == ContractPriceMode.TaxInclusive;

            invoice.TaxType = inclusive ? InvoiceTaxType.Included : InvoiceTaxType.Added;
            invoice.TaxRate = pricing.SalesTaxRatePercent > 0m
                ? pricing.SalesTaxRatePercent
                : settings.DefaultTaxRate;
            invoice.DiscountType = InvoiceDiscountType.None;
            invoice.PaymentMethod = InvoicePaymentMethod.AchBankTransfer;
            invoice.CustomerNote = settings.DefaultCustomerNote;

            // Tax-inclusive lines carry the total; tax-added lines carry the pre-tax fee. Either
            // way the LINE is what the contract quotes, and InvoiceCalculator does the split.
            var unitPrice = inclusive ? pricing.TotalPrice : pricing.PreTaxPrice;

            if (unitPrice <= 0m)
            {
                result.Warnings.Add(
                    "This contract has no price recorded, so the invoice has been created with no "
                    + "line items. Add them before sending.");
                return;
            }

            var dates = InvoiceService.ParseServiceDates(invoice.ServiceDatesJson);
            var visits = Math.Max(1, dates.Count);

            invoice.Items.Add(new CommercialInvoiceItem
            {
                Description = BuildContractLineDescription(snapshot, dates),
                Quantity = visits,
                UnitPrice = unitPrice,
                Amount = InvoiceCalculator.LineAmount(visits, unitPrice),
                SortOrder = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        private static string BuildContractLineDescription(ContractSnapshot snapshot, List<DateTime> dates)
        {
            var premises = string.IsNullOrWhiteSpace(snapshot.PremisesType)
                ? "premises"
                : snapshot.PremisesType;

            var text = $"Commercial cleaning services - {premises}";

            var period = ServiceDateFormatter.Describe(dates, null, null);
            if (period.HasValue) text += $" ({period.Text})";

            return text.Length > 500 ? text[..500] : text;
        }

        /// <summary>
        /// Says so when the contract now prices differently from the invoice that was cloned.
        ///
        /// NON-BLOCKING AND NON-DESTRUCTIVE, which is the whole design: the admin is about to open
        /// the draft, and a warning they can act on is better than an adjustment made for them. A
        /// contract edited for a future period must not silently re-price the invoice the client
        /// has been paying, and a contract whose price genuinely went up must not be quietly
        /// under-billed either.
        /// </summary>
        private static void AddPricingDriftWarning(
            ContractSnapshot snapshot,
            CommercialInvoice invoice,
            CommercialInvoice previous,
            CreateNextInvoiceResultDto result)
        {
            var pricing = snapshot.Pricing;
            var inclusive = pricing.PriceMode == ContractPriceMode.TaxInclusive;
            var contractUnit = inclusive ? pricing.TotalPrice : pricing.PreTaxPrice;

            if (contractUnit <= 0m) return;

            // Compared per LINE, not against the invoice total: a monthly invoice covering four
            // visits legitimately totals four times the contract's per-visit figure, and comparing
            // totals would fire on every single correctly-generated invoice.
            var matches = invoice.Items.Any(i => i.UnitPrice == contractUnit);
            if (matches) return;

            var rates = invoice.Items
                .Select(i => i.UnitPrice.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US")))
                .Distinct()
                .ToList();

            result.Warnings.Add(
                $"Current contract pricing differs from the previous invoice. The contract quotes "
                + $"{contractUnit:C} per visit; this draft was copied from {previous.InvoiceNumber} at "
                + $"{string.Join(", ", rates)}. Nothing has been changed - review the line items "
                + "before sending.");
        }

        /// <summary>
        /// Says so when the contract's tax treatment has moved away from the invoice that was
        /// cloned.
        ///
        /// SAME SHAPE AND SAME REASONING AS THE PRICE WARNING, and for the same reason it is a
        /// warning rather than a correction: the previous invoice recorded the rate it was ISSUED
        /// under, and silently re-rating a recurring bill the client has been paying for a year is
        /// worse than telling somebody the two now differ. A rate change is also a legal/filing
        /// decision, not a data-entry one.
        ///
        /// Two things can drift and both are reported: the MODE (does the agreed figure already
        /// include tax?) and the RATE. The mode matters more — getting it wrong moves the total,
        /// not just the split — so it is named first.
        /// </summary>
        private static void AddTaxDriftWarning(
            ContractSnapshot snapshot,
            CommercialInvoice invoice,
            CommercialInvoice previous,
            CreateNextInvoiceResultDto result)
        {
            var contractMode = snapshot.Pricing.PriceMode == ContractPriceMode.TaxInclusive
                ? InvoiceTaxType.Included
                : InvoiceTaxType.Added;

            var contractRate = snapshot.Pricing.SalesTaxRatePercent;

            if (invoice.TaxType != contractMode)
            {
                result.Warnings.Add(
                    $"Current contract tax treatment is \"{DescribeTaxType(contractMode)}\"; this draft "
                    + $"was copied from {previous.InvoiceNumber} as \"{DescribeTaxType(invoice.TaxType)}\". "
                    + "Nothing has been changed - review before sending.");
            }

            // A contract with no rate recorded says nothing about the rate, so it cannot disagree
            // with one. Only a real, differing figure is worth an admin's attention.
            if (contractRate > 0m && invoice.TaxRate.HasValue && invoice.TaxRate.Value != contractRate)
            {
                result.Warnings.Add(
                    $"Current contract tax rate: {contractRate:0.000}%. "
                    + $"Cloned invoice tax rate: {invoice.TaxRate.Value:0.000}%. "
                    + "Review before sending.");
            }
        }

        private static string DescribeTaxType(InvoiceTaxType type) => type switch
        {
            InvoiceTaxType.Included => "tax included in the price",
            InvoiceTaxType.Added => "tax added on top",
            _ => "tax exempt"
        };

        /// <summary>
        /// Freezes the generation warnings onto the draft so the editor can show them however the
        /// admin got there. See the call site for why returning them was not enough.
        ///
        /// Serialized rather than concatenated so the UI keeps one banner per warning; an empty
        /// list clears the column rather than storing "[]", because "no warnings" and "warnings
        /// nobody has looked at" should not render the same.
        /// </summary>
        private static void PersistDraftWarnings(
            CommercialInvoice invoice, CreateNextInvoiceResultDto result)
        {
            invoice.DraftWarningsJson = result.Warnings.Count == 0
                ? null
                : System.Text.Json.JsonSerializer.Serialize(result.Warnings);
        }

        /// <summary>
        /// Inserts the draft, allocating a number and retrying on the unique index.
        ///
        /// The same retry every other create path uses: the number check and the insert are not
        /// atomic, so the index is the last word and the retry is what turns its verdict back into
        /// a successful create.
        /// </summary>
        private async Task SaveWithNumberAsync(CommercialInvoice invoice)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                invoice.InvoiceNumber = await _numbers.NextInvoiceNumberAsync();
                invoice.PublicToken = InvoiceNumberService.NewPublicToken();

                _context.CommercialInvoices.Add(invoice);

                try
                {
                    await _context.SaveChangesAsync();
                    return;
                }
                catch (DbUpdateException ex) when (attempt < 2 && IsUniqueViolation(ex))
                {
                    _logger.LogWarning(ex,
                        "Invoice number {Number} collided on insert; retrying.", invoice.InvoiceNumber);

                    _context.Entry(invoice).State = EntityState.Detached;
                    foreach (var item in invoice.Items) _context.Entry(item).State = EntityState.Detached;
                }
            }

            throw new InvoiceWorkflowException("Could not allocate an invoice number. Please try again.");
        }

        private static bool IsUniqueViolation(DbUpdateException ex) =>
            ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;
    }
}

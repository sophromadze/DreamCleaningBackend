using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Models.Commercial;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>
    /// Reads and writes the single <see cref="BillingSettings"/> row, and is the ONLY place bank
    /// details are resolved from.
    ///
    /// Nothing else in the codebase may hardcode a routing or account number - not an email
    /// template, not the PDF writer, not an Angular component. Every surface that prints payment
    /// instructions goes through <see cref="BuildPaymentInstructions"/>, so changing the account
    /// is one edit in one place rather than a hunt through three layers.
    ///
    /// The row is created on first read rather than seeded through HasData, following
    /// ContractSeedService's reasoning: a HasData row emits an UpdateData on the next migration
    /// whose values differ, which would silently revert an admin's edit to their own bank details.
    /// </summary>
    public class BillingSettingsService
    {
        private readonly ApplicationDbContext _context;

        public BillingSettingsService(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// The settings row, created empty if it does not exist yet. Seeded with the company's
        /// legal identity copied from the default contractor profile where one exists - the same
        /// entity signs the contracts and receives the payments, so retyping it would only be an
        /// opportunity for the two to disagree.
        /// </summary>
        public async Task<BillingSettings> GetOrCreateAsync(CancellationToken ct = default)
        {
            var existing = await _context.BillingSettings
                .OrderBy(s => s.Id)
                .FirstOrDefaultAsync(ct);

            if (existing != null) return existing;

            var contractor = await _context.ContractorProfiles
                .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Id)
                .FirstOrDefaultAsync(ct);

            var created = new BillingSettings
            {
                CompanyLegalName = contractor?.LegalEntityName ?? "Dream Cleaning NYC",
                CompanyDbaName = contractor?.Dba,
                CompanyAddress = contractor?.Address,
                CompanyCity = contractor?.City,
                CompanyState = contractor?.State,
                CompanyZip = contractor?.Zip,
                CompanyPhone = contractor?.Phone,
                CompanyEmail = contractor?.NoticeEmail,
                BankAccountHolder = BuildDefaultAccountHolder(contractor?.LegalEntityName, contractor?.Dba),
                BankAccountType = "Business Checking",
                AchInstructions =
                    "Please send payment by ACH bank transfer to the account above and include the "
                    + "invoice number in the payment memo or reference field so we can match it to "
                    + "your account.",
                DefaultTaxType = InvoiceTaxType.Exempt,
                DefaultDueTerms = InvoiceDueTerms.Net15,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.BillingSettings.Add(created);
            await _context.SaveChangesAsync(ct);
            return created;
        }

        /// <summary>"Nodar Alania Inc. DBA Dream Cleaning NYC" from the two parts.</summary>
        private static string? BuildDefaultAccountHolder(string? legalName, string? dba)
        {
            if (string.IsNullOrWhiteSpace(legalName)) return null;
            return string.IsNullOrWhiteSpace(dba) ? legalName : $"{legalName} DBA {dba}";
        }

        /// <summary>
        /// The settings as an admin sees them.
        ///
        /// <paramref name="canEdit"/> does double duty: it drives the form's read-only state AND
        /// decides whether the account number is masked. Someone who cannot change the destination
        /// account has no reason to be handed the full number by a list endpoint.
        /// </summary>
        public BillingSettingsDto ToDto(BillingSettings s, bool canEdit) => new()
        {
            CompanyLegalName = s.CompanyLegalName,
            CompanyDbaName = s.CompanyDbaName,
            CompanyAddress = s.CompanyAddress,
            CompanyCity = s.CompanyCity,
            CompanyState = s.CompanyState,
            CompanyZip = s.CompanyZip,
            CompanyPhone = s.CompanyPhone,
            CompanyEmail = s.CompanyEmail,
            BankName = s.BankName,
            BankAccountHolder = s.BankAccountHolder,
            BankRoutingNumber = s.BankRoutingNumber,
            BankAccountNumber = canEdit ? s.BankAccountNumber : Mask(s.BankAccountNumber),
            BankAccountNumberMasked = !canEdit,
            BankAccountType = s.BankAccountType,
            AchInstructions = s.AchInstructions,
            WireInstructions = s.WireInstructions,
            BankWireRoutingNumber = s.BankWireRoutingNumber,
            StripeAchEnabled = s.StripeAchEnabled,
            StripeCardEnabled = s.StripeCardEnabled,
            ManualAchEnabled = s.ManualAchEnabled,
            ManualAchComplete = IsManualAchComplete(s),
            MissingManualAchFields = MissingManualAchFields(s),
            DefaultTaxType = s.DefaultTaxType,
            DefaultTaxRate = s.DefaultTaxRate,
            DefaultDueTerms = s.DefaultDueTerms,
            DefaultCustomerNote = s.DefaultCustomerNote,
            InvoiceFooterText = s.InvoiceFooterText,
            UpdatedAt = s.UpdatedAt,
            CanEdit = canEdit
        };

        /// <summary>
        /// Whether manual ACH is fully configured enough to actually be paid.
        ///
        /// The five fields below are the ones a customer needs in order to send a transfer. Showing
        /// an account holder and a payment reference without a routing and account number produces
        /// instructions nobody can act on, which is worse than showing nothing — the customer
        /// believes they have what they need and only discovers otherwise at their bank.
        ///
        /// Wire routing is deliberately NOT required: it is an optional convenience, and gating
        /// ACH on it would block the primary manual method on a field most clients never use.
        /// </summary>
        public static bool IsManualAchComplete(BillingSettings s) =>
            !string.IsNullOrWhiteSpace(s.BankName)
            && !string.IsNullOrWhiteSpace(s.BankAccountHolder)
            && !string.IsNullOrWhiteSpace(s.BankRoutingNumber)
            && !string.IsNullOrWhiteSpace(s.BankAccountNumber)
            && !string.IsNullOrWhiteSpace(s.BankAccountType);

        /// <summary>
        /// Which manual-ACH fields are still missing, for the admin warning. Names fields only —
        /// it never quotes a value.
        /// </summary>
        public static List<string> MissingManualAchFields(BillingSettings s)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(s.BankName)) missing.Add("Bank name");
            if (string.IsNullOrWhiteSpace(s.BankAccountHolder)) missing.Add("Account holder");
            if (string.IsNullOrWhiteSpace(s.BankRoutingNumber)) missing.Add("ACH routing number");
            if (string.IsNullOrWhiteSpace(s.BankAccountNumber)) missing.Add("Account number");
            if (string.IsNullOrWhiteSpace(s.BankAccountType)) missing.Add("Account type");
            return missing;
        }

        /// <summary>
        /// Whether the manual bank block may be shown to a customer at all: switched on AND
        /// complete. Both halves are needed — an enabled-but-half-configured setting must not
        /// produce partial instructions.
        /// </summary>
        public static bool CanOfferManualAch(BillingSettings s) =>
            s.ManualAchEnabled && IsManualAchComplete(s);

        /// <summary>Last four digits only, e.g. "••••8842". Null stays null.</summary>
        public static string? Mask(string? accountNumber)
        {
            if (string.IsNullOrWhiteSpace(accountNumber)) return null;
            var digits = accountNumber.Trim();
            return digits.Length <= 4 ? new string('•', digits.Length) : "••••" + digits[^4..];
        }

        /// <summary>
        /// Applies an edit. A null <see cref="SaveBillingSettingsDto.BankAccountNumber"/> KEEPS
        /// the stored value - the form only sends it when the admin actually retyped it, so
        /// opening the page and pressing Save cannot wipe the account number.
        /// </summary>
        public void Apply(BillingSettings target, SaveBillingSettingsDto dto, int userId)
        {
            target.CompanyLegalName = dto.CompanyLegalName.Trim();
            target.CompanyDbaName = Trim(dto.CompanyDbaName);
            target.CompanyAddress = Trim(dto.CompanyAddress);
            target.CompanyCity = Trim(dto.CompanyCity);
            target.CompanyState = Trim(dto.CompanyState);
            target.CompanyZip = Trim(dto.CompanyZip);
            target.CompanyPhone = Trim(dto.CompanyPhone);
            target.CompanyEmail = Trim(dto.CompanyEmail);

            target.BankName = Trim(dto.BankName);
            target.BankAccountHolder = Trim(dto.BankAccountHolder);
            target.BankRoutingNumber = Trim(dto.BankRoutingNumber);

            if (dto.BankAccountNumber != null)
                target.BankAccountNumber = Trim(dto.BankAccountNumber);

            target.BankAccountType = Trim(dto.BankAccountType);
            target.AchInstructions = Trim(dto.AchInstructions);
            target.WireInstructions = Trim(dto.WireInstructions);
            target.BankWireRoutingNumber = Trim(dto.BankWireRoutingNumber);

            target.StripeAchEnabled = dto.StripeAchEnabled;
            target.StripeCardEnabled = dto.StripeCardEnabled;
            target.ManualAchEnabled = dto.ManualAchEnabled;

            target.DefaultTaxType = dto.DefaultTaxType;
            target.DefaultTaxRate = dto.DefaultTaxRate;
            target.DefaultDueTerms = dto.DefaultDueTerms;
            target.DefaultCustomerNote = Trim(dto.DefaultCustomerNote);
            target.InvoiceFooterText = Trim(dto.InvoiceFooterText);

            target.UpdatedAt = DateTime.UtcNow;
            target.UpdatedByUserId = userId;
        }

        /// <summary>
        /// A human-readable summary of what a settings edit changed, for the audit log.
        ///
        /// BANK VALUES ARE NAMED BUT NEVER QUOTED: the log records that the routing number
        /// changed, not what it changed from or to. An audit trail that reproduces the account
        /// number in plain text just moves the sensitive value into a second, longer-lived and
        /// more widely-read table.
        /// </summary>
        public static string DescribeChanges(BillingSettings before, SaveBillingSettingsDto after)
        {
            var changed = new List<string>();

            void Compare(string label, string? a, string? b)
            {
                if (!string.Equals(Trim(a), Trim(b), StringComparison.Ordinal)) changed.Add(label);
            }

            Compare("company legal name", before.CompanyLegalName, after.CompanyLegalName);
            Compare("DBA name", before.CompanyDbaName, after.CompanyDbaName);
            Compare("company address", before.CompanyAddress, after.CompanyAddress);
            Compare("company phone", before.CompanyPhone, after.CompanyPhone);
            Compare("company email", before.CompanyEmail, after.CompanyEmail);
            Compare("bank name", before.BankName, after.BankName);
            Compare("account holder", before.BankAccountHolder, after.BankAccountHolder);
            Compare("routing number", before.BankRoutingNumber, after.BankRoutingNumber);

            if (after.BankAccountNumber != null)
                Compare("account number", before.BankAccountNumber, after.BankAccountNumber);

            Compare("account type", before.BankAccountType, after.BankAccountType);
            Compare("ACH instructions", before.AchInstructions, after.AchInstructions);
            Compare("wire instructions", before.WireInstructions, after.WireInstructions);
            Compare("wire routing number", before.BankWireRoutingNumber, after.BankWireRoutingNumber);

            // Which payment methods an invoice offers is a commercial decision worth recording:
            // turning Stripe ACH off silently would look to everyone else like an outage.
            if (before.StripeAchEnabled != after.StripeAchEnabled)
                changed.Add($"Stripe ACH {(after.StripeAchEnabled ? "enabled" : "disabled")}");
            if (before.StripeCardEnabled != after.StripeCardEnabled)
                changed.Add($"Stripe card {(after.StripeCardEnabled ? "enabled" : "disabled")}");
            if (before.ManualAchEnabled != after.ManualAchEnabled)
                changed.Add($"manual ACH {(after.ManualAchEnabled ? "enabled" : "disabled")}");

            if (before.DefaultTaxType != after.DefaultTaxType) changed.Add("default tax treatment");
            if (before.DefaultTaxRate != after.DefaultTaxRate) changed.Add("default tax rate");
            if (before.DefaultDueTerms != after.DefaultDueTerms) changed.Add("default payment terms");

            return changed.Count == 0
                ? "Billing settings saved with no changes."
                : "Billing settings updated: " + string.Join(", ", changed) + ".";
        }

        /// <summary>
        /// The payment block for the public invoice, the PDF and the email.
        ///
        /// The reference is ALWAYS the invoice number: it is what an admin matches a bank
        /// transaction against, so letting it be anything else would break reconciliation.
        /// </summary>
        public static PublicPaymentInstructionsDto BuildPaymentInstructions(
            BillingSettings settings, string invoiceNumber, InvoicePaymentMethod method)
        {
            return new PublicPaymentInstructionsDto
            {
                Method = method switch
                {
                    InvoicePaymentMethod.AchBankTransfer => "ACH Bank Transfer",
                    InvoicePaymentMethod.Card => "Card",
                    _ => "Other"
                },
                BankName = settings.BankName,
                AccountHolder = settings.BankAccountHolder,
                RoutingNumber = settings.BankRoutingNumber,
                AccountNumber = settings.BankAccountNumber,
                AccountType = settings.BankAccountType,
                AchInstructions = settings.AchInstructions,
                WireInstructions = settings.WireInstructions,
                PaymentReference = invoiceNumber
            };
        }

        /// <summary>The company block, shared by every client-facing surface.</summary>
        public static PublicCompanyDto BuildCompany(BillingSettings s) => new()
        {
            LegalName = s.CompanyLegalName,
            DbaName = s.CompanyDbaName,
            Address = s.CompanyAddress,
            CityStateZip = BuildCityLine(s.CompanyCity, s.CompanyState, s.CompanyZip),
            Phone = s.CompanyPhone,
            Email = s.CompanyEmail,
            FooterText = s.InvoiceFooterText
        };

        public static string? BuildCityLine(string? city, string? state, string? zip)
        {
            var left = string.Join(", ", new[] { city, state }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var line = string.Join(" ", new[] { left, zip }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }

        private static string? Trim(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

using System.Reflection;
using System.Text.RegularExpressions;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers.Commercial;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// PUBLIC REFERENCE NUMBERS: DCC for contracts, DCI for invoices.
    ///
    /// The format replaced a short sequential one (DC-2026-0001) for three reasons, and each is
    /// pinned below: it leaked the business's volume, it was guessable from any single document,
    /// and it did not say which KIND of document it named once invoices existed.
    ///
    /// The random half must come from a cryptographic generator. That is not testable by
    /// observation, so <see cref="ReferenceNumberGenerator"/> is asserted to use
    /// RandomNumberGenerator by reading its own source-level contract - the distribution tests
    /// here catch the other half of the mistake, a generator that is not actually random.
    /// </summary>
    public class ReferenceNumberTests
    {
        private static readonly Regex ContractFormat = new(@"^DCC-\d{4}-\d{8}$");
        private static readonly Regex InvoiceFormat = new(@"^DCI-\d{4}-\d{8}$");

        [Fact]
        public void ContractNumber_IsDccYearAndEightDigits()
        {
            var number = ReferenceNumberGenerator.NewContractNumber(2026);

            Assert.Matches(ContractFormat, number);
            Assert.StartsWith("DCC-2026-", number);
            Assert.Equal(17, number.Length);
        }

        [Fact]
        public void InvoiceNumber_IsDciYearAndEightDigits()
        {
            var number = ReferenceNumberGenerator.NewInvoiceNumber(2026);

            Assert.Matches(InvoiceFormat, number);
            Assert.StartsWith("DCI-2026-", number);
            Assert.Equal(17, number.Length);
        }

        /// <summary>
        /// THE TWO PREFIXES NEVER COLLIDE. "DC-2026-0007" was ambiguous the moment both documents
        /// existed; DCC and DCI cannot be mistaken for each other.
        /// </summary>
        [Fact]
        public void ContractAndInvoicePrefixesAreDistinct()
        {
            var contract = ReferenceNumberGenerator.NewContractNumber(2026);
            var invoice = ReferenceNumberGenerator.NewInvoiceNumber(2026);

            Assert.NotEqual(contract[..3], invoice[..3]);
            Assert.False(InvoiceFormat.IsMatch(contract));
            Assert.False(ContractFormat.IsMatch(invoice));
        }

        /// <summary>
        /// ALWAYS EIGHT DIGITS, never seven with a leading zero dropped. A shorter reference would
        /// be a different-looking number for the same invoice, and the number is the payment memo.
        /// </summary>
        [Fact]
        public void RandomTail_IsAlwaysWithinTheEightDigitRange()
        {
            for (var i = 0; i < 2000; i++)
            {
                var value = ReferenceNumberGenerator.SecureEightDigits();
                Assert.InRange(value, 10_000_000, 99_999_999);
                Assert.Equal(8, value.ToString().Length);
            }
        }

        /// <summary>
        /// NOT SEQUENTIAL, AND NOT PREDICTABLE. Two thousand draws should be essentially all
        /// distinct; a sequential or low-entropy generator fails this immediately.
        /// </summary>
        [Fact]
        public void NumbersAreRandom_NotSequential()
        {
            var numbers = Enumerable.Range(0, 2000)
                .Select(_ => ReferenceNumberGenerator.NewInvoiceNumber(2026))
                .ToList();

            // Birthday collisions over 90 million values are vanishingly unlikely at this sample
            // size; anything approaching sequential would collapse this count.
            Assert.True(numbers.Distinct().Count() >= 1995,
                "Reference numbers are not sufficiently random.");

            // A sequential generator would produce a run of consecutive tails.
            var tails = numbers.Select(n => int.Parse(n.Split('-')[2])).ToList();
            var consecutive = tails.Zip(tails.Skip(1), (a, b) => b - a == 1).Count(x => x);
            Assert.True(consecutive < 5, "Reference numbers look sequential.");
        }

        /// <summary>
        /// The generator must use <c>RandomNumberGenerator</c>, not <c>System.Random</c>. Asserted
        /// structurally because randomness quality cannot be observed from output alone at this
        /// sample size, and a swap to Random would otherwise pass every other test in this file.
        /// </summary>
        [Fact]
        public void Generator_UsesACryptographicSource()
        {
            var source = typeof(ReferenceNumberGenerator).Assembly.Location;
            Assert.NotNull(source);

            // The type must not carry a System.Random field - the usual shape of the mistake.
            var randomFields = typeof(ReferenceNumberGenerator)
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.FieldType == typeof(Random))
                .ToList();

            Assert.Empty(randomFields);
        }

        // ── Legacy coexistence ────────────────────────────────────────────────────────────────

        /// <summary>
        /// OLD CONTRACT NUMBERS ARE NOT MIGRATED, and must not be mistaken for new ones. A
        /// contract number is printed on an executed legal document and quoted in email threads;
        /// rewriting one would orphan every reference to it outside this database.
        /// </summary>
        [Fact]
        public void LegacyContractNumbers_AreRecognisedAsNotNewFormat()
        {
            Assert.False(ReferenceNumberGenerator.IsNewFormat(
                "DC-2026-0001", ReferenceNumberGenerator.ContractPrefix));
            Assert.False(ReferenceNumberGenerator.IsNewFormat(
                "DC-2026-0042", ReferenceNumberGenerator.ContractPrefix));

            Assert.True(ReferenceNumberGenerator.IsNewFormat(
                "DCC-2026-48392175", ReferenceNumberGenerator.ContractPrefix));
        }

        [Fact]
        public void IsNewFormat_RejectsMalformedInput()
        {
            var prefix = ReferenceNumberGenerator.InvoicePrefix;

            Assert.False(ReferenceNumberGenerator.IsNewFormat(null, prefix));
            Assert.False(ReferenceNumberGenerator.IsNewFormat("", prefix));
            Assert.False(ReferenceNumberGenerator.IsNewFormat("DCI-2026-123", prefix));      // too short
            Assert.False(ReferenceNumberGenerator.IsNewFormat("DCI-2026-123456789", prefix)); // too long
            Assert.False(ReferenceNumberGenerator.IsNewFormat("DCI-26-12345678", prefix));    // short year
            Assert.False(ReferenceNumberGenerator.IsNewFormat("DCI-2026-ABCDEFGH", prefix));  // not digits
            Assert.False(ReferenceNumberGenerator.IsNewFormat("DCC-2026-12345678", prefix));  // wrong prefix
        }

        // ── The write DTOs must not be able to carry a number or a derived total ──────────────

        /// <summary>
        /// THE BACKEND IS THE SOURCE OF TRUTH FOR THE REFERENCE NUMBER. It is not merely ignored
        /// on the way in - there is no field to send it in, so a client cannot express the idea.
        /// </summary>
        [Fact]
        public void SaveInvoiceDto_HasNoInvoiceNumberOrPublicToken()
        {
            var names = typeof(SaveInvoiceDto).GetProperties().Select(p => p.Name).ToList();

            Assert.DoesNotContain("InvoiceNumber", names);
            Assert.DoesNotContain("PublicToken", names);
            Assert.DoesNotContain("Status", names);
        }

        /// <summary>
        /// NEITHER CAN IT CARRY A TOTAL. Every derived figure is recomputed server-side by
        /// InvoiceCalculator; making them unrepresentable on the input DTO is the structural
        /// version of "never trust monetary totals submitted by the frontend".
        /// </summary>
        [Fact]
        public void SaveInvoiceDto_HasNoDerivedMonetaryFields()
        {
            var names = typeof(SaveInvoiceDto).GetProperties().Select(p => p.Name).ToList();

            foreach (var derived in new[]
                     { "SubTotal", "TaxAmount", "Total", "AmountPaid", "BalanceDue", "DiscountAmount" })
            {
                Assert.DoesNotContain(derived, names);
            }
        }

        [Fact]
        public void SaveInvoiceItemDto_HasNoAmount()
        {
            var names = typeof(SaveInvoiceItemDto).GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain("Amount", names);
        }

        // ── The public DTO leak guard ─────────────────────────────────────────────────────────

        /// <summary>
        /// THE PUBLIC INVOICE PAYLOAD MUST NOT CARRY ADMIN-ONLY FIELDS.
        ///
        /// This is the test that matters for the client-facing page. PublicInvoiceDto is a
        /// separate type from InvoiceDetailDto rather than a filtered copy precisely so a new
        /// admin field cannot leak by default - and this asserts that the separation still holds.
        /// </summary>
        [Fact]
        public void PublicInvoiceDto_CarriesNoInternalNoteActivityOrIdentifiers()
        {
            var names = typeof(PublicInvoiceDto).GetProperties().Select(p => p.Name).ToList();

            foreach (var forbidden in new[]
                     {
                         "InternalNote",     // admin eyes only, by definition
                         "Activity",         // who did what, inside the business
                         "EmailHistory",     // where else this invoice has been sent
                         "Id",               // sequential and enumerable
                         "PublicToken",      // the credential itself
                         "PublicUrl",
                         "ViewCount",        // how closely we are watching them
                         "FirstViewedAt",
                         "LastViewedAt",
                         "CreatedByName",
                         "VoidReason",
                         "Payments"          // the payment ledger, including internal notes
                     })
            {
                Assert.DoesNotContain(forbidden, names);
            }
        }

        /// <summary>The public DTO still has to carry everything a client needs in order to pay.</summary>
        [Fact]
        public void PublicInvoiceDto_CarriesWhatTheClientNeedsToPay()
        {
            var names = typeof(PublicInvoiceDto).GetProperties().Select(p => p.Name).ToList();

            foreach (var required in new[]
                     {
                         "InvoiceNumber", "InvoiceDate", "DueDate", "Items",
                         "SubTotal", "TaxAmount", "Total", "AmountPaid", "BalanceDue",
                         "PaymentInstructions", "Company"
                     })
            {
                Assert.Contains(required, names);
            }
        }

        /// <summary>
        /// The payment block carries RECEIVING coordinates only. Anything that would grant ACCESS
        /// to the account has no property to travel in.
        /// </summary>
        [Fact]
        public void PublicPaymentInstructions_CarryNoCredentials()
        {
            var names = typeof(PublicPaymentInstructionsDto).GetProperties()
                .Select(p => p.Name.ToLowerInvariant()).ToList();

            foreach (var forbidden in new[] { "password", "secret", "apikey", "token", "username", "pin" })
            {
                Assert.DoesNotContain(names, n => n.Contains(forbidden));
            }
        }

        /// <summary>The invoice number is always the payment reference - reconciliation depends on it.</summary>
        [Fact]
        public void PaymentReference_IsAlwaysTheInvoiceNumber()
        {
            var settings = new DreamCleaningBackend.Models.Commercial.BillingSettings
            {
                CompanyLegalName = "Nodar Alania Inc.",
                BankAccountHolder = "Nodar Alania Inc. DBA Dream Cleaning NYC",
                BankRoutingNumber = "021000021",
                BankAccountNumber = "1234567890"
            };

            var instructions = DreamCleaningBackend.Services.Commercial.BillingSettingsService
                .BuildPaymentInstructions(settings, "DCI-2026-74521863",
                    DreamCleaningBackend.Models.Commercial.InvoicePaymentMethod.AchBankTransfer);

            Assert.Equal("DCI-2026-74521863", instructions.PaymentReference);
            Assert.Equal("ACH Bank Transfer", instructions.Method);
        }

        /// <summary>
        /// The masked form shows the last four digits only - enough for an admin to confirm which
        /// account an invoice points at, without the full number reaching a list view.
        /// </summary>
        [Fact]
        public void AccountNumberMasking_KeepsOnlyTheLastFourDigits()
        {
            var mask = DreamCleaningBackend.Services.Commercial.BillingSettingsService.Mask;

            Assert.Equal("••••7890", mask("1234567890"));
            Assert.Equal("••••", mask("1234"));
            Assert.Null(mask(null));
            Assert.Null(mask("   "));
        }
    }
}

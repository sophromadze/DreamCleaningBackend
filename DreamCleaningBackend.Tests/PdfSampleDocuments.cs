using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Commercial;
using DreamCleaningBackend.Services.Contracts;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// Fixed sample data for every PDF the backend generates: the commercial invoice, the regular
/// customer invoice, the contract (preview, executed, certificate) and the two published policies.
/// No clocks and no randomness, so a page count or an extracted string means the same thing on
/// every run.
/// </summary>
internal static class PdfSampleDocuments
{
    /// <summary>What a cleaning company in NYC really receives in names, addresses and notes.</summary>
    public const string Multilingual =
        "王小明 · 김민준 · 山田花子 · محمد الحسن · דוד כהן · রহিম উদ্দিন · राहुल शर्मा · ਗੁਰਪ੍ਰੀਤ · ગીતા · தமிழ் · สมชาย · "
        + "ნინო ბერიძე · Արամ · Nguyễn Thị Hương · Ольга Петрова · Ελένη · Zoë Müller-Łukasiewicz · ✓ ★ → ☎ · 🙂 👍 · "
        + "“quoted” – € £ ¥ № ½";

    public record Sample(string Name, int ExpectedPages, string MustContain, Func<byte[]> Render);

    /// <summary>
    /// One of each document type. Contract and policy page counts follow their canonical text,
    /// so they are asserted together with the template version they were counted against.
    /// </summary>
    public static IReadOnlyList<Sample> All() => new List<Sample>
    {
        new("commercial invoice, 4 lines, every payment route", 2, "HOW TO PAY",
            () => new InvoicePdfService().Render(CommercialInvoice(), "https://dreamcleaningnyc.com/invoice/token")),
        new("commercial invoice, 70 lines", 5, "Visit 70",
            () => new InvoicePdfService().Render(CommercialInvoice(lines: 70))),
        new("commercial invoice, void", 1, "VOID",
            () => new InvoicePdfService().Render(CommercialInvoice(i => i.Status = InvoiceStatus.Void))),
        new("customer invoice, sent", 2, "AMOUNT DUE",
            () => new CustomerInvoicePdfService().Render(CustomerInvoice(), "https://dreamcleaningnyc.com/pay-invoice/token")),
        new("customer invoice, paid", 1, "Paid on",
            () => new CustomerInvoicePdfService().Render(CustomerInvoice(i => { i.Status = "Paid"; i.PaidAt = new DateTime(2026, 10, 2); }))),
        new("contract preview", 21, "DRAFT",
            () => Contract(certificate: false, draft: true)),
        new("executed contract with certificate", 22, "ELECTRONIC SIGNATURE CERTIFICATE",
            () => Contract(certificate: true, draft: false)),
        new("signature certificate", 1, "Document hash",
            () => new ContractPdfService().GenerateCertificate(ContractGeneratedDocumentRegressionTests.Dcc49303882Configuration(), Certificate())),
        new("commercial policies, complete", 8, "Cancellation",
            () => new CommercialPolicyPdfService().GenerateComplete()),
        new("commercial policies, cancellation and termination", 4, "Termination",
            () => new CommercialPolicyPdfService().GenerateCancellationAndTermination()),
    };

    public static PublicInvoiceDto CommercialInvoice(Action<PublicInvoiceDto>? tweak = null, int lines = 4)
    {
        var items = Enumerable.Range(1, lines).Select(i => new InvoiceItemDto
        {
            Id = i, SortOrder = i, Description = lines == 4 ? $"Nightly office cleaning, line {i}" : $"Visit {i}: nightly janitorial service",
            Quantity = 1m, UnitPrice = 145.83m, Amount = 145.83m
        }).ToList();
        var subTotal = items.Sum(i => i.Amount);
        var tax = Math.Round(subTotal * 0.08875m, 2, MidpointRounding.AwayFromZero);

        var invoice = new PublicInvoiceDto
        {
            InvoiceNumber = "DCI-2026-74521863", Status = InvoiceStatus.Sent,
            InvoiceDate = new DateTime(2026, 10, 1), DueDate = new DateTime(2026, 10, 31),
            ServiceDateLabel = "Service period", ServiceDateText = "September 1-30, 2026",
            ClientName = "Example Games LLC", BillingContactName = "Jamie Client",
            BillingAddress = "100 Example St, Manhattan, NY 10012", ServiceAddress = "100 Example St, 4th Floor",
            ContractNumber = "DCC-2026-49303882", PoNumber = "PO-77120",
            Items = items, SubTotal = subTotal, TaxType = InvoiceTaxType.Added, TaxRate = 8.875m,
            TaxAmount = tax, Total = subTotal + tax, BalanceDue = subTotal + tax,
            CustomerNote = "Thank you for your business!",
            PaymentInstructions = new PublicPaymentInstructionsDto
            {
                BankName = "Chase Bank", AccountHolder = "Nodar Alania Inc.", RoutingNumber = "021000021",
                AccountNumber = "000123456789", AccountType = "Business Checking",
                WireInstructions = "Domestic wires: routing 021000021.", PaymentReference = "DCI-2026-74521863"
            },
            PaymentOptions = new PublicPaymentOptionsDto
            {
                StripeAchAvailable = true, StripeCardAvailable = true, ManualAchAvailable = true,
                OtherArrangementAvailable = true, OtherArrangementNote = "Mail a check payable to Nodar Alania Inc.",
                AchProcessingFee = 5m, AchProcessingFeeLabel = "ACH Processing Fee"
            },
            Company = new PublicCompanyDto
            {
                LegalName = "Nodar Alania Inc.", DbaName = "Dream Cleaning NYC", Address = "8800 20th Ave, Apt 2B",
                CityStateZip = "Brooklyn, NY 11214", Phone = "(929) 930-1525", Email = "hello@dreamcleaningnyc.com"
            }
        };
        tweak?.Invoke(invoice);
        return invoice;
    }

    public static PublicCustomerInvoiceDto CustomerInvoice(Action<PublicCustomerInvoiceDto>? tweak = null)
    {
        var invoice = new PublicCustomerInvoiceDto
        {
            InvoiceNumber = "DCR-2026-31415926", Status = "Sent", Kind = "Full", IssuedAt = new DateTime(2026, 10, 1),
            OrderNumber = 386, ServiceTypeName = "Deep Cleaning", ServiceDate = new DateTime(2026, 10, 9), ServiceTime = "09:30",
            ServiceAddress = "245 E 63rd St, Apt 12C, New York, NY 10065",
            BilledToName = "Sarah Johnson", BilledToEmail = "sarah.johnson@example.com", BilledToPhone = "(212) 555-0147",
            SubTotal = 420m, Discounts = 42m, Credits = 25m, Tax = 33.55m, Tips = 40m, OrderTotal = 426.55m,
            Amount = 426.55m, AmountDue = 426.55m, Note = "Please bring a ladder for the kitchen cabinets.",
            CardPaymentPath = "/order/386/pay?request=91", AchAvailable = true,
            BankTransfer = new PublicPaymentInstructionsDto
            {
                BankName = "Chase Bank", AccountHolder = "Nodar Alania Inc.", RoutingNumber = "021000021",
                AccountNumber = "000123456789", AccountType = "Business Checking", PaymentReference = "DCR-2026-31415926"
            },
            Company = new PublicCompanyDto { LegalName = "Nodar Alania Inc.", DbaName = "Dream Cleaning NYC" }
        };
        tweak?.Invoke(invoice);
        return invoice;
    }

    public static byte[] Contract(bool certificate, bool draft, Action<ContractSnapshot>? tweak = null)
    {
        var snapshot = ContractGeneratedDocumentRegressionTests.Dcc49303882Configuration();
        tweak?.Invoke(snapshot);
        var rendered = ContractRenderer.Render(snapshot);
        return new ContractPdfService().GenerateDocument(
            rendered, snapshot, ContractGeneratedDocumentRegressionTests.Block(), certificate ? Certificate() : null, draft);
    }

    public static ContractCertificateData Certificate() => new()
    {
        ContractNumber = "DCC-2026-00000001", VersionNumber = 1, DocumentHash = new string('a', 64),
        EffectiveDate = new DateTime(2026, 10, 4), GeneratedAt = new DateTime(2026, 10, 3, 9, 31, 0, DateTimeKind.Utc),
        Signers = new List<ContractCertificateSigner>
        {
            new() { PartyLabel = "CONTRACTOR", EntityName = "Nodar Alania Inc.", Name = "Nodar Alania",
                    SignedAt = new DateTime(2026, 10, 2, 14, 5, 0, DateTimeKind.Utc), Method = ContractSignatureMethod.Draw, ConsentAccepted = true },
            new() { PartyLabel = "CLIENT", EntityName = "Example Games LLC", Name = "Jamie Client",
                    SignedAt = new DateTime(2026, 10, 3, 9, 30, 0, DateTimeKind.Utc), Method = ContractSignatureMethod.Type, ConsentAccepted = true }
        }
    };
}

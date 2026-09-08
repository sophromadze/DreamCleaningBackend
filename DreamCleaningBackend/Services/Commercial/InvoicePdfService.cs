using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Models.Commercial;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>
    /// Renders the invoice PDF with QuestPDF.
    ///
    /// It is built from the SAME <see cref="PublicInvoiceDto"/> the public web page renders, which
    /// is what guarantees the two cannot disagree. A PDF assembled from its own query would drift
    /// the first time a rule changed on one side only, and a client comparing the page against the
    /// attachment is exactly the person who would find it.
    ///
    /// LIGATURES ARE DISABLED, all four features, for the reason documented at length in
    /// ContractPdfService: with them on, the shaper substituted a glyph for pairs like "ti" and
    /// then dropped it, silently turning "Effective" into "Effecve" in a legal document. The same
    /// hazard applies to an invoice, where the mangled word could be a bank instruction.
    ///
    /// The palette and rules are deliberately the contract document's, so an invoice and an
    /// agreement from the same company look like they came from the same company: BrandBlue is the
    /// TEXT blue, AccentBlue the RULE blue, and they are not interchangeable.
    ///
    /// NEVER RENDERED HERE: the internal note, the activity log, database ids, and the public
    /// token. The DTO this consumes does not carry any of them, which is the structural reason
    /// rather than a rule someone has to remember.
    /// </summary>
    public class InvoicePdfService
    {
        private const float BodySize = 9.5f;
        private const float PageMargin = 46f;
        private const float BodyLineHeight = 1.4f;

        private const string BrandBlue = "#2563eb";   // text blue
        private const string AccentBlue = "#0065f3";  // rule blue
        private const string BodyInk = "#000000";
        private const string MutedInk = "#5a5a5a";
        private const string LabelInk = "#7a7a7a";
        private const string RowRule = "#dcdcdc";
        private const string PanelBg = "#f5f7fb";
        private const string BoxBorder = "#b9c6dd";
        private const string DangerInk = "#b91c1c";
        private const string PaidInk = "#15803d";

        private static readonly Lazy<byte[]?> Logo = new(LoadLogo);

        private static TextStyle BaseTextStyle => TextStyle.Default
            .FontSize(BodySize)
            .LineHeight(BodyLineHeight)
            .FontColor(BodyInk)
            .DisableFontFeature(FontFeatures.StandardLigatures)
            .DisableFontFeature("clig")
            .DisableFontFeature("dlig")
            .DisableFontFeature("hlig");

        /// <summary>
        /// "Dream-Cleaning-Invoice-DCI-2026-74521863.pdf" - the invoice number is the whole
        /// identity, so a client who saves several can tell them apart in a downloads folder.
        /// </summary>
        public static string BuildFileName(string invoiceNumber) =>
            $"Dream-Cleaning-Invoice-{invoiceNumber}.pdf";

        public byte[] Render(PublicInvoiceDto invoice, string? publicUrl = null)
        {
            QuestPDF.Settings.License = LicenseType.Community;

            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.Letter);
                    page.Margin(PageMargin);
                    page.DefaultTextStyle(BaseTextStyle);

                    page.Header().Element(e => ComposeHeader(e, invoice));
                    page.Content().Element(e => ComposeContent(e, invoice, publicUrl));
                    page.Footer().Element(e => ComposeFooter(e, invoice));
                });
            }).GeneratePdf();
        }

        // ── Header: logo and company on the left, the invoice's identity on the right ─────────

        private static void ComposeHeader(IContainer container, PublicInvoiceDto invoice)
        {
            container.Column(column =>
            {
                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(left =>
                    {
                        var logo = Logo.Value;
                        if (logo != null)
                            left.Item().Height(34f).AlignLeft().Image(logo).FitHeight();

                        left.Item().PaddingTop(logo == null ? 0 : 8)
                            .Text(invoice.Company.LegalName)
                            .FontSize(11.5f).SemiBold().FontColor(BrandBlue);

                        if (!string.IsNullOrWhiteSpace(invoice.Company.DbaName))
                            left.Item().Text($"DBA {invoice.Company.DbaName}")
                                .FontSize(8.5f).FontColor(MutedInk);

                        foreach (var line in new[]
                                 {
                                     invoice.Company.Address,
                                     invoice.Company.CityStateZip,
                                     invoice.Company.Phone,
                                     invoice.Company.Email
                                 }.Where(x => !string.IsNullOrWhiteSpace(x)))
                        {
                            left.Item().Text(line!).FontSize(8.5f).FontColor(MutedInk);
                        }
                    });

                    row.ConstantItem(200).Column(right =>
                    {
                        right.Item().AlignRight().Text("INVOICE")
                            .FontSize(20f).Bold().FontColor(BrandBlue).LetterSpacing(0.08f);

                        right.Item().PaddingTop(3).AlignRight().Text(invoice.InvoiceNumber)
                            .FontSize(11f).SemiBold();

                        right.Item().PaddingTop(6).AlignRight()
                            .Text(text =>
                            {
                                text.Span("Invoice Date  ").FontSize(8.5f).FontColor(LabelInk);
                                text.Span(Date(invoice.InvoiceDate)).FontSize(8.5f);
                            });

                        right.Item().AlignRight().Text(text =>
                        {
                            text.Span("Due Date  ").FontSize(8.5f).FontColor(LabelInk);
                            text.Span(Date(invoice.DueDate)).FontSize(8.5f).SemiBold();
                        });

                        if (!string.IsNullOrWhiteSpace(invoice.ContractNumber))
                        {
                            right.Item().AlignRight().Text(text =>
                            {
                                text.Span("Contract  ").FontSize(8.5f).FontColor(LabelInk);
                                text.Span(invoice.ContractNumber!).FontSize(8.5f);
                            });
                        }

                        if (!string.IsNullOrWhiteSpace(invoice.PoNumber))
                        {
                            right.Item().AlignRight().Text(text =>
                            {
                                text.Span("PO Number  ").FontSize(8.5f).FontColor(LabelInk);
                                text.Span(invoice.PoNumber!).FontSize(8.5f);
                            });
                        }
                    });
                });

                // QuestPDF has no BorderTopColor, so the accent is a filled bar - same technique
                // the contract document uses for its masthead rule.
                column.Item().PaddingTop(10).Height(1.5f).Background(AccentBlue);
            });
        }

        // ── Body ─────────────────────────────────────────────────────────────────────────────

        private static void ComposeContent(IContainer container, PublicInvoiceDto invoice, string? publicUrl)
        {
            container.PaddingTop(14).Column(column =>
            {
                if (invoice.Status == InvoiceStatus.Void)
                {
                    column.Item().PaddingBottom(10).Background("#fee2e2").Padding(8)
                        .Text("VOID - This invoice has been cancelled and is not payable.")
                        .FontSize(10f).Bold().FontColor(DangerInk);
                }

                column.Item().Element(e => ComposeParties(e, invoice));
                column.Item().PaddingTop(14).Element(e => ComposeLineItems(e, invoice));
                column.Item().PaddingTop(10).Element(e => ComposeTotals(e, invoice));

                // Void invoices print no payment section at all: they are not collectable, and
                // inviting a payment nobody can apply is worse than saying nothing.
                if (invoice.Status != InvoiceStatus.Void
                    && (invoice.PaymentInstructions != null || invoice.PaymentOptions.StripeAchAvailable))
                {
                    column.Item().PaddingTop(16).Element(e => ComposePaymentInstructions(e, invoice, publicUrl));
                }

                if (!string.IsNullOrWhiteSpace(invoice.CustomerNote))
                {
                    column.Item().PaddingTop(14).Column(note =>
                    {
                        note.Item().Text("NOTES").FontSize(8f).Bold()
                            .FontColor(BrandBlue).LetterSpacing(0.1f);
                        note.Item().PaddingTop(3).Text(invoice.CustomerNote!).FontSize(8.5f);
                    });
                }
            });
        }

        private static void ComposeParties(IContainer container, PublicInvoiceDto invoice)
        {
            container.Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("BILL TO").FontSize(8f).Bold()
                        .FontColor(BrandBlue).LetterSpacing(0.1f);
                    col.Item().PaddingTop(4).Text(invoice.ClientName).FontSize(10f).SemiBold();

                    if (!string.IsNullOrWhiteSpace(invoice.BillingContactName))
                        col.Item().Text($"Attn: {invoice.BillingContactName}").FontSize(8.5f);

                    if (!string.IsNullOrWhiteSpace(invoice.BillingAddress))
                        col.Item().Text(invoice.BillingAddress!).FontSize(8.5f).FontColor(MutedInk);
                });

                row.ConstantItem(16);

                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("SERVICE LOCATION").FontSize(8f).Bold()
                        .FontColor(BrandBlue).LetterSpacing(0.1f);
                    col.Item().PaddingTop(4)
                        .Text(string.IsNullOrWhiteSpace(invoice.ServiceAddress)
                            ? "Same as billing address"
                            : invoice.ServiceAddress!)
                        .FontSize(8.5f);

                    var period = FormatServicePeriod(invoice);
                    if (period != null)
                    {
                        col.Item().PaddingTop(6).Text(text =>
                        {
                            text.Span("Service Period  ").FontSize(8.5f).FontColor(LabelInk);
                            text.Span(period).FontSize(8.5f);
                        });
                    }

                    if (!string.IsNullOrWhiteSpace(invoice.ClientReference))
                    {
                        col.Item().Text(text =>
                        {
                            text.Span("Your Reference  ").FontSize(8.5f).FontColor(LabelInk);
                            text.Span(invoice.ClientReference!).FontSize(8.5f);
                        });
                    }
                });
            });
        }

        private static void ComposeLineItems(IContainer container, PublicInvoiceDto invoice)
        {
            container.Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(5);   // description
                    columns.ConstantColumn(52);  // qty
                    columns.ConstantColumn(78);  // rate
                    columns.ConstantColumn(84);  // amount
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("DESCRIPTION");
                    header.Cell().Element(HeaderCell).AlignRight().Text("QTY");
                    header.Cell().Element(HeaderCell).AlignRight().Text("RATE");
                    header.Cell().Element(HeaderCell).AlignRight().Text("AMOUNT");
                });

                foreach (var item in invoice.Items)
                {
                    table.Cell().Element(BodyCell).Text(item.Description).FontSize(9f);
                    table.Cell().Element(BodyCell).AlignRight().Text(Qty(item.Quantity)).FontSize(9f);
                    table.Cell().Element(BodyCell).AlignRight().Text(Money(item.UnitPrice)).FontSize(9f);
                    table.Cell().Element(BodyCell).AlignRight().Text(Money(item.Amount)).FontSize(9f);
                }
            });

            // Rule-only rows, no cell borders and no shaded label cell - the contract document's
            // exhibit treatment, kept so the two documents read as one family.
            static IContainer HeaderCell(IContainer c) => c
                .BorderBottom(1).BorderColor(AccentBlue)
                .PaddingVertical(5)
                .DefaultTextStyle(t => t.FontSize(8f).Bold().FontColor(BrandBlue).LetterSpacing(0.08f));

            static IContainer BodyCell(IContainer c) => c
                .BorderBottom(0.5f).BorderColor(RowRule)
                .PaddingVertical(6);
        }

        private static void ComposeTotals(IContainer container, PublicInvoiceDto invoice)
        {
            container.Row(row =>
            {
                row.RelativeItem();

                row.ConstantItem(250).Column(col =>
                {
                    Line(col, "Subtotal", Money(invoice.SubTotal));

                    if (invoice.DiscountAmount > 0m)
                        Line(col, "Discount", "-" + Money(invoice.DiscountAmount));

                    // "Included" is stated in words rather than as a number, because printing an
                    // amount next to a total it is already inside reads as an extra charge - the
                    // single most common way a tax-inclusive invoice is misread.
                    Line(col, TaxLabel(invoice), TaxValue(invoice));

                    col.Item().PaddingTop(5).Height(1).Background(AccentBlue);

                    col.Item().PaddingTop(5).Row(r =>
                    {
                        r.RelativeItem().Text("TOTAL").FontSize(10.5f).Bold().FontColor(BrandBlue);
                        r.ConstantItem(110).AlignRight()
                            .Text(Money(invoice.Total)).FontSize(10.5f).Bold().FontColor(BrandBlue);
                    });

                    // MONEY RECEIVED IS SHOWN POSITIVE. A payment row is stored positive and only
                    // a reversal is negative, so a hand-written "-" here printed a successful
                    // payment as "-$925.43" on the customer's own invoice. The Discount line above
                    // keeps its sign because that IS a reduction of what is billed; this is not.
                    if (invoice.AmountPaid != 0m)
                        Line(col, "Amount Paid", Money(invoice.AmountPaid));

                    col.Item().PaddingTop(6).Background(PanelBg).Padding(8).Row(r =>
                    {
                        r.RelativeItem().Text("BALANCE DUE")
                            .FontSize(10f).Bold()
                            .FontColor(invoice.BalanceDue <= 0m ? PaidInk : BodyInk);
                        r.ConstantItem(110).AlignRight()
                            .Text(Money(invoice.BalanceDue))
                            .FontSize(12f).Bold()
                            .FontColor(invoice.BalanceDue <= 0m ? PaidInk : BodyInk);
                    });

                    if (invoice.BalanceDue <= 0m && invoice.Status == InvoiceStatus.Paid)
                    {
                        col.Item().PaddingTop(4).AlignRight()
                            .Text("PAID" + (invoice.PaidAt.HasValue ? $" - {Date(invoice.PaidAt.Value)}" : ""))
                            .FontSize(9f).Bold().FontColor(PaidInk);
                    }
                });
            });

            static void Line(ColumnDescriptor col, string label, string value)
            {
                col.Item().PaddingVertical(2).Row(r =>
                {
                    r.RelativeItem().Text(label).FontSize(9f).FontColor(MutedInk);
                    r.ConstantItem(110).AlignRight().Text(value).FontSize(9f);
                });
            }
        }

        private static void ComposePaymentInstructions(IContainer container, PublicInvoiceDto invoice, string? publicUrl)
        {
            var pay = invoice.PaymentInstructions;
            var online = invoice.PaymentOptions.StripeAchAvailable
                         || invoice.PaymentOptions.StripeCardAvailable;

            // ShowEntire, for the same reason the contract's signature block uses it: payment
            // instructions split across a page break are the defect a client actually notices,
            // and half a routing number is worse than none.
            container.ShowEntire().Border(0.75f).BorderColor(BoxBorder).Padding(12).Column(col =>
            {
                col.Item().Height(1.5f).Background(AccentBlue);

                col.Item().PaddingTop(8).Text("HOW TO PAY")
                    .FontSize(8f).Bold().FontColor(BrandBlue).LetterSpacing(0.1f);

                // ── Online, listed first because it is the preferred route ──
                if (online)
                {
                    col.Item().PaddingTop(5).Text("Online payment").FontSize(10f).SemiBold();

                    col.Item().PaddingTop(2).Text(
                            invoice.PaymentOptions.StripeAchAvailable
                                ? "Pay securely from your bank account using the online invoice link. "
                                  + "Open the invoice and choose \"Pay from Bank\"."
                                : "Pay securely using the online invoice link.")
                        .FontSize(8.5f);

                    // A printed PDF has no clickable button, so the URL is spelled out — it is the
                    // only way a reader holding paper can reach the payment page.
                    //
                    // Passed in as a render parameter rather than carried on PublicInvoiceDto:
                    // the URL contains the invoice's secret token, and that DTO is deliberately
                    // free of it. A field there would be the same leak under a different name.
                    if (!string.IsNullOrWhiteSpace(publicUrl))
                        col.Item().PaddingTop(3).Text(publicUrl!).FontSize(8f).FontColor(BrandBlue);
                }

                // ── Manual bank transfer ──
                // Rendered only when the block is complete: PaymentInstructions is null unless
                // manual ACH is enabled AND every field a customer needs is filled in.
                if (pay != null)
                {
                    if (online)
                        col.Item().PaddingTop(9).Height(0.5f).Background(RowRule);

                    col.Item().PaddingTop(online ? 9 : 5)
                        .Text("Manual ACH bank transfer").FontSize(10f).SemiBold();

                    col.Item().PaddingTop(6).Row(row =>
                    {
                        row.RelativeItem().Column(left =>
                        {
                            Field(left, "Account Holder", pay.AccountHolder);
                            Field(left, "Bank", pay.BankName);
                            Field(left, "Account Type", pay.AccountType);
                        });

                        row.ConstantItem(16);

                        row.RelativeItem().Column(right =>
                        {
                            Field(right, "Routing Number", pay.RoutingNumber);
                            Field(right, "Account Number", pay.AccountNumber);
                            Field(right, "Payment Reference", pay.PaymentReference);
                        });
                    });

                    col.Item().PaddingTop(8).Text(
                            $"Please include invoice number {invoice.InvoiceNumber} in the payment "
                            + "memo or reference field.")
                        .FontSize(8.5f).SemiBold();

                    if (!string.IsNullOrWhiteSpace(pay.AchInstructions))
                        col.Item().PaddingTop(4).Text(pay.AchInstructions!).FontSize(8f).FontColor(MutedInk);

                    if (!string.IsNullOrWhiteSpace(pay.WireInstructions))
                    {
                        col.Item().PaddingTop(6).Text("Wire transfers").FontSize(8f).Bold();
                        col.Item().Text(pay.WireInstructions!).FontSize(8f).FontColor(MutedInk);
                    }
                }
            });

            static void Field(ColumnDescriptor col, string label, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                col.Item().PaddingBottom(3).Column(c =>
                {
                    c.Item().Text(label).FontSize(7.5f).FontColor(LabelInk).LetterSpacing(0.06f);
                    c.Item().Text(value!).FontSize(9f).SemiBold();
                });
            }
        }

        private static void ComposeFooter(IContainer container, PublicInvoiceDto invoice)
        {
            container.PaddingTop(8).BorderTop(0.5f).BorderColor(RowRule).PaddingTop(5).Column(col =>
            {
                if (!string.IsNullOrWhiteSpace(invoice.Company.FooterText))
                    col.Item().AlignCenter().Text(invoice.Company.FooterText!)
                        .FontSize(7.5f).FontColor(LabelInk);

                col.Item().AlignCenter().Text(text =>
                {
                    text.DefaultTextStyle(t => t.FontSize(7.5f).FontColor(LabelInk));
                    text.Span($"{invoice.InvoiceNumber}  ·  Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        }

        // ── Formatting ───────────────────────────────────────────────────────────────────────

        private static string TaxLabel(PublicInvoiceDto invoice) => invoice.TaxType switch
        {
            InvoiceTaxType.Included => "Sales Tax (included)",
            InvoiceTaxType.Added when invoice.TaxRate is > 0m => $"Sales Tax ({Rate(invoice.TaxRate!.Value)}%)",
            InvoiceTaxType.Added => "Sales Tax",
            _ => "Sales Tax"
        };

        private static string TaxValue(PublicInvoiceDto invoice) => invoice.TaxType switch
        {
            InvoiceTaxType.Exempt => "Exempt",
            InvoiceTaxType.Included => "Included",
            _ => Money(invoice.TaxAmount)
        };

        internal static string? FormatServicePeriod(PublicInvoiceDto invoice)
        {
            if (invoice.ServiceStartDate == null && invoice.ServiceEndDate == null) return null;

            var start = invoice.ServiceStartDate ?? invoice.ServiceEndDate!.Value;
            var end = invoice.ServiceEndDate ?? invoice.ServiceStartDate!.Value;

            if (start.Date == end.Date) return Date(start);

            // A range inside one month collapses the repeated month name: "September 1-30, 2026".
            if (start.Year == end.Year && start.Month == end.Month)
                return $"{start:MMMM} {start.Day}-{end.Day}, {start.Year}";

            return $"{Date(start)} - {Date(end)}";
        }

        private static string Date(DateTime value) => value.ToString("MMM d, yyyy");

        private static string Money(decimal value) => value.ToString("C2",
            System.Globalization.CultureInfo.GetCultureInfo("en-US"));

        /// <summary>Whole quantities print bare; fractional ones keep two places.</summary>
        private static string Qty(decimal value) =>
            value == Math.Floor(value) ? ((long)value).ToString() : value.ToString("0.##");

        private static string Rate(decimal value) => value.ToString("0.###");

        private static byte[]? LoadLogo()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "contract-logo.png");
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}

using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.DTOs;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// The REGULAR customer invoice (DCR-…) as a PDF — attached to the invoice email and served by
    /// the public invoice page's download button (2026-09). Before this the page's "Print or save
    /// as PDF" called <c>window.print()</c>, which printed the website around the invoice: header,
    /// footer, chat bubble and all.
    ///
    /// Built from the SAME <see cref="PublicCustomerInvoiceDto"/> the public page renders, so the
    /// attachment and the page cannot disagree — the rule the commercial <c>InvoicePdfService</c>
    /// follows, whose look (logo, masthead rule, brand blue, rule-only tables) this copies so every
    /// invoice from the company reads as one family. Ligatures are disabled for the reason given
    /// there: the shaper dropped glyph pairs such as "ti" and silently mangled words.
    /// </summary>
    public class CustomerInvoicePdfService
    {
        private const float BodySize = 9.5f;
        private const float PageMargin = 46f;
        private const float BodyLineHeight = 1.4f;

        // Brand palette — the same values as the commercial invoice and the site's --primary-color.
        private const string BrandColor = "#2563eb";
        private const string AccentColor = "#0065f3";
        private const string BodyInk = "#000000";
        private const string MutedInk = "#5a5a5a";
        private const string LabelInk = "#7a7a7a";
        private const string RowRule = "#dcdcdc";
        private const string PanelBg = "#f5f7fb";
        private const string BoxBorder = "#b9c6dd";
        private const string DangerInk = "#b91c1c";
        private const string PaidInk = "#15803d";

        private const string CompanyFallbackName = "Dream Cleaning";

        private static readonly Lazy<byte[]?> Logo = new(LoadLogo);

        private static TextStyle BaseTextStyle => TextStyle.Default
            .FontSize(BodySize)
            .LineHeight(BodyLineHeight)
            .FontColor(BodyInk)
            .DisableFontFeature(FontFeatures.StandardLigatures)
            .DisableFontFeature("clig")
            .DisableFontFeature("dlig")
            .DisableFontFeature("hlig");

        public static string BuildFileName(string invoiceNumber) =>
            $"Dream-Cleaning-Invoice-{invoiceNumber}.pdf";

        /// <param name="publicUrl">Spelled out under "How to pay" — paper has no button. Passed in
        /// rather than carried on the DTO because it contains the invoice's secret token.</param>
        public byte[] Render(PublicCustomerInvoiceDto invoice, string? publicUrl = null)
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
            }).GeneratePdfExclusive();
        }

        private static void ComposeHeader(IContainer container, PublicCustomerInvoiceDto invoice)
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

                        var company = invoice.Company;
                        left.Item().PaddingTop(logo == null ? 0 : 8)
                            .Text(company?.PrimaryName ?? CompanyFallbackName)
                            .FontSize(11.5f).SemiBold().FontColor(BrandColor);

                        if (!string.IsNullOrWhiteSpace(company?.SecondaryName))
                            left.Item().Text(company!.SecondaryName!).FontSize(8.5f).FontColor(MutedInk);

                        foreach (var line in new[] { company?.Address, company?.CityStateZip, company?.Phone, company?.Email }
                                     .Where(x => !string.IsNullOrWhiteSpace(x)))
                        {
                            left.Item().Text(line!).FontSize(8.5f).FontColor(MutedInk);
                        }
                    });

                    row.ConstantItem(200).Column(right =>
                    {
                        right.Item().AlignRight().Text("INVOICE")
                            .FontSize(20f).Bold().FontColor(BrandColor).LetterSpacing(0.08f);
                        right.Item().PaddingTop(3).AlignRight().Text(invoice.InvoiceNumber)
                            .FontSize(11f).SemiBold();
                        right.Item().PaddingTop(6).AlignRight().Text(text =>
                        {
                            text.Span("Invoice Date  ").FontSize(8.5f).FontColor(LabelInk);
                            text.Span(Date(invoice.IssuedAt)).FontSize(8.5f);
                        });
                        right.Item().AlignRight().Text(text =>
                        {
                            text.Span("Order  ").FontSize(8.5f).FontColor(LabelInk);
                            text.Span($"#{invoice.OrderNumber}").FontSize(8.5f);
                        });
                    });
                });

                column.Item().PaddingTop(10).Height(1.5f).Background(AccentColor);
            });
        }

        private static void ComposeContent(IContainer container, PublicCustomerInvoiceDto invoice, string? publicUrl)
        {
            container.PaddingTop(14).Column(column =>
            {
                if (invoice.Status is "Void" or "Cancelled")
                {
                    column.Item().PaddingBottom(10).Background("#fee2e2").Padding(8)
                        .Text(invoice.Status == "Void"
                            ? "VOID - This invoice has been cancelled and is not payable."
                            : "CANCELLED - The cleaning on this invoice was cancelled, so there is nothing to pay.")
                        .FontSize(10f).Bold().FontColor(DangerInk);
                }

                column.Item().Element(e => ComposeParties(e, invoice));
                column.Item().PaddingTop(14).Element(e => ComposeLineItems(e, invoice));
                column.Item().PaddingTop(10).Element(e => ComposeTotals(e, invoice));

                if (invoice.Status is "Sent" or "NotSent")
                    column.Item().PaddingTop(16).Element(e => ComposeHowToPay(e, invoice, publicUrl));

                if (!string.IsNullOrWhiteSpace(invoice.Note))
                {
                    column.Item().PaddingTop(14).Column(note =>
                    {
                        note.Item().Text("NOTES").FontSize(8f).Bold().FontColor(BrandColor).LetterSpacing(0.1f);
                        note.Item().PaddingTop(3).Text(invoice.Note!).FontSize(8.5f);
                    });
                }
            });
        }

        private static void ComposeParties(IContainer container, PublicCustomerInvoiceDto invoice)
        {
            container.Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("BILL TO").FontSize(8f).Bold().FontColor(BrandColor).LetterSpacing(0.1f);
                    col.Item().PaddingTop(4).Text(invoice.BilledToName).FontSize(10f).SemiBold();
                    if (!string.IsNullOrWhiteSpace(invoice.BilledToEmail))
                        col.Item().Text(invoice.BilledToEmail!).FontSize(8.5f).FontColor(MutedInk);
                    if (!string.IsNullOrWhiteSpace(invoice.BilledToPhone))
                        col.Item().Text(invoice.BilledToPhone!).FontSize(8.5f).FontColor(MutedInk);
                });

                row.ConstantItem(16);

                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("SERVICE LOCATION").FontSize(8f).Bold().FontColor(BrandColor).LetterSpacing(0.1f);
                    col.Item().PaddingTop(4).Text(string.IsNullOrWhiteSpace(invoice.ServiceAddress) ? "—" : invoice.ServiceAddress)
                        .FontSize(8.5f);
                    col.Item().PaddingTop(6).Text(text =>
                    {
                        text.Span("Service date  ").FontSize(8.5f).FontColor(LabelInk);
                        text.Span($"{invoice.ServiceDate:dddd, MMM d, yyyy} at {FormatTime(invoice.ServiceTime)}").FontSize(8.5f);
                    });
                });
            });
        }

        private static void ComposeLineItems(IContainer container, PublicCustomerInvoiceDto invoice)
        {
            container.Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(5);
                    columns.ConstantColumn(96);
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("DESCRIPTION");
                    header.Cell().Element(HeaderCell).AlignRight().Text("AMOUNT");
                });

                table.Cell().Element(BodyCell).Text($"{invoice.ServiceTypeName} — order #{invoice.OrderNumber}").FontSize(9f);
                table.Cell().Element(BodyCell).AlignRight().Text(Money(invoice.SubTotal)).FontSize(9f);

                if (invoice.Tips > 0m)
                {
                    table.Cell().Element(BodyCell).Text("Tips").FontSize(9f);
                    table.Cell().Element(BodyCell).AlignRight().Text(Money(invoice.Tips)).FontSize(9f);
                }
            });

            static IContainer HeaderCell(IContainer c) => c
                .BorderBottom(1).BorderColor(AccentColor)
                .PaddingVertical(5)
                .DefaultTextStyle(t => t.FontSize(8f).Bold().FontColor(BrandColor).LetterSpacing(0.08f));

            static IContainer BodyCell(IContainer c) => c
                .BorderBottom(0.5f).BorderColor(RowRule)
                .PaddingVertical(6);
        }

        private static void ComposeTotals(IContainer container, PublicCustomerInvoiceDto invoice)
        {
            var isPaid = invoice.Status == "Paid";

            container.Row(row =>
            {
                row.RelativeItem();

                row.ConstantItem(260).Column(col =>
                {
                    Line(col, "Subtotal", Money(invoice.SubTotal));
                    if (invoice.Discounts > 0m) Line(col, "Discounts", "-" + Money(invoice.Discounts));
                    Line(col, "Sales Tax", Money(invoice.Tax));
                    if (invoice.Tips > 0m) Line(col, "Tips", Money(invoice.Tips));
                    if (invoice.Credits > 0m) Line(col, "Gift card / credits", "-" + Money(invoice.Credits));

                    col.Item().PaddingTop(5).Height(1).Background(AccentColor);
                    col.Item().PaddingTop(5).Row(r =>
                    {
                        r.RelativeItem().Text("ORDER TOTAL").FontSize(10.5f).Bold().FontColor(BrandColor);
                        r.ConstantItem(110).AlignRight().Text(Money(invoice.OrderTotal)).FontSize(10.5f).Bold().FontColor(BrandColor);
                    });

                    if (invoice.OrderAmountPaid > 0m && !isPaid)
                        Line(col, "Already paid", Money(invoice.OrderAmountPaid));

                    if (invoice.Kind == "Split")
                        Line(col, "This invoice (part of your order)", Money(invoice.Amount));
                    else if (invoice.Kind == "Additional")
                        Line(col, "This invoice (added after your order was updated)", Money(invoice.Amount));

                    var due = isPaid ? 0m : invoice.AmountDue;
                    col.Item().PaddingTop(6).Background(PanelBg).Padding(8).Row(r =>
                    {
                        r.RelativeItem().Text(isPaid ? "PAID" : "AMOUNT DUE")
                            .FontSize(10f).Bold().FontColor(isPaid ? PaidInk : BodyInk);
                        r.ConstantItem(110).AlignRight().Text(Money(due))
                            .FontSize(12f).Bold().FontColor(isPaid ? PaidInk : BodyInk);
                    });

                    if (isPaid && invoice.PaidAt.HasValue)
                        col.Item().PaddingTop(4).AlignRight().Text($"Paid on {Date(invoice.PaidAt.Value)}")
                            .FontSize(9f).Bold().FontColor(PaidInk);
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

        private static void ComposeHowToPay(IContainer container, PublicCustomerInvoiceDto invoice, string? publicUrl)
        {
            var bank = invoice.BankTransfer;
            var card = !string.IsNullOrWhiteSpace(invoice.CardPaymentPath);
            var achOnline = invoice.AchAvailable;
            var online = card || achOnline;
            if (!online && bank == null) return;

            container.ShowEntire().Border(0.75f).BorderColor(BoxBorder).Padding(12).Column(col =>
            {
                col.Item().Height(1.5f).Background(AccentColor);
                col.Item().PaddingTop(8).Text("HOW TO PAY").FontSize(8f).Bold().FontColor(BrandColor).LetterSpacing(0.1f);

                if (online)
                {
                    // Both online options live on the same invoice page; paper names each one.
                    col.Item().PaddingTop(5).Text("Pay online").FontSize(10f).SemiBold();
                    col.Item().PaddingTop(2).Text("Open the invoice online and choose:").FontSize(8.5f);
                    if (card)
                        col.Item().PaddingTop(2).Text("•  \"Pay by card\" — card payment, confirmed immediately.").FontSize(8.5f);
                    if (achOnline)
                        col.Item().PaddingTop(2).Text("•  \"Pay from your bank online\" — directly from your US bank account (ACH), clears in 3–5 business days.").FontSize(8.5f);
                    if (!string.IsNullOrWhiteSpace(publicUrl))
                        col.Item().PaddingTop(3).Text(publicUrl!).FontSize(8f).FontColor(BrandColor);
                }

                if (bank != null)
                {
                    if (online) col.Item().PaddingTop(9).Height(0.5f).Background(RowRule);

                    col.Item().PaddingTop(online ? 9 : 5).Text("Bank transfer").FontSize(10f).SemiBold();
                    col.Item().PaddingTop(6).Row(row =>
                    {
                        row.RelativeItem().Column(left =>
                        {
                            Field(left, "Account Holder", bank.AccountHolder);
                            Field(left, "Bank", bank.BankName);
                            Field(left, "Account Type", bank.AccountType);
                        });
                        row.ConstantItem(16);
                        row.RelativeItem().Column(right =>
                        {
                            Field(right, "Routing Number", bank.RoutingNumber);
                            Field(right, "Account Number", bank.AccountNumber);
                            Field(right, "Payment Reference", bank.PaymentReference);
                        });
                    });
                    col.Item().PaddingTop(8)
                        .Text($"Please include invoice number {invoice.InvoiceNumber} in the payment memo or reference field.")
                        .FontSize(8.5f).SemiBold();
                    if (!string.IsNullOrWhiteSpace(bank.AchInstructions))
                        col.Item().PaddingTop(4).Text(bank.AchInstructions!).FontSize(8f).FontColor(MutedInk);
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

        private static void ComposeFooter(IContainer container, PublicCustomerInvoiceDto invoice)
        {
            container.PaddingTop(8).BorderTop(0.5f).BorderColor(RowRule).PaddingTop(5).Column(col =>
            {
                if (!string.IsNullOrWhiteSpace(invoice.Company?.FooterText))
                    col.Item().AlignCenter().Text(invoice.Company!.FooterText!).FontSize(7.5f).FontColor(LabelInk);

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

        private static string Date(DateTime value) => value.ToString("MMM d, yyyy");

        private static string Money(decimal value) => value.ToString("C2",
            System.Globalization.CultureInfo.GetCultureInfo("en-US"));

        private static string FormatTime(string hhmm)
        {
            var parts = (hhmm ?? "").Split(':');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return hhmm ?? "";
            return $"{(h % 12 == 0 ? 12 : h % 12)}:{m:00} {(h >= 12 ? "PM" : "AM")}";
        }

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

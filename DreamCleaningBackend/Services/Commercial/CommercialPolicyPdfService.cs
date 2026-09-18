using DreamCleaningBackend.Helpers.Commercial;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>
    /// Renders the two downloadable policy documents - the complete Commercial Cleaning Policies
    /// and the standalone Cancellation and Termination Policy - from
    /// <see cref="CommercialPolicyDocument"/>.
    ///
    /// IT RENDERS THE SAME OBJECT THE WEB PAGE RENDERS. There is no separate PDF copy of the
    /// wording: the page reads the frontend mirror of that class, this writer reads the class
    /// itself, and <c>CommercialPolicyContentTests</c> asserts the two are identical. A visitor
    /// who reads the page and then downloads the PDF cannot be shown two different cancellation
    /// windows.
    ///
    /// The document is TEXT, not a picture of a page: QuestPDF lays out real glyphs, so the result
    /// is selectable, searchable and readable by a screen reader.
    ///
    /// LIGATURES ARE DISABLED for the same proven reason they are in <c>ContractPdfService</c> -
    /// with standard ligatures on, the shaper substituted a single glyph for pairs like "ti" and
    /// then dropped it, so extracted text read "noce" for "notice". No font family is named, so
    /// QuestPDF's embedded Lato is used on the Linux VPS and the Windows dev box alike.
    ///
    /// The palette and measurements are deliberately the contract PDF's, so a client who receives
    /// an agreement and a policy document recognises them as the same company's paperwork. They
    /// are duplicated rather than shared because the constants in <c>ContractPdfService</c> were
    /// measured off the approved contract reference document and are that file's to change.
    /// </summary>
    public class CommercialPolicyPdfService
    {
        private const float BodySize = 10f;
        private const float PageMargin = 61f;
        private const float BodyLineHeight = 1.55f;

        // Two blues, as in the contract PDF: BrandBlue is text (title, section headings),
        // AccentBlue is rules (the masthead underline, a note's accent bar).
        private const string BrandBlue = "#2563eb";
        private const string AccentBlue = "#0065f3";

        private const string BodyInk = "#000000";
        private const string DisplayGrey = "#4a4a4a";
        private const string HeaderGrey = "#7a7a7a";
        private const string HeaderRule = "#d8d8d8";
        private const string NoteBackground = "#f4f7fd";

        private static readonly Lazy<byte[]?> Logo = new(LoadLogo);

        // The two documents are compile-time constants, so their bytes are too. Rendered once per
        // process rather than per download: the content cannot change without a deployment, and a
        // public URL is exactly the sort of thing that gets hit by a crawler in a loop.
        private readonly Lazy<byte[]> _complete;
        private readonly Lazy<byte[]> _cancellation;

        public CommercialPolicyPdfService()
        {
            _complete = new Lazy<byte[]>(
                () => Generate(CommercialPolicyDocument.BuildComplete()),
                LazyThreadSafetyMode.ExecutionAndPublication);
            _cancellation = new Lazy<byte[]>(
                () => Generate(CommercialPolicyDocument.BuildCancellationAndTermination()),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>The complete Commercial Cleaning Policies.</summary>
        public byte[] GenerateComplete() => _complete.Value;

        /// <summary>The standalone Cancellation and Termination Policy.</summary>
        public byte[] GenerateCancellationAndTermination() => _cancellation.Value;

        private static TextStyle BaseTextStyle => TextStyle.Default
            .FontSize(BodySize)
            .LineHeight(BodyLineHeight)
            .FontColor(BodyInk)
            .DisableFontFeature(FontFeatures.StandardLigatures)
            .DisableFontFeature("clig")
            .DisableFontFeature("dlig")
            .DisableFontFeature("hlig");

        public byte[] Generate(PolicyDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);

            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.Letter);
                    page.MarginHorizontal(PageMargin);
                    page.MarginTop(35);
                    page.MarginBottom(45);
                    page.DefaultTextStyle(BaseTextStyle);

                    ComposeHeader(page, document);
                    ComposeFooter(page, document);

                    page.Content().PaddingVertical(6).Column(column =>
                    {
                        column.Spacing(4);
                        ComposeMasthead(column, document);
                        ComposeIntro(column, document);
                        ComposeContents(column, document);
                        ComposeSections(column, document);
                        ComposeClosing(column, document);
                    });
                });
            }).GeneratePdf();
        }

        // ── page chrome ────────────────────────────────────────────────────────

        private static void ComposeHeader(PageDescriptor page, PolicyDocument document)
        {
            page.Header().Column(header =>
            {
                header.Item().Row(row =>
                {
                    row.RelativeItem().Text(document.Title.ToUpperInvariant())
                        .FontSize(7).FontColor(HeaderGrey).LetterSpacing(0.18f);
                    row.RelativeItem().AlignRight().Text(document.LegalIdentity.ToUpperInvariant())
                        .FontSize(7).FontColor(HeaderGrey).LetterSpacing(0.18f);
                });
                header.Item().PaddingTop(4).PaddingBottom(24)
                    .LineHorizontal(0.75f).LineColor(HeaderRule);
            });
        }

        /// <summary>
        /// Page numbers, and the version beside them. A policy document is quoted back at us
        /// months later, so the page a client is reading from has to say which version it is from
        /// even when it has been printed and separated from its cover.
        /// </summary>
        private static void ComposeFooter(PageDescriptor page, PolicyDocument document)
        {
            page.Footer().AlignCenter().Text(text =>
            {
                text.DefaultTextStyle(x => x.FontSize(7).FontColor(HeaderGrey).LetterSpacing(0.08f));
                text.Span($"Version {document.Version}  ·  Effective {LongDate(document.EffectiveDate)}  ·  Page ");
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        }

        // ── body ───────────────────────────────────────────────────────────────

        private static void ComposeMasthead(ColumnDescriptor column, PolicyDocument document)
        {
            var logo = Logo.Value;
            if (logo != null)
            {
                column.Item().AlignCenter().Height(42).Image(logo).FitHeight();
            }

            column.Item().PaddingTop(10).AlignCenter().Text(document.Title.ToUpperInvariant())
                .FontSize(17).Bold().FontColor(BrandBlue).LetterSpacing(0.13f);

            column.Item().PaddingTop(3).AlignCenter().Text(document.Subtitle.ToUpperInvariant())
                .FontSize(9.5f).FontColor(DisplayGrey).LetterSpacing(0.275f);

            column.Item().PaddingTop(10).LineHorizontal(0.75f).LineColor(AccentBlue);

            column.Item().PaddingTop(8).PaddingBottom(4).Row(row =>
            {
                row.RelativeItem().Text(document.LegalIdentity)
                    .FontSize(8.5f).FontColor(DisplayGrey);
                row.RelativeItem().AlignRight()
                    .Text($"Version {document.Version}  ·  Effective {LongDate(document.EffectiveDate)}")
                    .FontSize(8.5f).FontColor(DisplayGrey);
            });
        }

        private static void ComposeIntro(ColumnDescriptor column, PolicyDocument document)
        {
            foreach (var block in document.Intro)
            {
                ComposeBlock(column, block);
            }
        }

        /// <summary>
        /// A contents list. On screen the page has a sticky table of contents; on paper the same
        /// job is done here, because a fifteen-page policy with no contents is one a client stops
        /// reading at the part they were looking for.
        /// </summary>
        private static void ComposeContents(ColumnDescriptor column, PolicyDocument document)
        {
            column.Item().ShowEntire().PaddingTop(12).Column(contents =>
            {
                contents.Item().Text("CONTENTS")
                    .FontSize(8.5f).Bold().FontColor(AccentBlue).LetterSpacing(0.18f);
                contents.Item().PaddingTop(6).PaddingBottom(2)
                    .LineHorizontal(0.75f).LineColor(HeaderRule);

                foreach (var section in document.Sections)
                {
                    contents.Item().PaddingTop(3).Row(row =>
                    {
                        row.ConstantItem(26).Text(section.Number + ".")
                            .FontSize(9).FontColor(DisplayGrey);
                        row.RelativeItem().Text(section.Title).FontSize(9);
                    });
                }
            });
        }

        private static void ComposeSections(ColumnDescriptor column, PolicyDocument document)
        {
            foreach (var section in document.Sections)
            {
                // Kept with what follows it: a section heading alone at the foot of a page is the
                // most common ugly break in a document this long.
                column.Item().ShowEntire().PaddingTop(14).Text($"{section.Number}. {section.Title}")
                    .FontSize(10.5f).Bold().FontColor(BrandBlue).LetterSpacing(0.05f);

                foreach (var block in section.Blocks)
                {
                    ComposeBlock(column, block);
                }
            }
        }

        private static void ComposeBlock(ColumnDescriptor column, PolicyBlock block)
        {
            switch (block.Kind)
            {
                case PolicyBlockKind.Bullet:
                    column.Item().PaddingTop(2).PaddingLeft(18).Text($"•  {block.Text}");
                    break;

                case PolicyBlockKind.Note:
                    // The qualifier that must not be read past - "this cap is not an automatic
                    // charge", "the signed agreement governs". Drawn as a tinted panel with an
                    // accent bar drawn as a filled column rather than a border: QuestPDF has no
                    // per-side border colour, which is the same constraint ContractPdfService
                    // works around for the signature box.
                    column.Item().ShowEntire().PaddingTop(8).PaddingBottom(2).Row(row =>
                    {
                        row.ConstantItem(2).Background(AccentBlue);
                        row.RelativeItem().Background(NoteBackground).Padding(10)
                            .Text(block.Text).FontSize(9.5f);
                    });
                    break;

                default:
                    column.Item().PaddingTop(6).Text(block.Text).Justify();
                    break;
            }
        }

        /// <summary>
        /// The closing identity block. Repeated at the end because a downloaded PDF is forwarded,
        /// printed and read without the page it came from.
        /// </summary>
        private static void ComposeClosing(ColumnDescriptor column, PolicyDocument document)
        {
            column.Item().ShowEntire().PaddingTop(18).Column(closing =>
            {
                closing.Item().LineHorizontal(0.75f).LineColor(AccentBlue);
                closing.Item().PaddingTop(8).Text(document.LegalIdentity)
                    .FontSize(9.5f).Bold().FontColor(BrandBlue);
                closing.Item().PaddingTop(2).Text(
                        $"{CommercialPolicyDocument.ContactEmail}  ·  "
                        + $"{CommercialPolicyDocument.ContactPhone}  ·  "
                        + CommercialPolicyDocument.Website)
                    .FontSize(9).FontColor(DisplayGrey);
                closing.Item().PaddingTop(6).Text(
                        $"{document.Title} · Version {document.Version} · "
                        + $"Effective {LongDate(document.EffectiveDate)}")
                    .FontSize(8.5f).FontColor(DisplayGrey);
            });
        }

        /// <summary>
        /// Delegates to <see cref="CommercialPolicyDocument.FormatEffectiveDate"/> so the PDF, the
        /// page and the agreement's POLICY_EFFECTIVE_DATE token all spell the date one way.
        /// </summary>
        private static string LongDate(string isoDate) =>
            CommercialPolicyDocument.FormatEffectiveDate(isoDate);

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

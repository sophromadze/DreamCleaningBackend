using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models.Contracts;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>Everything the Electronic Signature Certificate page prints.</summary>
    public class ContractCertificateData
    {
        public string ContractNumber { get; set; } = string.Empty;
        public int VersionNumber { get; set; }
        public string DocumentHash { get; set; } = string.Empty;
        public DateTime? EffectiveDate { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public List<ContractCertificateSigner> Signers { get; set; } = new();
    }

    public class ContractCertificateSigner
    {
        public string PartyLabel { get; set; } = string.Empty;
        public string EntityName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Email { get; set; }
        public DateTime? SignedAt { get; set; }
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
        public ContractSignatureMethod? Method { get; set; }
        public string? DocumentHashAtSigning { get; set; }
        public bool ConsentAccepted { get; set; }
    }

    /// <summary>
    /// Renders contract PDFs with QuestPDF: the admin preview, the fully executed document, and
    /// the signature certificate. All three consume the SAME block list the HTML view renders, so
    /// what an admin previews, what a client reads in the browser and what gets executed cannot
    /// diverge.
    ///
    /// LIGATURES ARE DISABLED, and that is not cosmetic. With standard ligatures on, the shaper
    /// substituted a single glyph for letter pairs like "ti" and then dropped it, so the rendered
    /// document silently read "Effecve" for "Effective", "noce" for "notice", "cerficate" for
    /// "certificate". Nothing errored — the text simply lost characters, in a legal document.
    /// The default text style therefore turns off StandardLigatures and ContextualAlternates, and
    /// ContractPdfTextIntegrityTests extracts the text back out of a generated PDF and diffs it
    /// against the source to prove nothing is being dropped.
    ///
    /// No font family is named on purpose — QuestPDF embeds Lato, and naming a system font would
    /// render differently (or fail) on the Linux VPS than on a Windows dev box.
    /// </summary>
    public class ContractPdfService
    {
        // Every measurement below was taken off the approved reference document
        // ("Contract review and notes.pdf") rather than chosen: page margin, type sizes, tracking,
        // rule weights and the two distinct blues. Change one only against that reference.
        private const float BodySize = 10f;
        private const float PageMargin = 61f;
        private const float BodyLineHeight = 1.55f;

        // TWO blues, and they are not interchangeable. BrandBlue is the TEXT blue - title, section
        // headings, party labels, the Exhibit B header row. AccentBlue is the RULE blue - the
        // masthead and exhibit underlines, the signature-box accent bars, the exhibit eyebrows.
        private const string BrandBlue = "#2563eb";
        private const string AccentBlue = "#0065f3";

        private const string BodyInk = "#000000";       // body prose
        private const string DisplayGrey = "#4a4a4a";   // the masthead subtitle
        private const string CaptionGrey = "#5a5a5a";   // signature-box captions
        private const string HeaderGrey = "#7a7a7a";    // the running header
        private const string HeaderRule = "#d8d8d8";    // the hairline under the running header
        private const string RowRule = "#dcdcdc";       // exhibit row separators
        private const string BoxBorder = "#b9c6dd";     // signature-box border

        private static readonly Lazy<byte[]?> Logo = new(LoadLogo);

        /// <summary>
        /// The default text style for every contract page. Ligature substitution off — see the
        /// class summary; this single call is what stops characters disappearing.
        /// </summary>
        private static TextStyle BaseTextStyle => TextStyle.Default
            .FontSize(BodySize)
            .LineHeight(BodyLineHeight)
            .FontColor(BodyInk)
            .DisableFontFeature(FontFeatures.StandardLigatures)
            .DisableFontFeature("clig")   // contextual ligatures
            .DisableFontFeature("dlig")   // discretionary ligatures
            .DisableFontFeature("hlig");  // historical ligatures

        /// <summary>Preview or executed document. Pass <paramref name="certificate"/> to append
        /// the Electronic Signature Certificate as a final page.</summary>
        public byte[] GenerateDocument(
            RenderedContract rendered,
            ContractSnapshot snapshot,
            ContractSignatureBlockDto signatureBlock,
            ContractCertificateData? certificate,
            bool draftWatermark)
        {
            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    ConfigurePage(page, snapshot, draftWatermark);
                    page.Content().PaddingVertical(6).Column(column =>
                    {
                        column.Spacing(4);
                        ComposeCoverMasthead(column, snapshot, rendered);
                        ComposeBody(column, rendered, signatureBlock);
                    });
                });

                if (certificate != null)
                {
                    container.Page(page =>
                    {
                        ConfigurePage(page, snapshot, false);
                        page.Content().PaddingVertical(6).Column(column => ComposeCertificate(column, certificate));
                    });
                }
            });

            return document.GeneratePdf();
        }

        /// <summary>The certificate on its own, stored alongside the executed document.</summary>
        public byte[] GenerateCertificate(ContractSnapshot snapshot, ContractCertificateData certificate)
        {
            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    ConfigurePage(page, snapshot, false);
                    page.Content().PaddingVertical(6).Column(column => ComposeCertificate(column, certificate));
                });
            }).GeneratePdf();
        }

        // ── page chrome ────────────────────────────────────────────────────────

        private static void ConfigurePage(PageDescriptor page, ContractSnapshot snapshot, bool draftWatermark)
        {
            page.Size(PageSizes.Letter);
            page.MarginHorizontal(PageMargin);
            // The running header lives INSIDE the top margin in the reference, above the text
            // block, so the top margin is the header's strip and the header itself pads the gap
            // down to the body. A uniform 61pt margin pushed the header 27pt too low.
            page.MarginTop(35);
            page.MarginBottom(45);
            page.DefaultTextStyle(BaseTextStyle);

            var contractorName = (string.IsNullOrWhiteSpace(snapshot.Contractor.Dba)
                ? snapshot.Contractor.LegalEntityName
                : $"{snapshot.Contractor.LegalEntityName} d/b/a {snapshot.Contractor.Dba}").ToUpperInvariant();

            // Running header, on EVERY page including the first. It used to be suppressed on the
            // cover, but the reference document carries it there too — the masthead sits below it,
            // not instead of it.
            page.Header().Column(header =>
            {
                header.Item().Row(row =>
                {
                    row.RelativeItem().Text("MASTER SERVICE AGREEMENT")
                        .FontSize(7).FontColor(HeaderGrey).LetterSpacing(0.18f);
                    row.RelativeItem().AlignRight().Text(contractorName)
                        .FontSize(7).FontColor(HeaderGrey).LetterSpacing(0.18f);
                });
                header.Item().PaddingTop(4).PaddingBottom(24)
                    .LineHorizontal(0.75f).LineColor(HeaderRule);
            });

            // The reference carries no footer at all. A page number is kept anyway — this is a
            // sixteen-page executed agreement, and "page 9 is missing" is unanswerable without one
            // — but everything else the old footer printed (contract number, version, a second
            // hairline) is gone, because that information is on the cover and in the certificate.
            // Deliberately no initials line: signing happens once, in the signature block, and an
            // initials box invites a second, unrecorded act of signing.
            page.Footer().AlignCenter().Text(text =>
            {
                text.DefaultTextStyle(x => x.FontSize(7).FontColor(HeaderGrey).LetterSpacing(0.08f));
                if (draftWatermark) text.Span("DRAFT — NOT EXECUTED    ");
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        }

        /// <summary>
        /// The cover masthead: logo, blue title, grey subtitle, blue rule. Rendered here rather
        /// than from the template body so the branding is identical on every contract regardless
        /// of what a SuperAdmin edits into the agreement text.
        /// </summary>
        private static void ComposeCoverMasthead(
            ColumnDescriptor column, ContractSnapshot snapshot, RenderedContract rendered)
        {
            var logo = Logo.Value;
            if (logo != null)
            {
                column.Item().AlignCenter().Height(42).Image(logo).FitHeight();
            }

            // The title lines come from the template body's own "# " title blocks, so a renamed
            // agreement still says the right thing here. Falls back to the standard wording.
            var titles = rendered.Blocks
                .Where(b => b.Kind == ContractBlockKind.Title)
                .Select(b => b.Text)
                .ToList();

            var mainTitle = titles.ElementAtOrDefault(0) ?? "MASTER SERVICE AGREEMENT";
            var subTitle = titles.ElementAtOrDefault(1) ?? "COMMERCIAL CLEANING SERVICES";

            column.Item().PaddingTop(10).AlignCenter().Text(mainTitle.ToUpperInvariant())
                .FontSize(17).Bold().FontColor(BrandBlue).LetterSpacing(0.13f);

            column.Item().PaddingTop(3).AlignCenter().Text(subTitle.ToUpperInvariant())
                .FontSize(9.5f).FontColor(DisplayGrey).LetterSpacing(0.275f);

            // AccentBlue and a hairline, not BrandBlue at 1.5pt: in the reference this rule is the
            // same weight as every other rule on the page and reads as a divider, not a banner.
            column.Item().PaddingTop(10).PaddingBottom(12)
                .LineHorizontal(0.75f).LineColor(AccentBlue);
        }

        // ── body ───────────────────────────────────────────────────────────────

        /// <summary>Label-column widths, measured off the reference document.</summary>
        private const float ExhibitLabelWidth = 186f;   // Exhibit B's schedule
        private const float DefinitionLabelWidth = 166f; // Exhibit A's metadata rows

        /// <summary>Matches the "EXHIBIT A" / "EXHIBIT B" eyebrow headings.</summary>
        private static bool IsExhibitEyebrow(ContractBlock block) =>
            block.Kind == ContractBlockKind.Heading
            && System.Text.RegularExpressions.Regex.IsMatch(
                block.Text.Trim(), @"^EXHIBIT\s+[A-Z]$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Walks the block list, carrying the context a single block cannot see for itself: which
        /// exhibit it sits under, whether it opens a run of schedule rows, and whether a heading is
        /// an exhibit eyebrow or the exhibit title that follows one.
        ///
        /// Reading it off the headings, rather than adding a field to <see cref="ContractBlock"/>,
        /// keeps the block list, the HTML view and the canonical text the document hash is taken
        /// over completely untouched — this is a PDF layout concern only.
        /// </summary>
        private static void ComposeBody(
            ColumnDescriptor column, RenderedContract rendered, ContractSignatureBlockDto signatures)
        {
            var blocks = rendered.Blocks;
            string? exhibit = null;

            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                var role = HeadingRole.Section;

                if (block.Kind == ContractBlockKind.Heading)
                {
                    var text = block.Text.Trim();
                    if (IsExhibitEyebrow(block))
                    {
                        exhibit = text[^1..].ToUpperInvariant();
                        role = HeadingRole.ExhibitEyebrow;
                    }
                    else if (i > 0 && IsExhibitEyebrow(blocks[i - 1]))
                    {
                        role = HeadingRole.ExhibitTitle;
                    }
                    else if (text.Length > 0 && char.IsDigit(text[0]))
                    {
                        // A numbered clause heading means the exhibits have not started yet.
                        exhibit = null;
                    }
                }

                var opensTable = block.Kind == ContractBlockKind.ExhibitRow
                    && (i == 0 || blocks[i - 1].Kind != ContractBlockKind.ExhibitRow);

                ComposeBlock(column, block, signatures, opensTable && exhibit == "B", role, exhibit);
            }
        }

        private enum HeadingRole { Section, ExhibitEyebrow, ExhibitTitle }

        private static void ComposeBlock(
            ColumnDescriptor column, ContractBlock block, ContractSignatureBlockDto signatures,
            bool withTableHeader = false, HeadingRole role = HeadingRole.Section,
            string? exhibit = null)
        {
            switch (block.Kind)
            {
                case ContractBlockKind.Title:
                    // Already drawn by the cover masthead; skipped here so it is not repeated.
                    break;

                case ContractBlockKind.Heading:
                    // Kept with what follows it: a section header alone at the foot of a page is
                    // the most common ugly break in a document this long. Ordinary sections carry
                    // no rule underneath — the reference sets them off with colour and space, not a
                    // line. An exhibit opens differently: a small accent-blue eyebrow, then the
                    // exhibit's name at display size, then the one blue rule on the page.
                    if (role == HeadingRole.ExhibitEyebrow)
                    {
                        column.Item().ShowEntire().PaddingTop(6).Text(block.Text)
                            .FontSize(8.5f).Bold().FontColor(AccentBlue).LetterSpacing(0.18f);
                    }
                    else if (role == HeadingRole.ExhibitTitle)
                    {
                        column.Item().ShowEntire().Column(title =>
                        {
                            title.Item().PaddingTop(6).Text(block.Text)
                                .FontSize(14).Bold().FontColor(BrandBlue).LetterSpacing(0.10f);
                            title.Item().PaddingTop(8).PaddingBottom(4)
                                .LineHorizontal(0.75f).LineColor(AccentBlue);
                        });
                    }
                    else
                    {
                        column.Item().ShowEntire().PaddingTop(8).PaddingLeft(10).Text(block.Text)
                            .FontSize(10).Bold().FontColor(BrandBlue).LetterSpacing(0.05f);
                    }
                    break;

                case ContractBlockKind.SubHeading:
                    column.Item().PaddingTop(6).PaddingLeft(16).Text(block.Text)
                        .FontSize(9.5f).Bold().FontColor(BrandBlue).LetterSpacing(0.04f);
                    break;

                case ContractBlockKind.Bullet:
                    column.Item().PaddingLeft(20).Text($"•  {block.Text}");
                    break;

                case ContractBlockKind.ExhibitRow:
                    // A rule-only schedule: no vertical borders and no shaded label cell. Boxing
                    // every cell turned Exhibit B into a grid that fought the rest of the document.
                    // Each row stays whole, but rows may flow across pages — a 20-row schedule
                    // cannot be forced onto one.
                    if (withTableHeader)
                    {
                        column.Item().ShowEntire().PaddingTop(6).Column(header =>
                        {
                            header.Item().Row(row =>
                            {
                                row.ConstantItem(ExhibitLabelWidth).Text("ITEM")
                                    .FontSize(8).Bold().FontColor(BrandBlue).LetterSpacing(0.14f);
                                row.RelativeItem().Text("TERMS")
                                    .FontSize(8).Bold().FontColor(BrandBlue).LetterSpacing(0.14f);
                            });
                            header.Item().PaddingTop(4).LineHorizontal(0.75f).LineColor(BrandBlue);
                        });
                    }

                    column.Item().ShowEntire().Column(cell =>
                    {
                        cell.Item().PaddingVertical(7).Row(row =>
                        {
                            row.ConstantItem(ExhibitLabelWidth).PaddingRight(10)
                                .Text(block.Label ?? string.Empty);
                            row.RelativeItem().Text(block.Text);
                        });
                        cell.Item().LineHorizontal(0.75f).LineColor(RowRule);
                    });
                    break;

                case ContractBlockKind.SignatureBlock:
                    // ShowEntire is the fix for the split signature block: QuestPDF moves the whole
                    // element to the next page rather than breaking it, so the Contractor's name
                    // and title can never end up orphaned from the Client's box.
                    column.Item().ShowEntire().PaddingTop(10)
                        .Element(c => ComposeSignatureBlock(c, signatures));
                    break;

                default:
                    // Exhibit A states its premises, frequency, day, time and conditions as
                    // "LABEL: value" prose lines. The reference lays those out as a ruled
                    // definition list, so they are detected HERE rather than in the renderer —
                    // turning them into real blocks would change the canonical text the document
                    // hash is taken over, and the hash is what the signature certificate attests.
                    var definition = exhibit == "A" ? SplitDefinition(block.Text) : null;
                    if (definition != null)
                    {
                        column.Item().ShowEntire().Column(cell =>
                        {
                            cell.Item().PaddingVertical(7).Row(row =>
                            {
                                row.ConstantItem(DefinitionLabelWidth).PaddingRight(10)
                                    .Text(definition.Value.Label)
                                    .FontSize(8).FontColor(DisplayGrey).LetterSpacing(0.08f);
                                row.RelativeItem().Text(definition.Value.Value).FontSize(9.5f);
                            });
                            cell.Item().LineHorizontal(0.75f).LineColor(RowRule);
                        });
                    }
                    else
                    {
                        column.Item().Text(block.Text).Justify();
                    }
                    break;
            }
        }

        /// <summary>
        /// Two side-by-side bordered boxes with a blue top accent, one per party. Wrapped in
        /// ShowEntire by the caller so it is never split across a page boundary.
        /// </summary>
        private static void ComposeSignatureBlock(IContainer container, ContractSignatureBlockDto signatures)
        {
            container.Row(row =>
            {
                row.RelativeItem().Element(c => ComposeSignatureParty(c, signatures.Contractor));
                row.ConstantItem(18);
                row.RelativeItem().Element(c => ComposeSignatureParty(c, signatures.Client));
            });
        }

        private static void ComposeSignatureParty(IContainer container, ContractSignaturePartyDto party)
        {
            // QuestPDF sets one border colour per element, so the blue top accent is drawn as a
            // filled bar inside the grey box rather than as a differently-coloured top border.
            container
                .Border(0.75f).BorderColor(BoxBorder)
                .Column(outer =>
                {
                    outer.Item().Height(1.5f).Background(AccentBlue);
                    outer.Item().Padding(12).Column(column =>
                {
                    column.Spacing(3);
                    column.Item().Text(party.PartyLabel)
                        .FontSize(8.5f).Bold().FontColor(BrandBlue).LetterSpacing(0.12f);
                    column.Item().PaddingBottom(2).Text(party.EntityName).Bold();

                    // MinHeight, not Height: a fixed box makes a long typed mark a conflicting size
                    // constraint, and QuestPDF answers that by throwing — which would take down
                    // generation of an already-executed contract. The box has a floor so short
                    // marks and the unsigned rule still line up, and grows if it has to.
                    column.Item().PaddingTop(4).MinHeight(36).Element(mark =>
                    {
                        var image = TryDecodeDataUri(party.SignatureMark);
                        if (party.HasSigned && image != null)
                        {
                            mark.AlignLeft().AlignBottom().Height(34).Image(image).FitHeight();
                        }
                        else if (party.HasSigned && !string.IsNullOrWhiteSpace(party.SignatureMark))
                        {
                            // Typed signature. No script face is embedded, so italic at a larger
                            // size is what distinguishes the mark from the printed name beneath it.
                            mark.AlignBottom().Text(TypedMark(party.SignatureMark)).FontSize(16).Italic();
                        }
                    });

                    // The signature always sits ON its line, signed or not. Name and Title are
                    // different: a ruled line there marks a field somebody still has to fill in by
                    // hand, so a field that already carries its value does not get one. That is
                    // what makes an executed block read as executed rather than as a blank form.
                    column.Item().LineHorizontal(0.75f).LineColor(BodyInk);
                    column.Item().Text("Signature").FontSize(7.5f).FontColor(CaptionGrey)
                        .LetterSpacing(0.05f);

                    Field(column, "Name", party.SignerName);
                    Field(column, "Title", party.SignerTitle);

                    if (party.SignedAt.HasValue)
                    {
                        column.Item().PaddingTop(5)
                            .Text($"Signed electronically {FormatTimestamp(party.SignedAt.Value)}")
                            .FontSize(7).FontColor(CaptionGrey);
                    }
                    });
                });
        }

        /// <summary>
        /// Splits an Exhibit A metadata line into its label and value, or returns null if the
        /// paragraph is ordinary prose.
        ///
        /// Deliberately strict — an ALL-CAPS label, no longer than a short phrase, followed by a
        /// colon and a space. Ordinary sentences in the exhibit ("Services are performed using…")
        /// carry colons too, and turning one of those into a table row would be worse than leaving
        /// every row as prose.
        /// </summary>
        private static (string Label, string Value)? SplitDefinition(string text)
        {
            var colon = text.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0 || colon > 40) return null;

            var label = text[..colon];
            if (!label.Any(char.IsLetter)) return null;
            if (label.Any(c => char.IsLower(c))) return null;

            var value = text[(colon + 2)..].Trim();
            return value.Length == 0 ? null : (label, value);
        }

        /// <summary>
        /// One labelled field inside a signature box: the value if there is one, a ruled line if
        /// there is not, and the caption underneath either way.
        /// </summary>
        private static void Field(ColumnDescriptor column, string caption, string? value)
        {
            var filled = !string.IsNullOrWhiteSpace(value);

            if (filled) column.Item().PaddingTop(5).Text(value!);
            else column.Item().PaddingTop(5).Height(BodySize * BodyLineHeight);

            if (!filled) column.Item().LineHorizontal(0.75f).LineColor(BodyInk);

            column.Item().Text(caption).FontSize(7.5f).FontColor(CaptionGrey).LetterSpacing(0.05f);
        }

        // ── certificate ────────────────────────────────────────────────────────

        private static void ComposeCertificate(ColumnDescriptor column, ContractCertificateData cert)
        {
            column.Spacing(6);
            column.Item().AlignCenter().Text("ELECTRONIC SIGNATURE CERTIFICATE")
                .FontSize(13).Bold().FontColor(BrandBlue).LetterSpacing(0.04f);
            column.Item().AlignCenter()
                .Text("Record of electronic execution retained by the Contractor")
                .FontSize(8).FontColor(CaptionGrey);
            column.Item().PaddingTop(8).PaddingBottom(4).LineHorizontal(0.75f).LineColor(AccentBlue);

            void Row(ColumnDescriptor c, string label, string value)
            {
                c.Item().Row(row =>
                {
                    row.ConstantItem(140).Text(label).Bold().FontSize(9);
                    row.RelativeItem().Text(value).FontSize(9);
                });
            }

            column.Item().PaddingTop(4).Column(meta =>
            {
                meta.Spacing(3);
                Row(meta, "Document ID", cert.ContractNumber);
                Row(meta, "Contract version", cert.VersionNumber.ToString());
                Row(meta, "Effective date", ContractTextFormat.LongDate(cert.EffectiveDate));
                Row(meta, "Certificate issued", FormatTimestamp(cert.GeneratedAt));
            });

            column.Item().PaddingTop(8).ShowEntire().Column(hash =>
            {
                hash.Item().Text("Document hash (SHA-256)").Bold().FontSize(9);
                hash.Item().Text(cert.DocumentHash).FontSize(8).FontColor(CaptionGrey);
                hash.Item().PaddingTop(2)
                    .Text("Each signature below was applied to the document with this hash. Any change to the document produces a different hash and voids the match.")
                    .FontSize(7.5f).FontColor(CaptionGrey);
            });

            foreach (var signer in cert.Signers)
            {
                // Each signer's evidence block stays whole — half a signature record across a page
                // break is exactly the thing somebody would later question.
                column.Item().PaddingTop(10).ShowEntire()
                    .Border(0.75f).BorderColor(BoxBorder)
                    .Column(outer =>
                    {
                    outer.Item().Height(1.5f).Background(AccentBlue);
                    outer.Item().Padding(10).Column(block =>
                    {
                        block.Spacing(3);
                        block.Item().Text($"{signer.PartyLabel} — {signer.EntityName}")
                            .Bold().FontSize(9).FontColor(BrandBlue);
                        Row(block, "Signer", signer.Name);
                        if (!string.IsNullOrWhiteSpace(signer.Title)) Row(block, "Title", signer.Title!);
                        if (!string.IsNullOrWhiteSpace(signer.Email)) Row(block, "Email", signer.Email!);
                        Row(block, "Signed at", signer.SignedAt.HasValue
                            ? FormatTimestamp(signer.SignedAt.Value) : "Not signed");
                        Row(block, "Method", signer.Method?.ToString() ?? "-");
                        Row(block, "IP address", string.IsNullOrWhiteSpace(signer.IpAddress) ? "-" : signer.IpAddress!);
                        Row(block, "Consent", signer.ConsentAccepted ? "Accepted" : "Not recorded");
                        if (!string.IsNullOrWhiteSpace(signer.DocumentHashAtSigning))
                            Row(block, "Hash at signing", signer.DocumentHashAtSigning!);
                        if (!string.IsNullOrWhiteSpace(signer.UserAgent))
                            block.Item().Text(signer.UserAgent!).FontSize(7).FontColor(CaptionGrey);
                    });
                    });
            }
        }

        // ── helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// A typed signature is a person's name. Capping it keeps a pathological value — a pasted
        /// data URI, a malformed mark — from dominating the signature box, and is a second line of
        /// defence behind the MinHeight above.
        /// </summary>
        private static string TypedMark(string value) =>
            value.Length <= 60 ? value : value.Substring(0, 60) + "…";

        /// <summary>UTC is stated explicitly — a timestamp on a legal record must not be ambiguous.</summary>
        private static string FormatTimestamp(DateTime utc) => $"{utc:yyyy-MM-dd HH:mm:ss} UTC";

        /// <summary>
        /// The branding mark, loaded once. A missing file is not fatal: the document still renders
        /// without the logo rather than failing to produce a contract at all.
        /// </summary>
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

        /// <summary>PNG/JPEG data URI to bytes; null for typed signatures and malformed input.</summary>
        private static byte[]? TryDecodeDataUri(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var marker = value.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
            if (marker < 0 || !value.StartsWith("data:image", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                return Convert.FromBase64String(value.Substring(marker + "base64,".Length));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}

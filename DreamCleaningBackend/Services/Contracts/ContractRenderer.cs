using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DreamCleaningBackend.Services.Contracts
{
    public enum ContractBlockKind
    {
        Title,
        Heading,
        SubHeading,
        Paragraph,
        Bullet,
        ExhibitRow,
        SignatureBlock
    }

    /// <summary>One rendered element. The PDF writer and the HTML writer consume the same list.</summary>
    public class ContractBlock
    {
        public ContractBlockKind Kind { get; set; }
        public string Text { get; set; } = string.Empty;
        /// <summary>Left-hand label of an <see cref="ContractBlockKind.ExhibitRow"/>.</summary>
        public string? Label { get; set; }
    }

    /// <summary>A fully rendered document: structured blocks, HTML, canonical text and its hash.</summary>
    public class RenderedContract
    {
        public List<ContractBlock> Blocks { get; set; } = new();
        public string Html { get; set; } = string.Empty;
        public string PlainText { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;

        /// <summary>Tokens the body referenced that the snapshot could not fill. Surfaced to the
        /// admin on preview rather than shipped to a client as a literal {{TOKEN}}.</summary>
        public List<string> UnresolvedTokens { get; set; } = new();
    }

    /// <summary>
    /// Turns a frozen snapshot plus a template body into a document. Deterministic and pure - the
    /// same snapshot always renders the same bytes, which is what makes the stored hash meaningful
    /// as the thing a signer attested to.
    /// </summary>
    public static class ContractRenderer
    {
        private static readonly Regex TokenPattern =
            new(@"\{\{([A-Z0-9_]+(?::[a-z0-9\-]+)?)\}\}", RegexOptions.Compiled);

        public static RenderedContract Render(ContractSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var tokens = ContractPlaceholders.Build(snapshot);
            var scopeGroups = (snapshot.Scope?.Groups ?? new List<ScopeGroup>())
                .ToDictionary(g => g.Key ?? string.Empty, g => g, StringComparer.OrdinalIgnoreCase);

            var consumedScopeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unresolved = new List<string>();

            var lines = ExpandLines(snapshot.TemplateBodyText ?? string.Empty, scopeGroups, consumedScopeKeys);

            var blocks = new List<ContractBlock>();
            var paragraph = new StringBuilder();

            void FlushParagraph()
            {
                if (paragraph.Length == 0) return;
                blocks.Add(new ContractBlock { Kind = ContractBlockKind.Paragraph, Text = paragraph.ToString() });
                paragraph.Clear();
            }

            foreach (var rawLine in lines)
            {
                var line = Substitute(rawLine, tokens, scopeGroups, consumedScopeKeys, unresolved);

                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushParagraph();
                    continue;
                }

                if (line.StartsWith("@SIGNATURE_BLOCK", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    blocks.Add(new ContractBlock { Kind = ContractBlockKind.SignatureBlock });
                    continue;
                }
                if (line.StartsWith("### ", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    blocks.Add(new ContractBlock { Kind = ContractBlockKind.SubHeading, Text = line.Substring(4).Trim() });
                    continue;
                }
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    blocks.Add(new ContractBlock { Kind = ContractBlockKind.Heading, Text = line.Substring(3).Trim() });
                    continue;
                }
                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    blocks.Add(new ContractBlock { Kind = ContractBlockKind.Title, Text = line.Substring(2).Trim() });
                    continue;
                }
                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    blocks.Add(new ContractBlock { Kind = ContractBlockKind.Bullet, Text = line.Substring(2).Trim() });
                    continue;
                }
                if (line.StartsWith("|", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    var parts = line.Substring(1).Split('|', 2);
                    blocks.Add(new ContractBlock
                    {
                        Kind = ContractBlockKind.ExhibitRow,
                        Label = parts[0].Trim(),
                        Text = parts.Length > 1 ? parts[1].Trim() : string.Empty
                    });
                    continue;
                }

                // Ordinary prose. Consecutive non-blank lines that each start their own lettered
                // clause read better as separate paragraphs than as one run-on block, which is how
                // the source agreement is laid out.
                if (paragraph.Length > 0 && StartsNewClause(line))
                {
                    FlushParagraph();
                }
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line.Trim());
            }
            FlushParagraph();

            var plain = BuildPlainText(blocks);
            return new RenderedContract
            {
                Blocks = blocks,
                Html = BuildHtml(blocks),
                PlainText = plain,
                Sha256 = Hash(plain),
                UnresolvedTokens = unresolved.Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList()
            };
        }

        /// <summary>SHA-256, lowercase hex. Computed over the canonical plain text.</summary>
        public static string Hash(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        // ── internals ──────────────────────────────────────────────────────────

        /// <summary>
        /// Splits the body into lines, expanding {{SCOPE_ADDITIONAL}} into real lines for any
        /// scope group the body did not inline. Runs BEFORE substitution so the appended blocks
        /// parse through the same heading/paragraph rules as the rest of the document.
        /// </summary>
        private static List<string> ExpandLines(
            string body,
            IReadOnlyDictionary<string, ScopeGroup> groups,
            HashSet<string> consumed)
        {
            var source = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            // First pass: note every group the body inlines, so the append list is accurate no
            // matter where {{SCOPE_ADDITIONAL}} sits in the document.
            foreach (Match m in TokenPattern.Matches(body))
            {
                var name = m.Groups[1].Value;
                if (name.StartsWith("SCOPE:", StringComparison.Ordinal))
                    consumed.Add(name.Substring("SCOPE:".Length));
            }

            var output = new List<string>();
            foreach (var line in source)
            {
                if (line.Trim() != "{{SCOPE_ADDITIONAL}}")
                {
                    output.Add(line);
                    continue;
                }

                var extra = groups.Values
                    .Where(g => !consumed.Contains(g.Key ?? string.Empty))
                    .Where(g => g.Items.Any(i => i.Selected))
                    .ToList();
                if (extra.Count == 0) continue;

                output.Add("");
                output.Add("### A10. ADDITIONAL SCOPE");
                foreach (var group in extra)
                {
                    var items = SelectedLabels(group);
                    var verb = string.Equals(group.Kind, "excluded", StringComparison.OrdinalIgnoreCase)
                        ? "The following are not included"
                        : "The following are included";
                    output.Add("");
                    output.Add($"{group.Title}. {verb}: {items}.");
                }
                output.Add("");
            }
            return output;
        }

        private static string Substitute(
            string line,
            IReadOnlyDictionary<string, string> tokens,
            IReadOnlyDictionary<string, ScopeGroup> groups,
            HashSet<string> consumed,
            List<string> unresolved)
        {
            if (!line.Contains("{{", StringComparison.Ordinal)) return line;

            return TokenPattern.Replace(line, m =>
            {
                var name = m.Groups[1].Value;

                if (name.StartsWith("SCOPE:", StringComparison.Ordinal))
                {
                    var key = name.Substring("SCOPE:".Length);
                    consumed.Add(key);
                    if (!groups.TryGetValue(key, out var group)) return "none";
                    var labels = SelectedLabels(group);
                    // An emptied group must still leave a grammatical sentence - "The following
                    // are excluded: ." is worse than an explicit "none".
                    return string.IsNullOrWhiteSpace(labels) ? "none" : labels;
                }

                if (name == "SCOPE_ADDITIONAL") return string.Empty;

                if (tokens.TryGetValue(name, out var value))
                {
                    if (string.IsNullOrWhiteSpace(value)) unresolved.Add(name);
                    return value;
                }

                unresolved.Add(name);
                // Leave the token visible rather than silently blanking it: an admin seeing
                // {{FOO}} in the preview knows something is unmapped; a blank hides it.
                return m.Value;
            });
        }

        private static string SelectedLabels(ScopeGroup group)
        {
            var labels = group.Items
                .Where(i => i.Selected && !string.IsNullOrWhiteSpace(i.Label))
                .Select(i => i.Label.Trim())
                .ToList();
            if (labels.Count == 0) return string.Empty;
            if (labels.Count == 1) return labels[0];
            // Semicolons, not commas: several of these items contain commas of their own, and the
            // reference Exhibit A separates them the same way.
            return string.Join("; ", labels);
        }

        /// <summary>True for "(a) ", "(b-1) ", "A1. ", "SERVICE PREMISES:" style clause openers.</summary>
        private static bool StartsNewClause(string line)
        {
            var t = line.TrimStart();
            if (t.StartsWith("(", StringComparison.Ordinal)) return true;
            return Regex.IsMatch(t, @"^[A-Z][A-Z /]{3,}:");
        }

        private static string BuildPlainText(IEnumerable<ContractBlock> blocks)
        {
            var sb = new StringBuilder();
            foreach (var b in blocks)
            {
                switch (b.Kind)
                {
                    case ContractBlockKind.ExhibitRow:
                        sb.Append(b.Label).Append(": ").AppendLine(b.Text);
                        break;
                    case ContractBlockKind.Bullet:
                        sb.Append("- ").AppendLine(b.Text);
                        break;
                    case ContractBlockKind.SignatureBlock:
                        sb.AppendLine("[SIGNATURE BLOCK]");
                        break;
                    default:
                        sb.AppendLine(b.Text);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string BuildHtml(IEnumerable<ContractBlock> blocks)
        {
            var sb = new StringBuilder();
            var openTable = false;

            void CloseTable()
            {
                if (!openTable) return;
                sb.AppendLine("</tbody></table>");
                openTable = false;
            }

            foreach (var b in blocks)
            {
                if (b.Kind != ContractBlockKind.ExhibitRow) CloseTable();

                switch (b.Kind)
                {
                    case ContractBlockKind.Title:
                        sb.AppendLine($"<h1 class=\"dc-doc-title\">{E(b.Text)}</h1>");
                        break;
                    case ContractBlockKind.Heading:
                        sb.AppendLine($"<h2 class=\"dc-doc-heading\">{E(b.Text)}</h2>");
                        break;
                    case ContractBlockKind.SubHeading:
                        sb.AppendLine($"<h3 class=\"dc-doc-subheading\">{E(b.Text)}</h3>");
                        break;
                    case ContractBlockKind.Bullet:
                        sb.AppendLine($"<p class=\"dc-doc-bullet\">{E(b.Text)}</p>");
                        break;
                    case ContractBlockKind.ExhibitRow:
                        if (!openTable)
                        {
                            sb.AppendLine("<table class=\"dc-doc-table\"><tbody>");
                            openTable = true;
                        }
                        sb.AppendLine($"<tr><th>{E(b.Label)}</th><td>{E(b.Text)}</td></tr>");
                        break;
                    case ContractBlockKind.SignatureBlock:
                        // The host page draws the real block (marks, names, dates) - the document
                        // body only says where it goes.
                        sb.AppendLine("<div class=\"dc-doc-signature-anchor\"></div>");
                        break;
                    default:
                        sb.AppendLine($"<p class=\"dc-doc-para\">{E(b.Text)}</p>");
                        break;
                }
            }
            CloseTable();
            return sb.ToString();
        }

        private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    }
}

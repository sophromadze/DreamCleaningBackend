using System.Globalization;

namespace DreamCleaningBackend.Helpers.Contracts
{
    /// <summary>
    /// Renders the numeric tokens the way a contract writes them - "twelve (12)", "fifty percent
    /// (50%)", "thirty-five dollars ($35.00)". The reference agreement spells every number out
    /// alongside its digits, so a token that returned bare digits would visibly break the
    /// register of the document the moment an admin changed a term from its default.
    /// </summary>
    public static class ContractTextFormat
    {
        /// <summary>
        /// What an unanswered field prints: a ruled blank, the way the drafted agreement itself
        /// presents something the Parties have not filled in yet.
        ///
        /// ONE constant, shared with <c>ContractPlaceholders.RuledBlank</c>, because the renderer
        /// recognises this exact string to flag the field as unresolved in the preview banner.
        /// Two literals of sixteen underscores look identical in review and would silently stop
        /// matching the day one of them gained a seventeenth.
        /// </summary>
        public const string RuledBlank = "________________";

        private static readonly string[] Ones =
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
            "seventeen", "eighteen", "nineteen"
        };

        private static readonly string[] Tens =
        {
            "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"
        };

        /// <summary>Lowercase words for a whole number, e.g. 48 -> "forty-eight".</summary>
        public static string Words(int value)
        {
            if (value < 0) return "negative " + Words(-value);
            if (value < 20) return Ones[value];
            if (value < 100)
            {
                var tens = Tens[value / 10];
                var rest = value % 10;
                return rest == 0 ? tens : $"{tens}-{Ones[rest]}";
            }
            if (value < 1000)
            {
                var hundreds = $"{Ones[value / 100]} hundred";
                var rest = value % 100;
                return rest == 0 ? hundreds : $"{hundreds} {Words(rest)}";
            }
            if (value < 1_000_000)
            {
                var thousands = $"{Words(value / 1000)} thousand";
                var rest = value % 1000;
                return rest == 0 ? thousands : $"{thousands} {Words(rest)}";
            }
            // Beyond a million a contract term is vanishingly unlikely; fall back to digits
            // rather than pretending to spell it.
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        /// <summary>"twelve (12)" - the form every count in the reference agreement takes.</summary>
        public static string WordsWithDigits(int value) => $"{Words(value)} ({value})";

        /// <summary>
        /// Words with no digits, sentence-cased: "Three".
        ///
        /// For the handful of places a count OPENS a sentence, where "Three (3) Client-attributable
        /// missed visits..." reads like a form and not like an agreement. Everything mid-sentence
        /// keeps <see cref="WordsWithDigits"/>.
        /// </summary>
        public static string WordsCapitalized(int value)
        {
            var words = Words(value);
            return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words.Substring(1);
        }

        /// <summary>
        /// Ordinal words: 1 -> "first", 2 -> "second", 21 -> "twenty-first".
        ///
        /// Section 15(f) counts missed visits and then refers back to "the second such visit" and
        /// "a third such visit". Those two words have to follow the configured threshold, or an
        /// admin who raises it to four leaves the clause warning after the second and terminating
        /// on the third - a paragraph that contradicts its own first sentence.
        /// </summary>
        public static string Ordinal(int value)
        {
            if (value <= 0) return Words(value);

            var words = Words(value);
            var lastSpace = Math.Max(words.LastIndexOf(' '), words.LastIndexOf('-'));
            var head = lastSpace >= 0 ? words.Substring(0, lastSpace + 1) : string.Empty;
            var tail = lastSpace >= 0 ? words.Substring(lastSpace + 1) : words;

            var ordinalTail = tail switch
            {
                "one" => "first",
                "two" => "second",
                "three" => "third",
                "five" => "fifth",
                "eight" => "eighth",
                "nine" => "ninth",
                "twelve" => "twelfth",
                _ when tail.EndsWith("y", StringComparison.Ordinal) => tail[..^1] + "ieth",
                _ => tail + "th"
            };

            return head + ordinalTail;
        }

        /// <summary>
        /// "fifty percent (50%)" / "one and one-half percent (1.5%)". Fractional rates are spelled
        /// only for the halves and quarters that actually occur in commercial terms; anything else
        /// keeps its digits inside the words position so the sentence still parses.
        /// </summary>
        public static string PercentWithDigits(decimal value)
        {
            var digits = FormatPercent(value);
            var whole = (int)decimal.Truncate(value);
            var fraction = value - whole;

            string words;
            if (fraction == 0m) words = Words(whole);
            else if (fraction == 0.5m) words = whole == 1 ? "one and one-half" : $"{Words(whole)} and one-half";
            else if (fraction == 0.25m) words = $"{Words(whole)} and one-quarter";
            else if (fraction == 0.75m) words = $"{Words(whole)} and three-quarters";
            else words = digits.TrimEnd('%');

            return $"{words} percent ({digits})";
        }

        /// <summary>Trailing-zero-free percent: 8.875 -> "8.875%", 50 -> "50%".</summary>
        public static string FormatPercent(decimal value)
        {
            var trimmed = value.ToString("0.#####", CultureInfo.InvariantCulture);
            return $"{trimmed}%";
        }

        /// <summary>"$925.43" - always two decimals with thousands separators.</summary>
        public static string Money(decimal value) =>
            value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));

        /// <summary>
        /// "thirty-five dollars ($35.00)". Only used where the source agreement spells the amount
        /// out (Section 11(g)); everywhere else the bare <see cref="Money"/> form is correct.
        /// </summary>
        public static string MoneyWithWords(decimal value)
        {
            var whole = (int)decimal.Truncate(value);
            var cents = (int)Math.Round((value - whole) * 100m, MidpointRounding.AwayFromZero);
            var dollars = $"{Words(whole)} dollar{(whole == 1 ? "" : "s")}";
            if (cents > 0) dollars += $" and {Words(cents)} cent{(cents == 1 ? "" : "s")}";
            return $"{dollars} ({Money(value)})";
        }

        /// <summary>
        /// "Monday", "Monday and Wednesday", "Monday, Wednesday and Friday" - a list as a person
        /// writes it in prose.
        ///
        /// Serial comma deliberately absent: the reference agreement does not use one, and the
        /// document has to read in one voice whether it names one service day or five. An empty
        /// list yields an empty string rather than a dangling "and".
        /// </summary>
        public static string JoinWithAnd(IReadOnlyList<string>? parts)
        {
            var items = (parts ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .ToList();

            return items.Count switch
            {
                0 => string.Empty,
                1 => items[0],
                2 => $"{items[0]} and {items[1]}",
                _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1]
            };
        }

        /// <summary>Contract-style long date: "September 6, 2026". Blank line when unset.</summary>
        public static string LongDate(DateTime? value) =>
            value.HasValue
                ? value.Value.ToString("MMMM d, yyyy", CultureInfo.GetCultureInfo("en-US"))
                : RuledBlank;

        /// <summary>
        /// Formats a stored digits-only phone back to (929) 930-1525 for the notice block. Any
        /// length other than 10/11 digits is passed through untouched rather than mangled.
        /// </summary>
        public static string Phone(string? digits)
        {
            if (string.IsNullOrWhiteSpace(digits)) return string.Empty;
            var d = new string(digits.Where(char.IsDigit).ToArray());
            if (d.Length == 11 && d[0] == '1') d = d.Substring(1);
            if (d.Length != 10) return digits;
            return $"({d.Substring(0, 3)}) {d.Substring(3, 3)}-{d.Substring(6)}";
        }

        /// <summary>
        /// URL/file-safe slug of a legal entity name, for the executed PDF filename
        /// ({ContractNumber}-{ClientLegalNameSlug}-Executed.pdf).
        /// </summary>
        public static string Slug(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "client";
            var chars = value.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                .ToArray();
            var slug = new string(chars);
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            slug = slug.Trim('-');
            return string.IsNullOrEmpty(slug) ? "client" : slug;
        }
    }
}

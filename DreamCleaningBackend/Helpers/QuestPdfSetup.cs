using QuestPDF.Drawing;
using QuestPDF.Infrastructure;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// The ONE place QuestPDF is configured. <c>Program.cs</c> calls it at startup and every PDF
    /// service calls it from its constructor, so a document can never be composed under default
    /// settings — whether the caller is the web host, a test or a tool. It runs once per process;
    /// later calls are free.
    ///
    /// FONTS (QuestPDF 2026.9). System fonts are not used: QuestPDF draws with its bundled Lato and
    /// the Noto families shipped in <c>Assets/Fonts</c> (SIL Open Font License, <c>OFL.txt</c>
    /// beside them), so a PDF looks the same on a Windows dev box and on the Linux VPS. Before the
    /// upgrade, any character Lato lacks — a Chinese or Korean name, an Arabic address, an emoji
    /// in a note — fell back to whatever the machine had installed, i.e. something else on the
    /// server, or nothing. <see cref="FontFamilies"/> is the fallback chain every document's base
    /// text style names; Lato stays first, so Latin text renders exactly as it always has.
    ///
    /// A CHARACTER NO FONT COVERS NEVER FAILS A DOCUMENT. QuestPDF now throws on one by default;
    /// here that would turn a stray pasted symbol into a contract that cannot be executed or an
    /// invoice email sent without its PDF. It is drawn as an empty box instead, and
    /// <c>PdfFontCoverageTests</c> renders a broad multilingual sample with the check ON, so a gap
    /// in the shipped fonts fails the test suite rather than a customer's document.
    ///
    /// A MISSING FONT FILE NEVER FAILS A DOCUMENT EITHER. If a deployment leaves a Noto family
    /// out, text falls back to Lato and the next family in the chain; <c>Program.cs</c> logs one
    /// warning at startup naming what is missing (<see cref="MissingFontFamilies"/>).
    ///
    /// LAYOUT ERRORS CARRY NO DOCUMENT CONTENT. Since 2026.9 QuestPDF quotes fragments of the
    /// document in layout exceptions by default; these documents hold bank details and client
    /// names, and the exception ends up in the logs. Before the upgrade the detail was only added
    /// with a debugger attached, which is what production keeps.
    /// </summary>
    public static class QuestPdfSetup
    {
        /// <summary>Where the bundled Noto fonts are deployed (copied with the rest of Assets).</summary>
        public static string FontDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");

        /// <summary>
        /// The font fallback chain, in priority order. QuestPDF picks, per character, the first
        /// family that has a glyph for it. Lato is the house face; Noto Sans covers the Latin,
        /// Greek and Cyrillic characters Lato lacks; the rest cover one script each. One CJK face
        /// is enough: the collection maps every Chinese, Japanese and Korean character in each
        /// face, and SC is the regional glyph style most of our customers write.
        ///
        /// AT MOST <see cref="MaxFontFamilies"/> ENTRIES. QuestPDF hands the list to Skia in a
        /// fixed-size native array and silently drops whatever does not fit, so a seventeenth
        /// family would simply never be consulted (found the hard way: the emoji face was last).
        /// </summary>
        public static readonly string[] FontFamilies =
        {
            "Lato",
            "Noto Sans",
            "Noto Sans Arabic",
            "Noto Sans Hebrew",
            "Noto Sans Bengali",
            "Noto Sans Devanagari",
            "Noto Sans Gurmukhi",
            "Noto Sans Gujarati",
            "Noto Sans Tamil",
            "Noto Sans Thai",
            "Noto Sans Georgian",
            "Noto Sans Armenian",
            "Noto Sans CJK SC",
            "Noto Sans Symbols",
            "Noto Sans Symbols 2",
            "Noto Emoji"
        };

        /// <summary>QuestPDF's native limit on a text style's font families (SkTextStyle.FONT_FAMILIES_LENGTH).</summary>
        public const int MaxFontFamilies = 16;

        private static readonly Lazy<bool> Applied = new(Apply, LazyThreadSafetyMode.ExecutionAndPublication);

        public static void Configure() => _ = Applied.Value;

        /// <summary>
        /// The <see cref="FontFamilies"/> QuestPDF cannot find among its registered fonts — empty
        /// when the bundled fonts were deployed. Triggers the one-time font discovery.
        /// </summary>
        public static IReadOnlyList<string> MissingFontFamilies()
        {
            Configure();
            var registered = FontManager.GetRegisteredFonts()
                .Select(f => f.FamilyName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return FontFamilies.Where(f => !registered.Contains(f)).ToList();
        }

        private static bool Apply()
        {
            // Community licence: the business is under the revenue threshold (licence terms v3.0,
            // reviewed 2026-10 for the 2026.9.1 upgrade).
            QuestPDF.Settings.License = LicenseType.Community;

            // Scanned once, recursively, on the first document. Pointed at the font folder rather
            // than left on the default (the whole application directory), which would also walk
            // anything else deployed under it.
            QuestPDF.Settings.FontDiscoveryPath = FontDirectory;
            QuestPDF.Settings.UseSystemFonts = false;
            QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
            QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
            QuestPDF.Settings.EnableDetailedLayoutErrors = false;
            return true;
        }
    }
}

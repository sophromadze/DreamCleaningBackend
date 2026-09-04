namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// WHICH LANGUAGE A CLEANER IS SPOKEN TO IN - one map, used by the assignment email, the
    /// assignment SMS, the reminder and the cleaner portal.
    ///
    /// The nationality-to-language mapping predates the portal: it lived as a private method in
    /// EmailService, and the portal needed exactly the same answer. A second copy would have let
    /// the same person be emailed in Georgian and shown an English page, so the mapping moved here
    /// and EmailService delegates to it.
    ///
    /// TWO INPUTS, and the order between them is the whole rule: a cleaner's own CHOICE always
    /// wins, and their nationality is only the default. Somebody who reads English better than the
    /// translation of their own language must be able to say so and be believed - that is what
    /// <see cref="Models.Cleaner.PortalLanguage"/> is, and why NULL there means "follow my
    /// nationality" rather than "English".
    /// </summary>
    public static class CleanerLanguage
    {
        public const string English = "en";
        public const string Georgian = "ka";
        public const string Russian = "ru";
        public const string Spanish = "es";

        /// <summary>Every language the cleaner-facing surfaces are translated into.</summary>
        public static readonly IReadOnlyList<string> Supported = new[] { English, Georgian, Russian, Spanish };

        /// <summary>
        /// The default language for a nationality. Anything unrecognised - including a blank
        /// nationality, which is normal - falls back to English rather than guessing.
        /// </summary>
        public static string FromNationality(string? nationality)
        {
            var n = (nationality ?? string.Empty).Trim().ToLowerInvariant();
            return n switch
            {
                "georgian" => Georgian,
                "russian" => Russian,
                "spanish" => Spanish,
                _ => English
            };
        }

        /// <summary>
        /// The language to actually render in: the cleaner's stored choice when they have made a
        /// supported one, their nationality's default otherwise.
        /// </summary>
        public static string Resolve(string? nationality, string? preferredLanguage)
        {
            var preferred = Normalize(preferredLanguage);
            return preferred ?? FromNationality(nationality);
        }

        /// <summary>
        /// A language code trimmed and lower-cased, or NULL when it is blank or not one we
        /// translate into. Null is what gets STORED for "follow my nationality", so an unusable
        /// value must normalise to null rather than to English - pinning somebody to English is a
        /// different claim from having expressed no preference at all.
        /// </summary>
        public static string? Normalize(string? languageCode)
        {
            var code = (languageCode ?? string.Empty).Trim().ToLowerInvariant();
            if (code.Length == 0) return null;
            return Supported.Contains(code) ? code : null;
        }
    }
}

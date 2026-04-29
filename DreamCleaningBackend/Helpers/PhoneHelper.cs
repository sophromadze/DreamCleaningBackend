using System.Linq;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Phone-number helpers for the US (NANP) market.
    /// Storage form is a bare 10-digit string (no parens, dashes, spaces, country code).
    /// </summary>
    public static class PhoneHelper
    {
        /// <summary>
        /// Strips every non-digit and drops a leading "1" country code so the
        /// result is a bare 10-digit US number whenever possible. Returns null
        /// for null/whitespace/empty input. For unusually short or long numbers
        /// we still return the digit string so we don't silently lose data.
        /// </summary>
        public static string? NormalizeToDigits(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return null;
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            if (digits.Length == 0) return null;
            if (digits.Length == 11 && digits.StartsWith('1'))
                return digits.Substring(1);
            if (digits.Length > 11 && digits.StartsWith('1'))
                return digits.Substring(1, 10);
            if (digits.Length > 10)
                return digits.Substring(0, 10);
            return digits;
        }

        /// <summary>
        /// Same as <see cref="NormalizeToDigits"/> but returns string.Empty
        /// instead of null. Useful for non-nullable string columns.
        /// </summary>
        public static string NormalizeToDigitsOrEmpty(string? phone)
            => NormalizeToDigits(phone) ?? string.Empty;
    }
}

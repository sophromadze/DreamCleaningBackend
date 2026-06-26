using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DreamCleaningBackend.Services.CallTracking
{
    /// <summary>
    /// Pure classification of who placed a call. Used both at sync time (per new record) and by
    /// the admin reclassify backfill (every existing record). Idempotent: re-running on the same
    /// record yields the same result. Reads the record's already-resolved LeadId — it never
    /// touches lead-linking.
    ///
    /// Precedence: Cleaner &gt; Customer &gt; Spam &gt; Unknown.
    /// </summary>
    public static class CallClassifier
    {
        // Short missed/rejected calls are almost always robocalls.
        private const int SpamMaxDurationSeconds = 15;

        /// <summary>
        /// Default dedicated Google Ads tracking number (E.164). Overridable via config key
        /// "RingCentral:AdTrackingNumber" so it can change without a redeploy.
        /// </summary>
        public const string DefaultAdTrackingNumber = "+19294195681";

        /// <summary>
        /// Resolve the ad tracking number from config (falling back to
        /// <see cref="DefaultAdTrackingNumber"/>) and normalize it to bare digits — the same form
        /// used to compare against <see cref="CallRecord.ToNumber"/> in <see cref="Classify"/>.
        /// </summary>
        public static string ResolveAdNumberDigits(IConfiguration configuration)
        {
            var configured = configuration["RingCentral:AdTrackingNumber"];
            if (string.IsNullOrWhiteSpace(configured))
                configured = DefaultAdTrackingNumber;
            return PhoneHelper.NormalizeToDigitsOrEmpty(configured);
        }

        /// <summary>
        /// Build a bare-digit phone → cleaner id map once per run (avoids a query per call).
        /// Cleaner.Phone is already digit-normalized on set; we normalize again defensively and
        /// skip empty phones. First writer wins on the rare duplicate-number collision.
        /// </summary>
        public static async Task<Dictionary<string, int>> LoadCleanerPhoneMapAsync(
            ApplicationDbContext context, CancellationToken ct = default)
        {
            var cleaners = await context.Cleaners
                .Where(c => c.Phone != null && c.Phone != "")
                .Select(c => new { c.Id, c.Phone })
                .ToListAsync(ct);

            var map = new Dictionary<string, int>();
            foreach (var c in cleaners)
            {
                var digits = PhoneHelper.NormalizeToDigits(c.Phone);
                if (!string.IsNullOrEmpty(digits) && !map.ContainsKey(digits))
                    map[digits] = c.Id;
            }
            return map;
        }

        /// <summary>
        /// Set <see cref="CallRecord.CallCategory"/>, <see cref="CallRecord.MatchedCleanerId"/> and
        /// <see cref="CallRecord.IsAdCall"/> from the record's current state. MatchedCleanerId is
        /// always reset first so a number that stops being a cleaner re-classifies cleanly on
        /// backfill. <paramref name="adNumberDigits"/> is the bare-digit ad number from
        /// <see cref="ResolveAdNumberDigits"/>.
        /// </summary>
        public static void Classify(
            CallRecord record,
            IReadOnlyDictionary<string, int> cleanerPhoneMap,
            string? adNumberDigits)
        {
            record.MatchedCleanerId = null;

            // 1) Category first (precedence Cleaner > Customer > Spam > Unknown), setting
            //    MatchedCleanerId as a side effect for the cleaner case.
            record.CallCategory = ResolveCategory(record, cleanerPhoneMap);

            // 2) Ad flag derived from the resolved category + the dialed number. An ad call must be
            //    a genuine prospect, so staff/junk are excluded: a Cleaner or Spam call to the ad
            //    number is NOT an ad call (Customer + Unknown to the ad number are). ToNumber is the
            //    provider's E.164 format; normalize with the same bare-digit helper as cleaner
            //    matching so +1/formatting differences don't break the compare.
            var toDigits = PhoneHelper.NormalizeToDigits(record.ToNumber);
            var dialedAdNumber =
                !string.IsNullOrEmpty(adNumberDigits) &&
                !string.IsNullOrEmpty(toDigits) &&
                toDigits == adNumberDigits;
            record.IsAdCall =
                dialedAdNumber &&
                record.CallCategory != CallCategory.Cleaner &&
                record.CallCategory != CallCategory.Spam;
        }

        /// <summary>
        /// Resolve who placed the call using the precedence Cleaner &gt; Customer &gt; Spam &gt;
        /// Unknown. Sets <see cref="CallRecord.MatchedCleanerId"/> when a cleaner matches; otherwise
        /// leaves it as the caller reset it. Pure aside from that documented side effect.
        /// </summary>
        private static string ResolveCategory(
            CallRecord record, IReadOnlyDictionary<string, int> cleanerPhoneMap)
        {
            // 1) Cleaner — bare-digit match against the cleaner table. FromNumber is stored in the
            //    provider's format (E.164), so normalize before comparing.
            var digits = PhoneHelper.NormalizeToDigits(record.FromNumber);
            if (!string.IsNullOrEmpty(digits) && cleanerPhoneMap.TryGetValue(digits, out var cleanerId))
            {
                record.MatchedCleanerId = cleanerId;
                return CallCategory.Cleaner;
            }

            // 2) Customer — already linked to a CRM lead.
            if (record.LeadId.HasValue)
                return CallCategory.Customer;

            // 3) Spam — short missed/rejected call from an unknown number.
            var result = record.Result ?? string.Empty;
            var missedOrRejected =
                result.Equals("Missed", StringComparison.OrdinalIgnoreCase) ||
                result.Equals("Rejected", StringComparison.OrdinalIgnoreCase);
            if (missedOrRejected && record.DurationSeconds < SpamMaxDurationSeconds)
                return CallCategory.Spam;

            // 4) Everything else.
            return CallCategory.Unknown;
        }
    }
}

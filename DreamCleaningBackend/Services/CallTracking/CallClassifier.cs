using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

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
        /// Set <see cref="CallRecord.CallCategory"/> and <see cref="CallRecord.MatchedCleanerId"/>
        /// from the record's current state. MatchedCleanerId is always reset first so a number that
        /// stops being a cleaner re-classifies cleanly on backfill.
        /// </summary>
        public static void Classify(CallRecord record, IReadOnlyDictionary<string, int> cleanerPhoneMap)
        {
            record.MatchedCleanerId = null;

            // 1) Cleaner — bare-digit match against the cleaner table. FromNumber is stored in the
            //    provider's format (E.164), so normalize before comparing.
            var digits = PhoneHelper.NormalizeToDigits(record.FromNumber);
            if (!string.IsNullOrEmpty(digits) && cleanerPhoneMap.TryGetValue(digits, out var cleanerId))
            {
                record.MatchedCleanerId = cleanerId;
                record.CallCategory = CallCategory.Cleaner;
                return;
            }

            // 2) Customer — already linked to a CRM lead.
            if (record.LeadId.HasValue)
            {
                record.CallCategory = CallCategory.Customer;
                return;
            }

            // 3) Spam — short missed/rejected call from an unknown number.
            var result = record.Result ?? string.Empty;
            var missedOrRejected =
                result.Equals("Missed", StringComparison.OrdinalIgnoreCase) ||
                result.Equals("Rejected", StringComparison.OrdinalIgnoreCase);
            if (missedOrRejected && record.DurationSeconds < SpamMaxDurationSeconds)
            {
                record.CallCategory = CallCategory.Spam;
                return;
            }

            // 4) Everything else.
            record.CallCategory = CallCategory.Unknown;
        }
    }
}

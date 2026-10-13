using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SMS consent (2026-10, RingCentral 10DLC): it is OPTIONAL everywhere and never a condition of
    /// booking, paying, a quote or a contact message. The stored answer is <see cref="User.CanReceiveMessages"/>
    /// (default true, as before) plus WHEN and WHERE it last changed (<see cref="User.SmsConsentAt"/> /
    /// <see cref="User.SmsConsentSource"/>).
    ///
    /// The optional checkbox (booking, payment page):
    /// - it shows ticked only when consent was RECORDED and is still on (<see cref="ShowsTicked"/>) -
    ///   a guest, or an account whose consent was never recorded, sees it unticked (never pre-ticked);
    /// - ticking it records consent (true + time + source);
    /// - leaving it unticked changes nothing - SMS stays as it was;
    /// - unticking a box that was SHOWN ticked is an explicit opt-out (false + time + source).
    /// SMS otherwise stops only on an explicit opt-out: the profile toggle or the admin Users toggle
    /// (<see cref="RecordChange"/>). <c>SmsService</c> skips customer numbers that opted out.
    /// </summary>
    public static class SmsConsentRules
    {
        public const string SourceBooking = "booking";
        public const string SourcePaymentPage = "payment-page";
        public const string SourceProfile = "profile";
        public const string SourceAdmin = "admin";
        public const string SourceContactForm = "contact-form";
        public const string SourceQuoteForm = "quote-form";

        /// <summary>What the optional box shows for this account: consent recorded and still on.</summary>
        public static bool ShowsTicked(User user) => user.CanReceiveMessages && user.SmsConsentAt != null;

        /// <summary>
        /// Applies one optional-checkbox answer. <paramref name="boxShownTicked"/> is what the form
        /// showed (an anonymous checkout never shows it ticked, so it can only ADD consent).
        /// Null <paramref name="ticked"/> (a client that sent nothing) changes nothing. Returns true
        /// when the account changed.
        /// </summary>
        public static bool ApplyCheckbox(User user, bool? ticked, bool boxShownTicked, string source, DateTime nowUtc)
        {
            if (ticked == null) return false;
            if (ticked.Value)
            {
                if (ShowsTicked(user)) return false;
                Set(user, true, source, nowUtc);
                return true;
            }
            if (!boxShownTicked || !user.CanReceiveMessages) return false;
            Set(user, false, source, nowUtc);
            return true;
        }

        /// <summary>A direct change of the setting (profile / admin toggle): stamped only when it moves.</summary>
        public static void RecordChange(User user, bool canReceiveMessages, string source, DateTime nowUtc)
        {
            if (user.CanReceiveMessages == canReceiveMessages) return;
            Set(user, canReceiveMessages, source, nowUtc);
        }

        private static void Set(User user, bool value, string source, DateTime nowUtc)
        {
            user.CanReceiveMessages = value;
            user.SmsConsentAt = nowUtc;
            user.SmsConsentSource = source;
        }
    }
}

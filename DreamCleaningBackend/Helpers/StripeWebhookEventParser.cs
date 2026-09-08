using Microsoft.Extensions.Hosting;
using Stripe;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for turning a raw webhook request into a verified <see cref="Event"/>.
    ///
    /// Two things are decided here and nowhere else:
    ///
    ///  1. The Stripe SIGNATURE IS ALWAYS VERIFIED. There is no flag, no environment and no
    ///     configuration key that turns it off — an unsigned or wrongly-signed body is rejected in
    ///     Development exactly as it is in Production.
    ///
    ///  2. An API VERSION MISMATCH is tolerated in Development ONLY. Stripe.net pins the API
    ///     version it deserializes against (<see cref="StripeConfiguration.ApiVersion"/>) and
    ///     refuses an event formatted with any other one. The Stripe CLI (`stripe listen`) forwards
    ///     events using the CLI account's own default version, which is older than the pinned one,
    ///     so every locally forwarded event was rejected with a 400 before the handler ran — a
    ///     failure that looks exactly like a wrong signing secret and sent people hunting for one.
    ///
    /// Production keeps <c>throwOnApiVersionMismatch: true</c> deliberately. Its webhook
    /// destination is configured with the version Stripe.net expects, so a mismatch there means
    /// Stripe is sending a shape this build cannot deserialize correctly — a payment event silently
    /// misread is far worse than one rejected loudly, and Stripe retries a rejected delivery. This
    /// leniency path must therefore never be reachable in Production.
    /// </summary>
    public static class StripeWebhookEventParser
    {
        /// <summary>The API version this build of Stripe.net deserializes events against.</summary>
        public static string ExpectedApiVersion => StripeConfiguration.ApiVersion;

        /// <summary>
        /// Whether an event formatted with a different API version may still be processed.
        /// Development only — Staging and Production both reject, because anything that mirrors
        /// Production must fail the same way Production would.
        /// </summary>
        public static bool TolerateApiVersionMismatch(IHostEnvironment environment) =>
            environment.IsDevelopment();

        /// <summary>
        /// Verifies the signature and deserializes the event.
        /// </summary>
        /// <param name="tolerateApiVersionMismatch">
        /// From <see cref="TolerateApiVersionMismatch"/>. Affects ONLY the API-version check;
        /// the signature is verified either way.
        /// </param>
        /// <exception cref="StripeException">
        /// The signature is missing, malformed, wrong or outside the timestamp tolerance; or the
        /// event's API version differs from <see cref="ExpectedApiVersion"/> and mismatches are not
        /// tolerated.
        /// </exception>
        public static StripeWebhookEventResult Parse(
            string json,
            string stripeSignatureHeader,
            string webhookSecret,
            bool tolerateApiVersionMismatch)
        {
            // ConstructEvent verifies the signature FIRST and only then applies the version rule,
            // so passing the flag through cannot weaken the signature check. Letting the library
            // own both rules also keeps the version comparison from drifting away from the one
            // Stripe.net actually deserializes by.
            var stripeEvent = EventUtility.ConstructEvent(
                json,
                stripeSignatureHeader,
                webhookSecret,
                throwOnApiVersionMismatch: !tolerateApiVersionMismatch);

            // Only reachable with a mismatch when it was tolerated — Production threw above.
            var mismatched = !string.Equals(
                stripeEvent.ApiVersion, ExpectedApiVersion, StringComparison.Ordinal);

            return new StripeWebhookEventResult(stripeEvent, mismatched);
        }
    }

    /// <summary>A verified webhook event, plus whether its API version differed from the pinned one.</summary>
    public sealed class StripeWebhookEventResult
    {
        public StripeWebhookEventResult(Event stripeEvent, bool apiVersionMismatched)
        {
            Event = stripeEvent;
            ApiVersionMismatched = apiVersionMismatched;
        }

        public Event Event { get; }

        /// <summary>
        /// True when the event was formatted with an API version other than
        /// <see cref="StripeWebhookEventParser.ExpectedApiVersion"/> and was processed anyway.
        /// Always false in Production, where such an event is rejected instead.
        /// </summary>
        public bool ApiVersionMismatched { get; }

        /// <summary>The API version the event was formatted with, as Stripe sent it.</summary>
        public string? ReceivedApiVersion => Event.ApiVersion;
    }
}

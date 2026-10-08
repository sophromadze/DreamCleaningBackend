using System.Security.Cryptography;
using System.Text;
using DreamCleaningBackend.Helpers;
using Stripe;
using Stripe.Checkout;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE EVENT OBJECTS THE WEBHOOK HANDLERS READ, AS STRIPE.NET 53 PARSES THEM (2026-10).
    ///
    /// Stripe.net 51 replaced Json.NET with System.Text.Json for deserialization, and 53 pins
    /// 2026-09-30.endive. The handlers branch on a handful of fields — metadata keys, amounts,
    /// the intent a charge or session belongs to, card brand/last4/expiry, the failure message —
    /// and a field that silently deserialized as null would not fail; it would mis-settle. Each
    /// test signs an event exactly as Stripe sends it and reads the fields the handlers use.
    /// </summary>
    public class StripeWebhookPayloadTests
    {
        private const string Secret = "whsec_TESTONLYpayload0123456789abcdefghijk";

        [Fact]
        public void APaymentIntentSucceededEventCarriesWhatSettlementReads()
        {
            var intent = Parse<PaymentIntent>("payment_intent.succeeded", """
                {
                  "id": "pi_123", "object": "payment_intent", "amount": 38616, "amount_received": 38616,
                  "currency": "usd", "status": "succeeded", "customer": "cus_9",
                  "payment_method": "pm_7", "latest_charge": "ch_5", "setup_future_usage": "off_session",
                  "metadata": { "type": "booking", "orderId": "369", "userId": "42" }
                }
                """);

            Assert.Equal("pi_123", intent.Id);
            Assert.Equal(38616, intent.Amount);
            Assert.Equal(38616, intent.AmountReceived);
            Assert.Equal("usd", intent.Currency);
            Assert.Equal("succeeded", intent.Status);
            Assert.Equal("cus_9", intent.CustomerId);
            Assert.Equal("pm_7", intent.PaymentMethodId);
            Assert.Equal("ch_5", intent.LatestChargeId);
            Assert.Equal("off_session", intent.SetupFutureUsage);
            Assert.Equal("booking", intent.Metadata["type"]);
            Assert.Equal("369", intent.Metadata["orderId"]);
        }

        [Fact]
        public void APaymentFailedEventCarriesTheDeclineCodeAndMessage()
        {
            var intent = Parse<PaymentIntent>("payment_intent.payment_failed", """
                {
                  "id": "pi_124", "object": "payment_intent", "amount": 2500, "currency": "usd",
                  "status": "requires_payment_method", "metadata": { "type": "order_partial" },
                  "last_payment_error": { "type": "card_error", "code": "card_declined",
                                          "decline_code": "insufficient_funds",
                                          "message": "Your card has insufficient funds." }
                }
                """);

            Assert.Equal("card_declined", intent.LastPaymentError?.Code);
            Assert.Equal("insufficient_funds", intent.LastPaymentError?.DeclineCode);
            Assert.Equal("Your card has insufficient funds.", intent.LastPaymentError?.Message);
            Assert.Equal("order_partial", intent.Metadata["type"]);
        }

        [Fact]
        public void ACompletedCheckoutSessionNamesItsIntentAndMetadata()
        {
            var session = Parse<Session>("checkout.session.completed", """
                {
                  "id": "cs_1", "object": "checkout.session", "mode": "payment",
                  "status": "complete", "payment_status": "unpaid",
                  "payment_intent": "pi_ach_1", "customer": "cus_c1", "amount_total": 105000,
                  "metadata": { "type": "commercial_invoice", "invoice_id": "17", "payment_attempt_id": "3" }
                }
                """);

            Assert.Equal("cs_1", session.Id);
            Assert.Equal("complete", session.Status);
            Assert.Equal("unpaid", session.PaymentStatus);
            Assert.Equal("pi_ach_1", session.PaymentIntentId);
            Assert.Equal("cus_c1", session.CustomerId);
            Assert.Equal(105000, session.AmountTotal);
            Assert.Equal("17", session.Metadata["invoice_id"]);
        }

        [Fact]
        public void AnUpdatedCardCarriesItsNewExpiryAndLast4()
        {
            var method = Parse<PaymentMethod>("payment_method.automatically_updated", """
                {
                  "id": "pm_7", "object": "payment_method", "type": "card", "customer": "cus_9",
                  "card": { "brand": "visa", "last4": "4242", "exp_month": 12, "exp_year": 2031,
                            "funding": "credit", "wallet": null }
                }
                """);

            Assert.Equal("pm_7", method.Id);
            Assert.Equal("cus_9", method.CustomerId);
            Assert.Equal("visa", method.Card?.Brand);
            Assert.Equal("4242", method.Card?.Last4);
            Assert.Equal(12, method.Card?.ExpMonth);
            Assert.Equal(2031, method.Card?.ExpYear);
            Assert.Equal("credit", method.Card?.Funding);
        }

        [Fact]
        public void ADetachedCardHasNoCustomerAnyMore()
        {
            var method = Parse<PaymentMethod>("payment_method.detached", """
                { "id": "pm_8", "object": "payment_method", "type": "card", "customer": null,
                  "card": { "brand": "mastercard", "last4": "4444", "exp_month": 1, "exp_year": 2030 } }
                """);

            Assert.Null(method.CustomerId);
            Assert.Equal("4444", method.Card?.Last4);
        }

        private static T Parse<T>(string type, string objectJson) where T : class
        {
            var payload = $$"""
                {
                  "id": "evt_{{Guid.NewGuid():N}}", "object": "event",
                  "api_version": "{{StripeWebhookEventParser.ExpectedApiVersion}}",
                  "created": 1767225600, "livemode": false, "pending_webhooks": 1,
                  "request": { "id": null, "idempotency_key": null },
                  "type": "{{type}}",
                  "data": { "object": {{objectJson}} }
                }
                """;

            var parsed = StripeWebhookEventParser.Parse(payload, Sign(payload), Secret, tolerateApiVersionMismatch: false);

            Assert.Equal(type, parsed.Event.Type);
            return Assert.IsType<T>(parsed.Event.Data.Object);
        }

        private static string Sign(string payload)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
            return $"t={timestamp},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
        }
    }
}

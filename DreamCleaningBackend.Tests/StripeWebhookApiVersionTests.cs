using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stripe;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE WEBHOOK API-VERSION GATE.
    ///
    /// Stripe.net pins the API version it deserializes events against. The Stripe CLI
    /// (`stripe listen --forward-to …`) forwards events formatted with the CLI account's own
    /// default version, which is older, so every locally forwarded event was rejected with a 400
    /// before the handler ran. That failure is indistinguishable from a wrong signing secret from
    /// the outside, which is where the time went.
    ///
    /// The fix relaxes the API-VERSION check in Development only. What these tests exist to hold
    /// down is the shape of that exception:
    ///
    ///   - Production REJECTS an event on any other API version. Its webhook destination is pinned
    ///     to the version Stripe.net expects, so a mismatch there means Stripe is sending a shape
    ///     this build cannot read correctly — and a payment event silently misparsed is far worse
    ///     than one rejected loudly, since Stripe retries a rejection.
    ///   - SIGNATURE VERIFICATION IS NOT WEAKENED ANYWHERE. Development rejects a wrong signature,
    ///     a malformed header and a replayed timestamp exactly as Production does. The environment
    ///     flag reaches the version check and nothing else.
    ///   - Development SAYS SO when it accepts a mismatched event, so a field that deserialized as
    ///     null because of the version gap is traceable to the log line rather than to a hunt.
    ///
    /// NO TEST TOUCHES STRIPE. Every payload here is signed locally with an invented secret, using
    /// the documented scheme (`t=<unix>,v1=<hex hmac-sha256 of "t.payload">`), so the tests exercise
    /// the real verification path in Stripe.net rather than a stub of it.
    /// </summary>
    public class StripeWebhookApiVersionTests
    {
        /// <summary>Invented signing secret. Never a real one — shape only.</summary>
        private const string TestSecret = "whsec_TESTONLYtestonly0123456789abcdefghij";

        /// <summary>What the Stripe CLI forwarded as, and the version that started this.</summary>
        private const string StripeCliApiVersion = "2024-06-20";

        // ── Fixtures ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A minimal but structurally real webhook event. `customer.created` deliberately: the
        /// controller's switch sends it to `default:`, so these tests measure the version gate and
        /// not any payment handling behind it.
        /// </summary>
        private static string EventJson(string apiVersion, string eventId = "evt_test_apiversion")
        {
            return $$"""
            {
              "id": "{{eventId}}",
              "object": "event",
              "api_version": "{{apiVersion}}",
              "created": 1767225600,
              "livemode": false,
              "pending_webhooks": 1,
              "request": { "id": null, "idempotency_key": null },
              "type": "customer.created",
              "data": {
                "object": {
                  "id": "cus_test_apiversion",
                  "object": "customer",
                  "email": "webhook-fixture@example.invalid"
                }
              }
            }
            """;
        }

        /// <summary>Signs a payload the way Stripe does, so the real verifier accepts it.</summary>
        private static string Sign(string payload, string secret = TestSecret, DateTimeOffset? at = null)
        {
            var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
            return $"t={timestamp},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
        }

        private static IHostEnvironment Env(string name) =>
            new FakeEnvironment { EnvironmentName = name };

        // ── The pin itself ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The version Production's webhook destination must be configured with. Hardcoded ON
        /// PURPOSE: bumping Stripe.net moves the pin, and the only way anyone learns that the live
        /// destination now needs reconfiguring is this test failing. Do not "fix" it by reading the
        /// value back off Stripe.net — that is the check.
        /// </summary>
        [Fact]
        public void PinnedApiVersion_IsWhatTheProductionWebhookDestinationIsConfiguredWith()
        {
            Assert.Equal("2025-06-30.basil", StripeWebhookEventParser.ExpectedApiVersion);
        }

        // ── Who tolerates a mismatch ──────────────────────────────────────────────────────────

        [Fact]
        public void MismatchIsToleratedInDevelopmentOnly()
        {
            Assert.True(StripeWebhookEventParser.TolerateApiVersionMismatch(Env(Environments.Development)));

            // Staging mirrors Production, so it must fail the way Production would.
            Assert.False(StripeWebhookEventParser.TolerateApiVersionMismatch(Env(Environments.Production)));
            Assert.False(StripeWebhookEventParser.TolerateApiVersionMismatch(Env(Environments.Staging)));
            Assert.False(StripeWebhookEventParser.TolerateApiVersionMismatch(Env("QA")));
        }

        // ── Production rejects, Development accepts ───────────────────────────────────────────

        [Fact]
        public void Production_RejectsAnEventFormattedWithAnotherApiVersion()
        {
            var payload = EventJson(StripeCliApiVersion);

            var ex = Assert.Throws<StripeException>(() => StripeWebhookEventParser.Parse(
                payload, Sign(payload), TestSecret, tolerateApiVersionMismatch: false));

            // The rejection names both versions, so the log says what to reconfigure.
            Assert.Contains(StripeCliApiVersion, ex.Message);
            Assert.Contains(StripeWebhookEventParser.ExpectedApiVersion, ex.Message);
        }

        /// <summary>
        /// The other half of the previous test: Production is not rejecting everything. This is what
        /// proves the rejection above is about the API version and not about the fixture or the
        /// signing helper — the payloads differ in exactly one field.
        /// </summary>
        [Fact]
        public void Production_AcceptsAnEventOnThePinnedApiVersion()
        {
            var payload = EventJson(StripeWebhookEventParser.ExpectedApiVersion);

            var result = StripeWebhookEventParser.Parse(
                payload, Sign(payload), TestSecret, tolerateApiVersionMismatch: false);

            Assert.Equal("evt_test_apiversion", result.Event.Id);
            Assert.False(result.ApiVersionMismatched);
        }

        [Fact]
        public void Development_AcceptsTheStripeCliApiVersion_AndReportsTheMismatch()
        {
            var payload = EventJson(StripeCliApiVersion);

            var result = StripeWebhookEventParser.Parse(
                payload, Sign(payload), TestSecret, tolerateApiVersionMismatch: true);

            Assert.Equal("evt_test_apiversion", result.Event.Id);
            Assert.True(result.ApiVersionMismatched);
            Assert.Equal(StripeCliApiVersion, result.ReceivedApiVersion);
        }

        /// <summary>The warning must not cry wolf on a CLI that is already on the pinned version.</summary>
        [Fact]
        public void Development_ReportsNoMismatchWhenTheVersionsAgree()
        {
            var payload = EventJson(StripeWebhookEventParser.ExpectedApiVersion);

            var result = StripeWebhookEventParser.Parse(
                payload, Sign(payload), TestSecret, tolerateApiVersionMismatch: true);

            Assert.False(result.ApiVersionMismatched);
        }

        // ── Signature verification is never weakened ──────────────────────────────────────────

        [Fact]
        public void Development_StillRejectsASignatureFromTheWrongSecret()
        {
            var payload = EventJson(StripeCliApiVersion);
            var forged = Sign(payload, secret: "whsec_TESTONLYsomeoneelsessecret000000000");

            var ex = Assert.Throws<StripeException>(() => StripeWebhookEventParser.Parse(
                payload, forged, TestSecret, tolerateApiVersionMismatch: true));

            Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Development_StillRejectsABodyTamperedWithAfterSigning()
        {
            var signed = EventJson(StripeCliApiVersion);
            var signature = Sign(signed);
            var tampered = signed.Replace("cus_test_apiversion", "cus_attacker_00000");

            Assert.Throws<StripeException>(() => StripeWebhookEventParser.Parse(
                tampered, signature, TestSecret, tolerateApiVersionMismatch: true));
        }

        [Fact]
        public void Development_StillRejectsAMalformedSignatureHeader()
        {
            var payload = EventJson(StripeCliApiVersion);

            Assert.Throws<StripeException>(() => StripeWebhookEventParser.Parse(
                payload, "not-a-stripe-signature", TestSecret, tolerateApiVersionMismatch: true));
        }

        /// <summary>Replay protection is part of the signature check, so it survives too.</summary>
        [Fact]
        public void Development_StillRejectsASignatureOutsideTheTimestampTolerance()
        {
            var payload = EventJson(StripeCliApiVersion);
            var stale = Sign(payload, at: DateTimeOffset.UtcNow.AddHours(-2));

            Assert.Throws<StripeException>(() => StripeWebhookEventParser.Parse(
                payload, stale, TestSecret, tolerateApiVersionMismatch: true));
        }

        // ── The controller is actually wired to the environment ───────────────────────────────

        /// <summary>
        /// End to end through the endpoint, because a correct policy helper the controller does not
        /// consult would leave the 400s exactly where they were.
        /// </summary>
        [Fact]
        public async Task Endpoint_RejectsTheCliEventInProduction_AndAcceptsItInDevelopment()
        {
            var payload = EventJson(StripeCliApiVersion);
            var signature = Sign(payload);

            var production = await PostAsync(Environments.Production, payload, signature);
            var badRequest = Assert.IsType<BadRequestObjectResult>(production.Result);
            Assert.Contains("API version", badRequest.Value?.ToString() ?? string.Empty);

            var development = await PostAsync(Environments.Development, payload, signature);
            Assert.IsType<OkResult>(development.Result);
        }

        /// <summary>
        /// The Development warning must actually be emitted — it is the only thing that explains a
        /// field arriving null because of the version gap.
        /// </summary>
        [Fact]
        public async Task Endpoint_WarnsInDevelopmentWhenTheApiVersionDiffers()
        {
            var payload = EventJson(StripeCliApiVersion);
            var development = await PostAsync(Environments.Development, payload, Sign(payload));

            var warning = Assert.Single(development.Logger.Entries
                .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("API version")));

            Assert.Contains(StripeCliApiVersion, warning.Message);
            Assert.Contains(StripeWebhookEventParser.ExpectedApiVersion, warning.Message);
        }

        /// <summary>An event on the pinned version is processed silently, with nothing to explain.</summary>
        [Fact]
        public async Task Endpoint_DoesNotWarnWhenTheApiVersionMatches()
        {
            var payload = EventJson(StripeWebhookEventParser.ExpectedApiVersion);
            var development = await PostAsync(Environments.Development, payload, Sign(payload));

            Assert.IsType<OkResult>(development.Result);
            Assert.DoesNotContain(development.Logger.Entries,
                e => e.Level == LogLevel.Warning && e.Message.Contains("API version"));
        }

        [Fact]
        public async Task Endpoint_RejectsAWrongSignatureInDevelopmentToo()
        {
            var payload = EventJson(StripeWebhookEventParser.ExpectedApiVersion);
            var forged = Sign(payload, secret: "whsec_TESTONLYsomeoneelsessecret000000000");

            var development = await PostAsync(Environments.Development, payload, forged);

            Assert.IsType<BadRequestObjectResult>(development.Result);
        }

        // ── Endpoint harness ──────────────────────────────────────────────────────────────────

        private sealed record PostOutcome(IActionResult Result, CapturingLogger<StripeWebhookController> Logger);

        private static async Task<PostOutcome> PostAsync(
            string environmentName, string payload, string signature)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"stripe-webhook-{Guid.NewGuid()}")
                .Options;
            await using var context = new ApplicationDbContext(options);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Stripe:WebhookSecret"] = TestSecret
                })
                .Build();

            var logger = new CapturingLogger<StripeWebhookController>();
            var controller = new StripeWebhookController(
                configuration, context, new UnusedReconciler(), logger, Env(environmentName))
            {
                ControllerContext = new ControllerContext { HttpContext = NewRequest(payload, signature) }
            };

            return new PostOutcome(await controller.Handle(), logger);
        }

        private static DefaultHttpContext NewRequest(string payload, string signature)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            httpContext.Request.Headers["Stripe-Signature"] = signature;
            return httpContext;
        }

        /// <summary>The `default:` branch settles no money, so any call here is a test that drifted.</summary>
        private sealed class UnusedReconciler : IOrderPaymentStatusReconciler
        {
            public Task<AdditionalPaymentResult> ApplyStripeAdditionalPaymentAsync(
                int orderId, string paymentIntentId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("A customer.created event must not settle payments.");

            public Task<bool> ReconcileStatusAfterAdditionalPaymentAsync(
                int orderId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("A customer.created event must not reconcile an order.");
        }

        private sealed class CapturingLogger<T> : ILogger<T>
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private sealed class FakeEnvironment : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = Environments.Development;
            public string ApplicationName { get; set; } = "DreamCleaningBackend.Tests";
            public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        }
    }
}

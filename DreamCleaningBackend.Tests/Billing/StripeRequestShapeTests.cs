using System.Net;
using System.Text;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;
using Xunit;

namespace DreamCleaningBackend.Tests.Billing;

/// <summary>
/// WHAT STRIPE ACTUALLY RECEIVES (Stripe.net 53 / API 2026-09-30.endive, 2026-10).
///
/// Two SDK changes made the request a different thing from the C# that builds it:
///
///   - since Stripe.net 51 an options property ASSIGNED null is sent as an empty value
///     ("field=") — which on an update clears the field and on a create is a different request
///     from leaving it out. Our call sites pass null for "not applicable" all the time (no
///     customer, no receipt email, no description).
///   - endive refuses payment_method_types outright; the card-only / ACH-only restriction is
///     now allowed_payment_method_types.
///
/// These tests run the real StripeService against a recording HTTP client, so they see the
/// form body the SDK produces rather than our assumptions about it.
/// </summary>
[Collection(StripeGlobalStateCollection.Name)]
public sealed class StripeRequestShapeTests : IDisposable
{
    private readonly IStripeClient? _previousClient = StripeConfiguration.StripeClient;
    private readonly RecordingHandler _handler = new();
    private readonly StripeService _service;

    public StripeRequestShapeTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:SecretKey"] = "sk_test_recording" })
            .Build();

        // The constructor sets the global API key; the recording client is installed after it.
        _service = new StripeService(configuration, NullLogger<StripeService>.Instance);
        StripeConfiguration.StripeClient = new StripeClient(
            "sk_test_recording",
            httpClient: new SystemNetHttpClient(new HttpClient(_handler)));
    }

    public void Dispose() => StripeConfiguration.StripeClient = _previousClient;

    [Fact]
    public async Task AGuestPaymentIntentSendsNoEmptyFields_AndAllowsCardsOnly()
    {
        _handler.Respond("""{"id":"pi_1","object":"payment_intent","status":"requires_payment_method"}""");

        await _service.CreatePaymentIntentAsync(123.45m, metadata: null, receiptEmail: null, customerId: null);

        var form = _handler.SingleForm("/v1/payment_intents");
        Assert.Equal("12345", form["amount"]);
        Assert.Equal("usd", form["currency"]);
        Assert.Equal("card", form["allowed_payment_method_types[0]"]);
        AssertNoEmptyFields(form);
        Assert.DoesNotContain(form.Keys, k => k.StartsWith("payment_method_types", StringComparison.Ordinal));
        Assert.DoesNotContain("customer", form.Keys);
        Assert.DoesNotContain("receipt_email", form.Keys);
        Assert.DoesNotContain("setup_future_usage", form.Keys);
    }

    [Fact]
    public async Task ACustomerPaymentIntentThatSavesTheCardSendsThoseFields()
    {
        _handler.Respond("""{"id":"pi_1","object":"payment_intent","status":"requires_payment_method"}""");

        await _service.CreatePaymentIntentAsync(50m, new Dictionary<string, string> { ["orderId"] = "9" },
            receiptEmail: " owner@example.com ", customerId: "cus_1", saveCardForOffSession: true,
            idempotencyKey: "prepare-9");

        var form = _handler.SingleForm("/v1/payment_intents");
        Assert.Equal("cus_1", form["customer"]);
        Assert.Equal("owner@example.com", form["receipt_email"]);
        Assert.Equal("off_session", form["setup_future_usage"]);
        Assert.Equal("9", form["metadata[orderId]"]);
        Assert.Equal("prepare-9", _handler.Requests.Single().IdempotencyKey);
        AssertNoEmptyFields(form);
    }

    [Fact]
    public async Task ASetupIntentWithoutMetadataSendsNoEmptyFields()
    {
        _handler.Respond("""{"id":"seti_1","object":"setup_intent","status":"requires_payment_method"}""");

        await _service.CreateSetupIntentAsync("cus_1", metadata: null);

        var form = _handler.SingleForm("/v1/setup_intents");
        Assert.Equal("cus_1", form["customer"]);
        Assert.Equal("off_session", form["usage"]);
        Assert.Equal("card", form["allowed_payment_method_types[0]"]);
        AssertNoEmptyFields(form);
    }

    [Fact]
    public async Task AnOffSessionSavedCardChargeIsCardOnlyAndSendsNoEmptyFields()
    {
        _handler.Respond("""{"id":"pi_2","object":"payment_intent","status":"succeeded","amount":2500}""");

        await _service.ChargeSavedCardAsync(new SavedCardChargeRequest
        {
            Amount = 25m,
            CustomerId = "cus_1",
            PaymentMethodId = "pm_1",
            IdempotencyKey = "attempt-1",
            ReceiptEmail = null,
            Description = null,
            OffSession = true
        });

        var form = _handler.SingleForm("/v1/payment_intents");
        Assert.Equal("2500", form["amount"]);
        Assert.Equal("pm_1", form["payment_method"]);
        Assert.Equal("true", form["off_session"]);
        Assert.Equal("true", form["confirm"]);
        Assert.Equal("card", form["allowed_payment_method_types[0]"]);
        Assert.DoesNotContain(form.Keys, k => k.StartsWith("automatic_payment_methods", StringComparison.Ordinal));
        AssertNoEmptyFields(form);
    }

    [Fact]
    public async Task AFullRefundSendsNoAmount()
    {
        _handler.Respond("""{"id":"re_1","object":"refund","status":"succeeded"}""");

        await _service.CreateRefundAsync("pi_1");

        var form = _handler.SingleForm("/v1/refunds");
        Assert.Equal("pi_1", form["payment_intent"]);
        Assert.DoesNotContain("amount", form.Keys);
        AssertNoEmptyFields(form);
    }

    [Fact]
    public async Task ARefundWithMetadataCarriesIt()
    {
        _handler.Respond("""{"id":"re_1","object":"refund","status":"succeeded"}""");

        await _service.CreateRefundAsync("pi_1", 12.5m, "refund-1", new Dictionary<string, string> { ["orderId"] = "4" });

        var form = _handler.SingleForm("/v1/refunds");
        Assert.Equal("1250", form["amount"]);
        Assert.Equal("4", form["metadata[orderId]"]);
        AssertNoEmptyFields(form);
    }

    [Fact]
    public async Task ANoEmailCustomerIsCreatedWithoutAnEmailField()
    {
        _handler.Respond("""{"id":"cus_9","object":"customer"}""");

        var id = await _service.CreateOrGetCustomerAsync(new Models.User
        {
            Id = 9, FirstName = "Cash", LastName = "Customer",
            Email = "9@no-email.invalid", IsNoEmailUser = true
        });

        Assert.Equal("cus_9", id);
        var form = _handler.SingleForm("/v1/customers");
        Assert.Equal("Cash Customer", form["name"]);
        Assert.Equal("9", form["metadata[userId]"]);
        Assert.DoesNotContain("email", form.Keys);
        AssertNoEmptyFields(form);
    }

    [Fact]
    public async Task ACustomerWithAnEmailIsCreatedWithIt()
    {
        _handler.Respond("""{"id":"cus_8","object":"customer"}""");

        await _service.CreateOrGetCustomerAsync(new Models.User
        {
            Id = 8, FirstName = "Casey", LastName = "Client", Email = "casey@example.com"
        });

        Assert.Equal("casey@example.com", _handler.SingleForm("/v1/customers")["email"]);
    }

    [Fact]
    public async Task UpdatingMetadataWithNothingSendsNothing()
    {
        // On an update "metadata=" would wipe every key the intent already carries.
        await _service.UpdatePaymentIntentMetadataAsync("pi_1", null!);
        await _service.UpdatePaymentIntentMetadataAsync("pi_1", new Dictionary<string, string>());

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task UpdatingMetadataSendsOnlyTheGivenKeys()
    {
        _handler.Respond("""{"id":"pi_1","object":"payment_intent","status":"succeeded"}""");

        await _service.UpdatePaymentIntentMetadataAsync("pi_1", new Dictionary<string, string> { ["orderId"] = "12" });

        var form = _handler.SingleForm("/v1/payment_intents/pi_1");
        Assert.Equal("12", form["metadata[orderId]"]);
        AssertNoEmptyFields(form);
    }

    [Fact]
    public void TheSdkPinsTheEndiveApiVersion()
    {
        Assert.Equal("2026-09-30.endive", StripeConfiguration.ApiVersion);
    }

    private static void AssertNoEmptyFields(IReadOnlyDictionary<string, string> form)
    {
        var empty = form.Where(kv => kv.Value.Length == 0).Select(kv => kv.Key).ToList();
        Assert.True(empty.Count == 0, "Empty form fields would clear or reject values: " + string.Join(", ", empty));
    }

    private sealed record RecordedRequest(string Path, Dictionary<string, string> Form, string? IdempotencyKey);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private string _response = "{}";

        public List<RecordedRequest> Requests { get; } = new();

        public void Respond(string json) => _response = json;

        public Dictionary<string, string> SingleForm(string path)
        {
            var request = Assert.Single(Requests);
            Assert.Equal(path, request.Path);
            return request.Form;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => p.Length > 1 ? WebUtility.UrlDecode(p[1]) : string.Empty);
            request.Headers.TryGetValues("Idempotency-Key", out var keys);
            Requests.Add(new RecordedRequest(request.RequestUri!.AbsolutePath, form, keys?.FirstOrDefault()));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            };
        }
    }
}

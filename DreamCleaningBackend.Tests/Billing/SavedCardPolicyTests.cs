using System.Net;
using DreamCleaningBackend.Helpers.Billing;
using DreamCleaningBackend.Models.Billing;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Billing;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;
using Xunit;

namespace DreamCleaningBackend.Tests.Billing;

/// <summary>
/// The pure rules behind saved cards and AutoPay — no database, no Stripe. The money paths
/// themselves are exercised against real MariaDB in <see cref="SavedCardChargeIntegrationTests"/>.
/// </summary>
[Collection(StripeGlobalStateCollection.Name)]
public class SavedCardPolicyTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningBackend.Tests"))) dir = dir.Parent;
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    // ── Classifying Stripe's answers: only a PROVEN refusal is a decline ─────────────────────

    private static StripeService NewStripeService() =>
        new(new ConfigurationBuilder().Build(), NullLogger<StripeService>.Instance);

    private static SavedCardChargeRequest Request() => new() { IdempotencyKey = "k", Amount = 10m };

    [Fact]
    public void ACardError_IsADecline_WithItsCodes()
    {
        var ex = new StripeException(HttpStatusCode.PaymentRequired,
            new StripeError { Type = "card_error", Code = "card_declined", DeclineCode = "insufficient_funds" }, "declined");
        var result = NewStripeService().MapChargeException(ex, Request());
        Assert.Equal(SavedCardChargeOutcome.Declined, result.Outcome);
        Assert.Equal("insufficient_funds", result.DeclineCode);
    }

    [Fact]
    public void AuthenticationRequired_IsRequiresAction_NotADecline()
    {
        var ex = new StripeException(HttpStatusCode.PaymentRequired,
            new StripeError { Type = "card_error", Code = "authentication_required" }, "auth");
        Assert.Equal(SavedCardChargeOutcome.RequiresAction, NewStripeService().MapChargeException(ex, Request()).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "api_error")]
    [InlineData(HttpStatusCode.BadGateway, null)]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit_error")]
    [InlineData(HttpStatusCode.BadRequest, "idempotency_error")]
    public void AnAnswerThatLeavesTheOutcomeOpen_IsUnknown_NeverADecline(HttpStatusCode status, string? type)
    {
        // Treating any of these as a decline would hand the obligation to the Backup card while
        // the first charge may still have succeeded — two collections for one payment.
        var ex = new StripeException(status, type == null ? null : new StripeError { Type = type }, "boom");
        Assert.Equal(SavedCardChargeOutcome.Unknown, NewStripeService().MapChargeException(ex, Request()).Outcome);
    }

    [Fact]
    public void ATransportFailureWithNoStripeError_IsUnknown()
    {
        var ex = new StripeException("Error while communicating with one of our backends.");
        Assert.Equal(SavedCardChargeOutcome.Unknown, NewStripeService().MapChargeException(ex, Request()).Outcome);
    }

    [Theory]
    [InlineData("succeeded", SavedCardChargeOutcome.Succeeded)]
    [InlineData("processing", SavedCardChargeOutcome.Processing)]
    [InlineData("requires_action", SavedCardChargeOutcome.RequiresAction)]
    [InlineData("requires_payment_method", SavedCardChargeOutcome.Declined)]
    [InlineData("canceled", SavedCardChargeOutcome.Declined)]
    public void IntentStatus_MapsToOutcome(string status, SavedCardChargeOutcome expected)
    {
        Assert.Equal(expected, StripeService.MapIntent(new PaymentIntent { Id = "pi_1", Status = status }).Outcome);
    }

    [Theory]
    [InlineData(386.16, 38616)]
    [InlineData(0.50, 50)]
    [InlineData(2743.655, 274366)]
    public void Cents_AreRounded_NeverTruncated(decimal dollars, long cents) =>
        Assert.Equal(cents, StripeService.ToCents(dollars));

    // ── Card expiry: unknown is not expired ───────────────────────────────────────────────────

    [Fact]
    public void ACardExpires_AfterTheLastDayOfItsMonth()
    {
        var card = new CustomerPaymentMethod { ExpMonth = 2, ExpYear = 2027 };
        Assert.False(card.IsExpiredAt(new DateTime(2027, 2, 28)));
        Assert.True(card.IsExpiredAt(new DateTime(2027, 3, 1)));
    }

    [Fact]
    public void UnknownExpiry_IsNeverTreatedAsExpired()
    {
        // Legacy one-card rows carry no expiry; reading "unknown" as "expired" would silently
        // switch AutoPay off for every migrated customer.
        Assert.False(new CustomerPaymentMethod().IsExpiredAt(DateTime.UtcNow.AddYears(20)));
    }

    [Theory]
    [InlineData("stolen_card")]
    [InlineData("lost_card")]
    [InlineData("pickup_card")]
    [InlineData("revocation_of_all_authorizations")]
    public void IssuerNeverRetryCodes_BlockTheCard(string code) =>
        Assert.Contains(code, DreamCleaningBackend.Services.Billing.PaymentMethodService.NeverRetryDeclineCodes);

    [Theory]
    [InlineData("insufficient_funds")]
    [InlineData("generic_decline")]
    public void OrdinaryDeclines_DoNotBlockTheCard(string code) =>
        Assert.DoesNotContain(code, DreamCleaningBackend.Services.Billing.PaymentMethodService.NeverRetryDeclineCodes);

    // ── The authorisation wording says what the code does ────────────────────────────────────

    [Fact]
    public void RecurringTerms_PromiseOneCleaningAtATime_AndTheTwentyFourHourGap()
    {
        var text = AutoPayTerms.Render(PaymentAuthorizationScope.RecurringSeries, new AutoPayTermsContext());
        Assert.Contains("ONE cleaning at a time", text);
        Assert.Contains("24 hours after the previous cleaning", text);
        Assert.Contains("never charge several upcoming cleanings at once", text);
    }

    [Fact]
    public void BackupWording_FollowsTheChoice()
    {
        var with = AutoPayTerms.Render(PaymentAuthorizationScope.RecurringSeries, new AutoPayTermsContext { AllowBackupFallback = true });
        var without = AutoPayTerms.Render(PaymentAuthorizationScope.RecurringSeries, new AutoPayTermsContext { AllowBackupFallback = false });
        Assert.Contains("try your Backup card once", with);
        Assert.Contains("never be charged twice", with);
        Assert.Contains("Backup card: not used", without);
    }

    [Fact]
    public void GeneralTerms_AuthoriseNothingOnTheirOwn()
    {
        var text = AutoPayTerms.Render(PaymentAuthorizationScope.General, new AutoPayTermsContext());
        Assert.Contains("does not, by itself, authorize any charge", text);
    }

    [Fact]
    public void CommercialTerms_KeepTheNetTerms_AndNeverDebitABank()
    {
        var text = AutoPayTerms.Render(PaymentAuthorizationScope.CommercialClient, new AutoPayTermsContext { ClientName = "Acme LLC" });
        Assert.Contains("Acme LLC", text);
        Assert.Contains("nothing is charged before the due date", text);
        Assert.Contains("never automatically debit a bank account", text);
        Assert.Contains("at most once", text);
        Assert.Contains("no card fee is added", text);
    }

    [Fact]
    public void OfficeTerms_CarryTheThreeBookingConsents_VerbatimFromTheBookingForm()
    {
        // The office scope stands in for the booking form an office-booked customer never saw, so
        // its consents must read EXACTLY as the booking form's do (consent-texts.ts).
        var ts = System.IO.File.ReadAllText(RepoFile("..", "DreamCleaningNG", "src", "app", "shared", "booking", "consent-texts.ts"))
            .Replace("\r\n", "\n");
        string Concat(string name)
        {
            var start = ts.IndexOf($"export const {name} =", StringComparison.Ordinal);
            var end = ts.IndexOf(";\n", start, StringComparison.Ordinal);
            var body = ts[start..end];
            return string.Concat(System.Text.RegularExpressions.Regex.Matches(body, "'([^']*)'|\"([^\"]*)\"")
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
        }

        Assert.Equal(Concat("CANCELLATION_CONSENT_TEXT"), AutoPayTerms.CancellationConsentText);
        Assert.StartsWith(Concat("SMS_CONSENT_LEAD").TrimEnd(), AutoPayTerms.SmsConsentText);
        Assert.StartsWith(Concat("TERMS_CONSENT_LEAD").TrimEnd(), AutoPayTerms.TermsConsentText);

        var office = AutoPayTerms.Render(PaymentAuthorizationScope.OfficeBookedOrders, new AutoPayTermsContext());
        Assert.Contains(AutoPayTerms.CancellationConsentText, office);
        Assert.Contains("Nothing is charged automatically under this authorization", office);
    }

    [Fact]
    public void TheTermsHash_IsStable()
    {
        var a = AutoPayTerms.Render(PaymentAuthorizationScope.General, new AutoPayTermsContext());
        Assert.Equal(AutoPayTerms.Hash(a), AutoPayTerms.Hash(a));
        Assert.Equal(64, AutoPayTerms.Hash(a).Length);
    }

    // ── Source guards: one path charges saved cards, and success never waits for card saving ──

    [Fact]
    public void OnlyTheChargeServiceChargesASavedCard()
    {
        var root = RepoFile("DreamCleaningBackend");
        var offenders = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => System.IO.File.ReadAllText(f).Contains("ChargeSavedCardAsync("))
            .Select(Path.GetFileName)
            .Where(n => n is not ("SavedCardChargeService.cs" or "StripeService.cs" or "IStripeService.cs"))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void ConfirmPayment_SavesTheCardDetached_NeverInline()
    {
        // Saving the card must never be awaited on the confirmation path: the ONLY call to it in
        // the controller sits inside the detached BackgroundWork block, with its own DI scope.
        var source = System.IO.File.ReadAllText(RepoFile("DreamCleaningBackend", "Controllers", "BookingController.cs"));
        var block = source.IndexOf("BackgroundWork.Run(_scopeFactory, _logger, $\"save card from", StringComparison.Ordinal);
        Assert.True(block > 0, "the card save must run through BackgroundWork.Run");
        var calls = System.Text.RegularExpressions.Regex.Matches(source, @"SaveFromPaymentIntentAsync\(").Select(m => m.Index).ToList();
        var call = Assert.Single(calls);
        Assert.InRange(call, block, block + 600);
        Assert.DoesNotContain("TrySaveCardFromPaymentIntentAsync", source);
    }

    [Fact]
    public void AdminCharge_NoLongerChargesTheOrderTotal()
    {
        var source = System.IO.File.ReadAllText(RepoFile("DreamCleaningBackend", "Controllers", "Admin", "AdminOrdersController.cs"));
        Assert.DoesNotContain("CreateOffSessionPaymentIntentAsync", source);
        Assert.Contains("BillingAttemptTrigger.AdminCharge", source);
    }

    [Fact]
    public void BillingAuditTypes_AreUndoBlocked()
    {
        foreach (var type in new[] { AuditEntityTypes.CustomerPaymentMethodAction, AuditEntityTypes.PaymentAuthorizationAction,
                     AuditEntityTypes.SavedCardChargeAction })
            Assert.True(AuditEntityTypes.UndoBlockedReasons.ContainsKey(type), type);
    }

    [Fact]
    public void TheRolloutSwitches_DefaultOff()
    {
        var features = new BillingFeatures(new ConfigurationBuilder().Build());
        Assert.False(features.SavedCardsEnabled);
        Assert.False(features.AutoPayEnabled);

        // AutoPay without saved cards is meaningless and stays off.
        var onlyAutoPay = new BillingFeatures(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Billing:AutoPayEnabled"] = "true" }).Build());
        Assert.False(onlyAutoPay.AutoPayEnabled);
    }
}

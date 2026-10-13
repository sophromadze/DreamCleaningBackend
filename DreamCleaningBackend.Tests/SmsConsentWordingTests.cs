using System.Text.RegularExpressions;
using DreamCleaningBackend.Helpers.Billing;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// The optional SMS consent text (2026-10, RingCentral 10DLC), held to its exact wording on every form
/// that shows it: the registered name (Dream Cleaning NYC), "Reply HELP for assistance" with NO number (a reply
/// goes to the RingCentral sender, config RingCentral:FromNumber, which can differ from the support
/// number on the site), and the two policy pages named by their actual titles - the same titles the
/// terms consent line and the office AutoPay terms use.
/// </summary>
public class SmsConsentWordingTests
{
    private const string Brand = "Dream Cleaning NYC";
    private const string Tail =
        "You may reply STOP to opt out at any time. Reply HELP for assistance. Messages and data rates may apply. " +
        "Message frequency will vary. Learn more on our Privacy Policy page and Terms & Conditions.";

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(SourceTree.TestsProjectDir);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningBackend.Tests"))) dir = dir.Parent;
        return Path.Combine(new[] { dir!.FullName, "..", "DreamCleaningNG", "src", "app" }.Concat(parts).ToArray());
    }

    private static string Read(params string[] parts) => File.ReadAllText(RepoFile(parts)).Replace("\r\n", "\n");

    private static string ConsentText(string name)
    {
        var ts = Read("shared", "booking", "consent-texts.ts");
        var start = ts.IndexOf($"export const {name} =", StringComparison.Ordinal);
        Assert.True(start >= 0, name + " missing");
        var end = ts.IndexOf(";\n", start, StringComparison.Ordinal);
        return string.Concat(Regex.Matches(ts[start..end], "'([^']*)'|\"([^\"]*)\"")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
    }

    /// <summary>The label starting at <paramref name="marker"/> as it reads on screen.</summary>
    private static string Label(string html, string marker)
    {
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, marker + " not found");
        var label = html[start..html.IndexOf("</label>", start, StringComparison.Ordinal)];
        label = Regex.Replace(label, "^<label[^>]*>", "")
            .Replace("{{ smsConsentLead }}", ConsentText("SMS_CONSENT_LEAD"))
            .Replace("{{ termsConsentLead }}", ConsentText("TERMS_CONSENT_LEAD"));
        label = Regex.Replace(label, @"<span class=""required"">\*</span>", "");
        label = Regex.Replace(label, "<[^>]+>", "").Replace("&amp;", "&");
        return Regex.Replace(label, @"\s+", " ").Trim();
    }

    private static string Heading(params string[] page) =>
        Regex.Match(Read(page), "<h1>([^<]*)</h1>").Groups[1].Value.Replace("&amp;", "&");

    [Fact]
    public void ThePolicyLinks_UseTheTitlesOfThePagesTheyOpen()
    {
        Assert.Equal("Privacy Policy", Heading("privacy-policy", "privacy-policy.component.html"));
        Assert.Equal("Terms & Conditions", Heading("terms-and-conditions", "terms-and-conditions.component.html"));
    }

    [Fact]
    public void BookingAndPaymentPage_ShowTheExactSmsAndTermsWording()
    {
        var sms = $"By checking this box, you agree to receive SMS messages from {Brand} related to your booking, " +
                  "including booking confirmations, appointment reminders and updates, and payment notices. " + Tail;
        foreach (var page in new[] { new[] { "booking", "booking.component.html" },
                                     new[] { "auth", "profile", "order-payment", "order-payment.component.html" } })
        {
            var html = Read(page);
            Assert.Equal(sms, Label(html, "<label for=\"" + (page[0] == "booking" ? "smsConsent" : "payment-sms-consent") + "\""));
            Assert.Equal(AutoPayTerms.TermsConsentText, Label(html, "{{ termsConsentLead }}"));
        }
    }

    [Fact]
    public void ContactAndQuoteForms_ShowTheSameSentence_ForTheirOwnRequest()
    {
        Assert.Equal(
            $"By checking this box, you agree to receive SMS messages from {Brand} related to your inquiry, " +
            "booking confirmations, appointment reminders and updates, and payment notices. " + Tail,
            Label(Read("contact", "contact.component.html"), "<label for=\"smsConsent\""));
        Assert.Equal(
            $"By checking this box, you agree to receive SMS messages from {Brand} related to your quote request, " +
            "booking confirmations, appointment reminders and updates, and payment notices. " + Tail,
            Label(Read("free-quote", "free-quote.component.html"), "<label for=\"smsConsent\""));
    }

    [Fact]
    public void NoSmsLabel_NamesAPhoneNumber()
    {
        foreach (var (page, marker) in new[]
                 {
                     (new[] { "booking", "booking.component.html" }, "<label for=\"smsConsent\""),
                     (new[] { "auth", "profile", "order-payment", "order-payment.component.html" }, "<label for=\"payment-sms-consent\""),
                     (new[] { "contact", "contact.component.html" }, "<label for=\"smsConsent\""),
                     (new[] { "free-quote", "free-quote.component.html" }, "<label for=\"smsConsent\"")
                 })
        {
            var html = Read(page);
            var start = html.IndexOf(marker, StringComparison.Ordinal);
            var raw = html[start..html.IndexOf("</label>", start, StringComparison.Ordinal)];
            Assert.DoesNotContain("phoneNumber", raw);
            Assert.DoesNotMatch(@"\d{3}\)?[ .-]\d{3}-\d{4}", raw);
        }
    }
}

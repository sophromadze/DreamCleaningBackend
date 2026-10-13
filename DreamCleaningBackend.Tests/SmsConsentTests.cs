using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// SMS consent is OPTIONAL (2026-10, RingCentral 10DLC) and never a condition of anything. The
/// stored answer is User.CanReceiveMessages (+ when/where it last changed); the optional boxes
/// record it, and SmsService skips a customer who opted out.
/// </summary>
public class SmsConsentTests
{
    private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString(), b => b.EnableNullChecks(false))
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    // ── The checkbox rules ──

    [Fact]
    public void ANeverRecordedAccount_ShowsTheBoxUnticked_EvenThoughSmsIsOn()
    {
        var legacy = new User { CanReceiveMessages = true };          // every account before 2026-10
        Assert.False(SmsConsentRules.ShowsTicked(legacy));
        Assert.True(SmsConsentRules.ShowsTicked(new User { CanReceiveMessages = true, SmsConsentAt = Now }));
        Assert.False(SmsConsentRules.ShowsTicked(new User { CanReceiveMessages = false, SmsConsentAt = Now }));
    }

    [Fact]
    public void Ticking_RecordsConsent_WithTimeAndSource()
    {
        var user = new User { CanReceiveMessages = true };
        Assert.True(SmsConsentRules.ApplyCheckbox(user, true, boxShownTicked: false, SmsConsentRules.SourceBooking, Now));
        Assert.True(user.CanReceiveMessages);
        Assert.Equal(Now, user.SmsConsentAt);
        Assert.Equal("booking", user.SmsConsentSource);

        var optedOut = new User { CanReceiveMessages = false, SmsConsentAt = Now.AddDays(-3), SmsConsentSource = "profile" };
        Assert.True(SmsConsentRules.ApplyCheckbox(optedOut, true, false, SmsConsentRules.SourcePaymentPage, Now));
        Assert.True(optedOut.CanReceiveMessages);
        Assert.Equal("payment-page", optedOut.SmsConsentSource);
    }

    [Fact]
    public void LeavingItUnticked_ChangesNothing_SmsStaysOn()
    {
        var user = new User { CanReceiveMessages = true };
        Assert.False(SmsConsentRules.ApplyCheckbox(user, false, boxShownTicked: false, SmsConsentRules.SourceBooking, Now));
        Assert.True(user.CanReceiveMessages);
        Assert.Null(user.SmsConsentAt);
    }

    [Fact]
    public void UntickingABoxThatWasShownTicked_IsAnExplicitOptOut()
    {
        var user = new User { CanReceiveMessages = true, SmsConsentAt = Now.AddDays(-30), SmsConsentSource = "booking" };
        Assert.True(SmsConsentRules.ApplyCheckbox(user, false, boxShownTicked: true, SmsConsentRules.SourceBooking, Now));
        Assert.False(user.CanReceiveMessages);
        Assert.Equal(Now, user.SmsConsentAt);
    }

    [Fact]
    public void AnAnonymousCheckout_CanOnlyAddConsent_NeverRevokeIt()
    {
        // A guest checkout never sees the box ticked: an unticked submit from it leaves the
        // account exactly as it was, whatever that account had recorded.
        var user = new User { CanReceiveMessages = true, SmsConsentAt = Now.AddDays(-30) };
        Assert.False(SmsConsentRules.ApplyCheckbox(user, false, boxShownTicked: false, SmsConsentRules.SourceBooking, Now));
        Assert.True(user.CanReceiveMessages);
    }

    [Fact]
    public void AClientThatSentNothing_ChangesNothing()
    {
        var user = new User { CanReceiveMessages = true, SmsConsentAt = Now.AddDays(-1) };
        Assert.False(SmsConsentRules.ApplyCheckbox(user, null, true, SmsConsentRules.SourceBooking, Now));
        Assert.True(user.CanReceiveMessages);
    }

    [Fact]
    public void ProfileAndAdminToggles_AreStampedOnlyWhenTheyMove()
    {
        var user = new User { CanReceiveMessages = true };
        SmsConsentRules.RecordChange(user, true, SmsConsentRules.SourceAdmin, Now);
        Assert.Null(user.SmsConsentAt);

        SmsConsentRules.RecordChange(user, false, SmsConsentRules.SourceProfile, Now);
        Assert.False(user.CanReceiveMessages);
        Assert.Equal(Now, user.SmsConsentAt);
        Assert.Equal("profile", user.SmsConsentSource);
    }

    // ── SmsService respects it ──

    private static SmsService Sms(ApplicationDbContext db, RecordingLogger<SmsService> logger) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RingCentral:EnableSmsSending"] = "true", ["RingCentral:FromNumber"] = "+12125550100",
            ["RingCentral:ClientId"] = "test", ["RingCentral:ClientSecret"] = "test",
            ["RingCentral:ServerUrl"] = "https://example.invalid", ["RingCentral:JwtToken"] = "test"
        }).Build(), logger, db);

    private static async Task<bool> Skipped(ApplicationDbContext db, string phone)
    {
        var logger = new RecordingLogger<SmsService>();
        try { await Sms(db, logger).SendSmsAsync(phone, "Your cleaning is confirmed"); }
        catch { /* not skipped: it went on to RingCentral, which does not exist here */ }
        return logger.Entries.Any(e => e.Message.Contains("opted out"));
    }

    [Fact]
    public async Task ACustomerWhoOptedOut_GetsNoSms_ByProfilePhoneOrOrderContactPhone()
    {
        using var db = Db();
        var customer = new User { Id = 1, Email = "a@example.invalid", Phone = "(212) 555-0101", Role = UserRole.Customer, IsActive = true, CanReceiveMessages = false };
        db.Users.Add(customer);
        db.Orders.Add(new Order { Id = 10, UserId = 1, User = customer, ContactPhone = "212-555-0199" });
        await db.SaveChangesAsync();

        Assert.True(await Skipped(db, "+12125550101"));
        Assert.True(await Skipped(db, "2125550199"));
    }

    [Fact]
    public async Task ConsentingCustomers_StaffAndCleaners_AreNeverHeldBackByIt()
    {
        using var db = Db();
        db.Users.AddRange(
            new User { Id = 1, Email = "c@example.invalid", Phone = "2125550101", Role = UserRole.Customer, IsActive = true, CanReceiveMessages = true },
            new User { Id = 2, Email = "s@example.invalid", Phone = "2125550102", Role = UserRole.Admin, IsActive = true, CanReceiveMessages = false },
            new User { Id = 3, Email = "k@example.invalid", Phone = "2125550103", Role = UserRole.Cleaner, IsActive = true, CanReceiveMessages = false });
        await db.SaveChangesAsync();

        Assert.False(await Skipped(db, "2125550101"));
        Assert.False(await Skipped(db, "2125550102"));
        Assert.False(await Skipped(db, "2125550103"));
        Assert.False(await Skipped(db, "2125550999"));
    }

    // ── Contact / quote leads ──

    [Fact]
    public async Task ALead_StoresTheOptionalBox_AndAnUntickedRepeatNeverClearsIt()
    {
        using var db = Db();
        var capture = new LeadCaptureService(db, NullLogger<LeadCaptureService>.Instance);

        var unticked = await capture.CaptureAsync(LeadSource.ContactForm, "Ann", "Lee", "ann@example.invalid", "2125550111", message: "Hi", smsConsent: false);
        Assert.False(unticked!.SmsConsent);
        Assert.Null(unticked.SmsConsentAt);

        var ticked = await capture.CaptureAsync(LeadSource.QuoteRequest, "Ann", "Lee", "ann@example.invalid", "2125550111", message: "Quote", smsConsent: true);
        Assert.Equal(unticked.Id, ticked!.Id);
        Assert.True(ticked.SmsConsent);
        Assert.NotNull(ticked.SmsConsentAt);

        var again = await capture.CaptureAsync(LeadSource.ContactForm, "Ann", "Lee", "ann@example.invalid", "2125550111", message: "Again", smsConsent: false);
        Assert.True(again!.SmsConsent);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}

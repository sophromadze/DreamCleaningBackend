using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DreamCleaningBackend.Tests;

public class AuditPrivacyRegressionTests
{
    private static AuditService Audit(ApplicationDbContext db) => new(db, new HttpContextAccessor(), NullLogger<AuditService>.Instance);

    [Fact]
    public async Task EmptyToNullAndCredentialOnlyUpdatesDoNotWriteEmptyAuditRows()
    {
        using var db = RecurringDiscountRegressionTests.Db(); var audit = Audit(db);
        await audit.LogUpdateAsync(new User { Id = 1, Email = null, PasswordResetToken = "before" },
            new User { Id = 1, Email = "", PasswordResetToken = "after" });
        await audit.LogActionAsync("User", 1, "Update", new { LoginOtpCode = "before" }, new { LoginOtpCode = "after" });
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task ActionSupportsRecurringVerbsAndCreateStoresOnlyNonSecretScalars()
    {
        using var db = RecurringDiscountRegressionTests.Db();
        Assert.Equal(128, db.Model.FindEntityType(typeof(AuditLog))!.FindProperty(nameof(AuditLog.Action))!.GetMaxLength());
        await Audit(db).LogCreateAsync(new Order { Id = 7, UserId = 4, Total = 125.55m, PaymentAccessToken = "do-not-store",
            User = new User { FirstName = "Nested", PasswordResetToken = "nested-secret" } });
        var row = await db.AuditLogs.SingleAsync();
        var snapshot = JObject.Parse(row.NewValues!);
        Assert.Null(row.ChangedFields); Assert.Equal(125.55m, (decimal)snapshot["Total"]!);
        foreach (var field in new[] { "PaymentAccessToken", "User", "Apartment", "OrderServices", "OrderCleaners" }) Assert.Null(snapshot[field]);
        Assert.DoesNotContain("do-not-store", row.NewValues!);
    }

    [Theory]
    [InlineData("PaymentAccessToken")]
    [InlineData("PasswordHash")]
    [InlineData("PasswordSalt")]
    [InlineData("PasswordResetToken")]
    [InlineData("EmailVerificationTokenExpiry")]
    [InlineData("LastEmailVerificationTokenHash")]
    [InlineData("EmailChangeToken")]
    [InlineData("LoginOtpCode")]
    [InlineData("TwoFactorPinHash")]
    [InlineData("EmailCodeHash")]
    [InlineData("RefreshSession")]
    [InlineData("RefreshTokenExpiryTime")]
    [InlineData("ApiKey")]
    [InlineData("ClientSecret")]
    [InlineData("WebhookSecret")]
    [InlineData("PrivateKeyPem")]
    public async Task EveryWriterAndLegacyReadProjectionStripCredentialValues(string field)
    {
        using var db = RecurringDiscountRegressionTests.Db();
        var payload = new JObject { [field] = "secret-sentinel", ["Nested"] = new JObject { [field] = "secret-sentinel" }, ["Total"] = 75 };
        var raw = payload.ToString();
        var row = new AuditLog { Id = 1, EntityType = "Order", Action = "Create", NewValues = raw };
        db.AuditLogs.Add(row); await db.SaveChangesAsync();
        Assert.DoesNotContain("secret-sentinel", row.NewValues!);
        var legacy = new AuditLog { Id = 2, EntityType = "Order", NewValues = raw };
        var display = (await AuditDisplayProjection.BuildAsync(db, new[] { legacy }))[2];
        Assert.DoesNotContain("secret-sentinel", display.NewValues!); Assert.Equal(raw, legacy.NewValues);
        Assert.Equal(75, (int)JObject.Parse(display.NewValues!)["Total"]!);
    }

    [Fact]
    public void SettingsAndEmbeddedJsonAreSanitizedWithoutHidingBusinessDocumentHashes()
    {
        var safe = AuditDataPolicy.SanitizeJson("{\"Key\":\"ApiKey\",\"Value\":\"secret-sentinel\",\"MetadataJson\":\"{\\\"LoginOtpCode\\\":\\\"secret-sentinel\\\"}\",\"DocumentHashSha256\":\"keep-integrity-hash\"}");
        Assert.DoesNotContain("secret-sentinel", safe!); Assert.Contains("keep-integrity-hash", safe!);
        Assert.Null(AuditDataPolicy.SanitizeJson("malformed secret-sentinel"));
    }

    [Fact]
    public async Task DeletedOrderCanBeRestoredAndRedoneWithoutRestoringPaymentCredentials()
    {
        using var db = RecurringDiscountRegressionTests.Db(); var audit = Audit(db);
        var order = new Order { Id = 8, UserId = 4, Total = 125.55m, SubTotal = 100, Tax = 8.88m, Tips = 16.67m,
            ServiceAddress = "Business address", PaymentMethod = PaymentMethod.Zelle, IsPaid = true,
            CleanerTotalSalary = 42, ServiceDate = new DateTime(2026, 9, 9), PaymentAccessToken = "expired-link-secret" };
        await audit.LogDeleteAsync(order);
        var log = await db.AuditLogs.SingleAsync();
        await audit.UndoAsync(log.Id);
        var restored = (await db.Orders.FindAsync(8))!;
        Assert.Equal(order.Total, restored.Total); Assert.Equal(order.Tax, restored.Tax); Assert.Equal(order.Tips, restored.Tips);
        Assert.Equal(order.CleanerTotalSalary, restored.CleanerTotalSalary); Assert.Equal(order.ServiceAddress, restored.ServiceAddress);
        Assert.Equal(order.PaymentMethod, restored.PaymentMethod); Assert.True(restored.IsPaid); Assert.Null(restored.PaymentAccessToken);
        await audit.RedoAsync(log.Id); Assert.Null(await db.Orders.FindAsync(8));
    }

    [Fact]
    public async Task RestoredAccountIsInactiveAndNeedsFreshCredentials()
    {
        using var db = RecurringDiscountRegressionTests.Db(); var audit = Audit(db);
        await audit.LogDeleteAsync(new User { Id = 9, FirstName = "Existing", LastName = "Customer", Email = "test@example.invalid",
            IsActive = true, PasswordResetToken = "reset-secret", LoginOtpCode = "otp-secret", TwoFactorPinHash = "pin-secret" });
        var log = await db.AuditLogs.SingleAsync(); await audit.UndoAsync(log.Id);
        var restored = (await db.Users.FindAsync(9))!;
        Assert.Equal("Existing", restored.FirstName); Assert.False(restored.IsActive);
        Assert.Null(restored.PasswordResetToken); Assert.Null(restored.LoginOtpCode); Assert.Null(restored.TwoFactorPinHash);
    }

    [Fact]
    public async Task LegacyUpdateUndoPreservesCurrentCredentialAndUnrelatedBusinessFields()
    {
        using var db = RecurringDiscountRegressionTests.Db(); var audit = Audit(db);
        db.Users.Add(new User { Id = 9, FirstName = "After", LastName = "Keep", PasswordResetToken = "current-secret" });
        var log = new AuditLog { EntityType = "User", EntityId = 9, Action = "Update",
            OldValues = "{\"Id\":9,\"FirstName\":\"Before\",\"PasswordResetToken\":\"old-secret\"}",
            NewValues = "{\"Id\":9,\"FirstName\":\"After\"}", ChangedFields = "[\"FirstName\",\"PasswordResetToken\"]" };
        db.AuditLogs.Add(log); await db.SaveChangesAsync();
        // Simulate the legacy persisted snapshot, before the new write policy existed.
        log.OldValues = "{\"Id\":9,\"FirstName\":\"Before\",\"PasswordResetToken\":\"old-secret\"}";
        await audit.UndoAsync(log.Id);
        var user = (await db.Users.FindAsync(9))!;
        Assert.Equal("Before", user.FirstName); Assert.Equal("Keep", user.LastName); Assert.Equal("current-secret", user.PasswordResetToken);
        await audit.RedoAsync(log.Id); Assert.Equal("After", user.FirstName); Assert.Equal("current-secret", user.PasswordResetToken);
    }

    [Fact]
    public async Task ReadProjectionResolvesNamesWithoutChangingRestoreIdentifiers()
    {
        using var db = RecurringDiscountRegressionTests.Db();
        db.Users.Add(new User { Id = 4, FirstName = "Ada", LastName = "Client" });
        db.ServiceTypes.Add(new ServiceType { Id = 3, Name = "Deep Cleaning" }); await db.SaveChangesAsync();
        var log = new AuditLog { Id = 1, NewValues = "{\"UserId\":4,\"ServiceTypeId\":3}" };
        var display = (await AuditDisplayProjection.BuildAsync(db, new[] { log }))[1];
        Assert.Contains("Ada Client (#4)", display.NewValues!); Assert.Contains("Deep Cleaning (#3)", display.NewValues!);
        Assert.Equal(4, (int)JObject.Parse(log.NewValues!)["UserId"]!);
    }
}

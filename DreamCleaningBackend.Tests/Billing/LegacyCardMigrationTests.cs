using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DreamCleaningBackend.Tests.Billing;

/// <summary>
/// The one-card → many-cards DATA step of AddSavedCardsAndAutoPay, run for real on MariaDB against
/// rows shaped exactly as the old feature left them (a pm id + brand + last four on the user, no
/// Primary pointer). The SQL executed is read out of the migration itself, so this tests the
/// statements that will actually run on deploy — twice, to prove a re-run changes nothing.
/// </summary>
public class LegacyCardMigrationTests : IClassFixture<MariaDbDatabase>
{
    private readonly MariaDbDatabase _db;

    public LegacyCardMigrationTests(MariaDbDatabase db) => _db = db;

    private static async Task RunMigrationDataStepAsync(Data.ApplicationDbContext db)
    {
        var migrations = db.GetInfrastructure().GetRequiredService<IMigrationsAssembly>();
        var migration = migrations.CreateMigration(migrations.Migrations["20260919052357_AddSavedCardsAndAutoPay"],
            "Pomelo.EntityFrameworkCore.MySql");
        var statements = migration.UpOperations.OfType<SqlOperation>().ToList();
        Assert.Equal(2, statements.Count);
        foreach (var sql in statements)
            await db.Database.ExecuteSqlRawAsync(sql.Sql);
    }

    [MariaDbFact]
    public async Task ExistingSavedCards_BecomeEachUsersPrimary_AndNothingIsLostOrDuplicated()
    {
        User Legacy(string? customer, string? pm, string? brand, string? last4) => new()
        {
            FirstName = "Legacy", LastName = "User", Email = $"{Guid.NewGuid():N}@example.com", Role = UserRole.Customer,
            IsActive = true, CreatedAt = DateTime.UtcNow,
            StripeCustomerId = customer, DefaultPaymentMethodId = pm, SavedCardBrand = brand, SavedCardLast4 = last4
        };

        var pmId = "pm_legacy_" + Guid.NewGuid().ToString("N")[..8];
        User withCard, withoutCustomer, withoutCard;
        await using (var db = _db.CreateContext())
        {
            withCard = Legacy("cus_legacy_1", pmId, "visa", "4242");
            withoutCustomer = Legacy(null, "pm_orphan_" + Guid.NewGuid().ToString("N")[..8], "visa", "1111"); // never chargeable
            withoutCard = Legacy("cus_legacy_3", null, null, null);
            db.Users.AddRange(withCard, withoutCustomer, withoutCard);
            await db.SaveChangesAsync();
        }

        await using (var db = _db.CreateContext())
            await RunMigrationDataStepAsync(db);

        await using (var db = _db.CreateContext())
        {
            var card = await db.CustomerPaymentMethods.AsNoTracking().SingleAsync(c => c.StripePaymentMethodId == pmId);
            Assert.Equal(withCard.Id, card.UserId);
            Assert.Equal("cus_legacy_1", card.StripeCustomerId);
            Assert.Equal("visa", card.Brand);
            Assert.Equal("4242", card.Last4);
            Assert.Equal(CustomerPaymentMethodStatus.Active, card.Status);
            Assert.Equal("legacy_migration", card.Source);

            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == withCard.Id);
            Assert.Equal(card.Id, user.PrimaryPaymentMethodId);
            Assert.Null(user.BackupPaymentMethodId);
            Assert.False(user.AutoPayEnabled);                 // saving a card never implied AutoPay
            Assert.Equal(pmId, user.DefaultPaymentMethodId);   // legacy column kept, not dropped

            var orphan = await db.Users.AsNoTracking().FirstAsync(u => u.Id == withoutCustomer.Id);
            Assert.Null(orphan.PrimaryPaymentMethodId);
            Assert.NotNull(orphan.DefaultPaymentMethodId);     // left exactly where it was
            Assert.False(await db.CustomerPaymentMethods.AnyAsync(c => c.UserId == withoutCustomer.Id));
            Assert.Null((await db.Users.AsNoTracking().FirstAsync(u => u.Id == withoutCard.Id)).PrimaryPaymentMethodId);
        }

        // An interrupted or repeated deploy runs it again: nothing may duplicate or move.
        await using (var db = _db.CreateContext())
            await RunMigrationDataStepAsync(db);

        await using (var db = _db.CreateContext())
        {
            Assert.Equal(1, await db.CustomerPaymentMethods.CountAsync(c => c.StripePaymentMethodId == pmId));
            var card = await db.CustomerPaymentMethods.AsNoTracking().SingleAsync(c => c.StripePaymentMethodId == pmId);
            Assert.Equal(card.Id, (await db.Users.AsNoTracking().FirstAsync(u => u.Id == withCard.Id)).PrimaryPaymentMethodId);
        }
    }

    [MariaDbFact]
    public async Task AMigratedCard_IsUsableByTheNewServices()
    {
        var h = new BillingHarness(_db);
        var pmId = h.Stripe.AddCard("cus_legacy_9", CardBehaviour.Succeed, last4: "0077");
        User user;
        await using (var db = _db.CreateContext())
        {
            user = new User
            {
                FirstName = "Legacy", LastName = "Payer", Email = $"{Guid.NewGuid():N}@example.com", Role = UserRole.Customer,
                IsActive = true, CreatedAt = DateTime.UtcNow, StripeCustomerId = "cus_legacy_9",
                DefaultPaymentMethodId = pmId, SavedCardBrand = "visa", SavedCardLast4 = "0077"
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        await using (var db = _db.CreateContext())
            await RunMigrationDataStepAsync(db);

        await using var scope = h.Scope();
        var cards = await scope.ServiceProvider.GetRequiredService<Services.Billing.IPaymentMethodService>().ListAsync(user.Id, true);
        var card = Assert.Single(cards);
        Assert.True(card.IsPrimary);
        Assert.Equal("0077", card.Last4);
        Assert.NotNull(card.ExpYear); // backfilled from Stripe on first read
    }
}

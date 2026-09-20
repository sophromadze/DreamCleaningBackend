using System.Reflection;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Repositories.Interfaces;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// The customer-facing email change (Security tab -> Change Email), end to end on the server:
/// <c>AuthService.InitiateEmailChange</c> writes a pending address plus a one-hour token and
/// mails a link; <c>AuthService.ConfirmEmailChange</c> is what actually moves
/// <c>User.Email</c>.
///
/// The properties these tests hold down:
///   * the address only ever moves through a token that was mailed to the NEW address, so an
///     authenticated session alone cannot complete a change;
///   * a request that could not be mailed leaves the account exactly as it was, rather than
///     half-changed;
///   * every refusal names what is actually wrong, because the page can only show the
///     { message } body (see Helpers/EmailAddressValidator).
/// </summary>
public class EmailChangeFlowTests
{
    private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString(), b => b.EnableNullChecks(false))
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    /// <summary>Records what was mailed, and can be told to fail like a dead SMTP server.</summary>
    private sealed class MailSpy
    {
        public readonly List<(string To, string Link)> ChangeVerifications = new();
        public readonly List<string> ChangeConfirmations = new();
        public bool ThrowOnVerification;
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?>? run = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Run = run; return proxy;
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Run;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Run == null
            ? throw new InvalidOperationException($"Unexpected external call: {method!.Name}") : Run(method!, args);
    }

    private static IEmailService Mail(MailSpy spy) => Stub<IEmailService>((m, a) =>
    {
        switch (m.Name)
        {
            case nameof(IEmailService.SendEmailChangeVerificationAsync):
                if (spy.ThrowOnVerification) throw new InvalidOperationException("SMTP is down");
                spy.ChangeVerifications.Add(((string)a![0]!, (string)a[2]!));
                return Task.CompletedTask;
            case nameof(IEmailService.SendEmailChangeConfirmationAsync):
                spy.ChangeConfirmations.Add((string)a![0]!);
                return Task.CompletedTask;
            default:
                return Task.CompletedTask;
        }
    });

    private static AuthService Service(ApplicationDbContext db, MailSpy spy)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppSettings:Token"] = "a-test-signing-key-that-is-definitely-long-enough-for-hmac-sha512-use",
            ["Frontend:Url"] = "http://localhost:4200"
        }).Build();

        return new AuthService(
            db,
            config,
            Mail(spy),
            NullLogger<AuthService>.Instance,
            Stub<ISpecialOfferService>((_, _) => Task.CompletedTask),
            Stub<IAuditService>((_, _) => Task.CompletedTask),
            Stub<IUserRepository>(),
            Stub<IReferralService>((_, _) => Task.CompletedTask),
            Stub<IBubblePointsService>((_, _) => Task.CompletedTask),
            Stub<ICleanerAccountService>((_, _) => Task.CompletedTask),
            null!);
    }

    /// <summary>
    /// A password the account can actually be asked for. Mirrors AuthService.CreatePasswordHash
    /// (HMACSHA512 with the generated key as the salt) rather than reaching into it by
    /// reflection - if that scheme ever changes, VerifyPasswordHash rejects this and the tests
    /// say so loudly.
    /// </summary>
    private static void SetPassword(User user, string password)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA512();
        user.PasswordSalt = Convert.ToBase64String(hmac.Key);
        user.PasswordHash = Convert.ToBase64String(
            hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(password)));
    }

    private static User Seed(ApplicationDbContext db, string email = "old@example.com",
        string authProvider = "Local", bool withPassword = true)
    {
        var user = new User
        {
            Id = 1, FirstName = "Pat", LastName = "Customer", Email = email,
            AuthProvider = authProvider, IsActive = true, IsEmailVerified = true
        };
        if (withPassword) SetPassword(user, "Correct1horse");
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static async Task<string> Fails(Func<Task> act)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(act);
        return ex.Message;
    }

    // -- Happy path ----------------------------------------------------------------------

    [Fact]
    public async Task AValidRequest_MailsTheNewAddressAndChangesNothingYet()
    {
        using var db = Db();
        var user = Seed(db);
        var spy = new MailSpy();

        var result = await Service(db, spy).InitiateEmailChange(1,
            new InitiateEmailChangeDto { NewEmail = "new@example.com", CurrentPassword = "Correct1horse" });

        Assert.True(result.RequiresVerification);
        // The verification link goes to the NEW address, never the old one.
        Assert.Single(spy.ChangeVerifications);
        Assert.Equal("new@example.com", spy.ChangeVerifications[0].To);

        // Nothing has moved yet - the account still signs in with the old address.
        Assert.Equal("old@example.com", user.Email);
        Assert.Equal("new@example.com", user.PendingEmail);
        Assert.False(string.IsNullOrEmpty(user.EmailChangeToken));
        Assert.True(user.EmailChangeTokenExpiry > DateTime.UtcNow);
    }

    [Fact]
    public async Task ConfirmingWithTheMailedToken_MovesTheAddressAndConsumesTheToken()
    {
        using var db = Db();
        var user = Seed(db);
        var spy = new MailSpy();
        var svc = Service(db, spy);

        await svc.InitiateEmailChange(1,
            new InitiateEmailChangeDto { NewEmail = "new@example.com", CurrentPassword = "Correct1horse" });
        var token = user.EmailChangeToken!;

        Assert.True(await svc.ConfirmEmailChange(token));

        Assert.Equal("new@example.com", user.Email);
        Assert.Null(user.PendingEmail);
        Assert.Null(user.EmailChangeToken);
        Assert.Null(user.EmailChangeTokenExpiry);
        Assert.Contains("new@example.com", spy.ChangeConfirmations);

        // Single use: replaying the same link is refused rather than silently succeeding.
        Assert.Equal("Invalid or expired email change token", await Fails(() => svc.ConfirmEmailChange(token)));
    }

    /// <summary>
    /// The token is the whole gate. Being signed in is not enough to move the address - which is
    /// what stops a borrowed session changing the account's recovery address.
    /// </summary>
    [Fact]
    public async Task AGuessedOrExpiredToken_ChangesNothing()
    {
        using var db = Db();
        var user = Seed(db);
        var spy = new MailSpy();
        var svc = Service(db, spy);

        await svc.InitiateEmailChange(1,
            new InitiateEmailChangeDto { NewEmail = "new@example.com", CurrentPassword = "Correct1horse" });

        Assert.Equal("Invalid or expired email change token", await Fails(() => svc.ConfirmEmailChange("not-the-token")));
        Assert.Equal("old@example.com", user.Email);

        // Expire it and try the real one.
        user.EmailChangeTokenExpiry = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        Assert.Equal("Invalid or expired email change token", await Fails(() => svc.ConfirmEmailChange(user.EmailChangeToken!)));
        Assert.Equal("old@example.com", user.Email);
    }

    // -- Refusals, each naming the actual problem -----------------------------------------

    [Fact]
    public async Task TheWrongPassword_IsRefusedAndLeavesNoPendingChange()
    {
        using var db = Db();
        var user = Seed(db);
        var spy = new MailSpy();

        Assert.Equal("Current password is incorrect", await Fails(() => Service(db, spy).InitiateEmailChange(1,
            new InitiateEmailChangeDto { NewEmail = "new@example.com", CurrentPassword = "Wrong1horse" })));

        Assert.Null(user.PendingEmail);
        Assert.Null(user.EmailChangeToken);
        Assert.Empty(spy.ChangeVerifications);
    }

    [Fact]
    public async Task AnEmptyPassword_SaysSoRatherThanThrowingSomethingOpaque()
    {
        using var db = Db();
        Seed(db);
        Assert.Equal("Enter your current password to confirm this change.",
            await Fails(() => Service(db, new MailSpy()).InitiateEmailChange(1,
                new InitiateEmailChangeDto { NewEmail = "new@example.com", CurrentPassword = "" })));
    }

    /// <summary>
    /// A Local account with no password at all - a claimed guest checkout is the common one.
    /// VerifyPasswordHash reaches Convert.FromBase64String(null) on those, and the customer used
    /// to be told "Value cannot be null. (Parameter 's')".
    /// </summary>
    [Fact]
    public async Task APasswordlessLocalAccount_IsToldToSetAPasswordFirst()
    {
        using var db = Db();
        Seed(db, withPassword: false);

        Assert.Equal("Set a password on your account before changing your email address.",
            await Fails(() => Service(db, new MailSpy()).InitiateEmailChange(1,
                new InitiateEmailChangeDto { NewEmail = "new@example.com", CurrentPassword = "anything" })));
    }

    [Fact]
    public async Task ASocialAccount_IsRefused()
    {
        using var db = Db();
        Seed(db, authProvider: "Google");

        Assert.Equal("Email change is only available for local accounts",
            await Fails(() => Service(db, new MailSpy()).InitiateEmailChange(1,
                new InitiateEmailChangeDto { NewEmail = "new@example.com", CurrentPassword = "Correct1horse" })));
    }

    [Theory]
    [InlineData("", "Email address is required.")]
    [InlineData("nope", "missing the \"@\" symbol")]
    [InlineData("a@b", "missing its ending")]
    [InlineData("a b@example.com", "cannot contain spaces")]
    public async Task AMalformedAddress_IsDescribedNotJustRejected(string address, string expectedFragment)
    {
        using var db = Db();
        Seed(db);

        var message = await Fails(() => Service(db, new MailSpy()).InitiateEmailChange(1,
            new InitiateEmailChangeDto { NewEmail = address, CurrentPassword = "Correct1horse" }));

        Assert.Contains(expectedFragment, message);
    }

    [Fact]
    public async Task AnAddressAlreadyOnAnotherAccount_IsRefused()
    {
        using var db = Db();
        Seed(db);
        db.Users.Add(new User { Id = 2, FirstName = "Other", LastName = "Person", Email = "taken@example.com", IsActive = true });
        await db.SaveChangesAsync();

        Assert.Equal("This email address is already in use",
            await Fails(() => Service(db, new MailSpy()).InitiateEmailChange(1,
                new InitiateEmailChangeDto { NewEmail = "TAKEN@Example.com ", CurrentPassword = "Correct1horse" })));
    }

    /// <summary>
    /// Casing and stray whitespace are normalised BEFORE the comparisons. Without that, the
    /// "must be different" guard missed and the account happily started a change to the address
    /// it already had - and the space would have been stored into Email, where no login matches.
    /// </summary>
    [Fact]
    public async Task TheAddressItAlreadyHas_IsRefusedWhateverTheCasingOrSpacing()
    {
        using var db = Db();
        Seed(db);

        Assert.Equal("New email must be different from current email",
            await Fails(() => Service(db, new MailSpy()).InitiateEmailChange(1,
                new InitiateEmailChangeDto { NewEmail = "  OLD@Example.COM  ", CurrentPassword = "Correct1horse" })));
    }

    [Fact]
    public async Task AMixedCaseAddress_IsStoredLowercasedAndMailedThatWay()
    {
        using var db = Db();
        var user = Seed(db);
        var spy = new MailSpy();

        await Service(db, spy).InitiateEmailChange(1,
            new InitiateEmailChangeDto { NewEmail = "  New@Example.COM  ", CurrentPassword = "Correct1horse" });

        Assert.Equal("new@example.com", user.PendingEmail);
        Assert.Equal("new@example.com", spy.ChangeVerifications[0].To);
    }

    /// <summary>
    /// If the verification mail cannot be sent, the customer must not be left with a pending
    /// change they can never confirm and no way to tell that from a change that worked.
    /// </summary>
    [Fact]
    public async Task AFailedVerificationMail_RollsThePendingChangeBack()
    {
        using var db = Db();
        var user = Seed(db);
        var spy = new MailSpy { ThrowOnVerification = true };

        var message = await Fails(() => Service(db, spy).InitiateEmailChange(1,
            new InitiateEmailChangeDto { NewEmail = "new@example.com", CurrentPassword = "Correct1horse" }));

        Assert.Contains("Failed to send verification email", message);
        Assert.Null(user.PendingEmail);
        Assert.Null(user.EmailChangeToken);
        Assert.Null(user.EmailChangeTokenExpiry);
        Assert.Equal("old@example.com", user.Email);
    }

    /// <summary>
    /// Asking twice replaces the first token rather than leaving two live links, and the address
    /// still only moves once.
    /// </summary>
    [Fact]
    public async Task AskingTwice_LeavesOnlyTheLatestLinkWorking()
    {
        using var db = Db();
        var user = Seed(db);
        var spy = new MailSpy();
        var svc = Service(db, spy);

        await svc.InitiateEmailChange(1,
            new InitiateEmailChangeDto { NewEmail = "first@example.com", CurrentPassword = "Correct1horse" });
        var firstToken = user.EmailChangeToken!;

        await svc.InitiateEmailChange(1,
            new InitiateEmailChangeDto { NewEmail = "second@example.com", CurrentPassword = "Correct1horse" });
        var secondToken = user.EmailChangeToken!;

        Assert.NotEqual(firstToken, secondToken);
        Assert.Equal("Invalid or expired email change token", await Fails(() => svc.ConfirmEmailChange(firstToken)));

        Assert.True(await svc.ConfirmEmailChange(secondToken));
        Assert.Equal("second@example.com", user.Email);
    }

    /// <summary>
    /// The address was free when the link was mailed and taken by the time it was clicked. The
    /// change is refused rather than colliding on the unique address.
    /// </summary>
    [Fact]
    public async Task AnAddressClaimedWhileTheLinkWasInFlight_IsRefusedAtConfirmTime()
    {
        using var db = Db();
        var user = Seed(db);
        var svc = Service(db, new MailSpy());

        await svc.InitiateEmailChange(1,
            new InitiateEmailChangeDto { NewEmail = "contested@example.com", CurrentPassword = "Correct1horse" });

        db.Users.Add(new User { Id = 2, FirstName = "Fast", LastName = "Mover", Email = "contested@example.com", IsActive = true });
        await db.SaveChangesAsync();

        Assert.Equal("This email address is no longer available",
            await Fails(() => svc.ConfirmEmailChange(user.EmailChangeToken!)));
        Assert.Equal("old@example.com", user.Email);
    }

    /// <summary>
    /// The DTO must stay free of [Required]/[EmailAddress]. Those are answered by
    /// [ApiController] with a ValidationProblemDetails body that has no `message`, so the page
    /// could only show Angular's transport text instead of the sentence above.
    /// </summary>
    [Fact]
    public void TheDtoDoesNotLeanOnModelValidation()
    {
        foreach (var property in typeof(InitiateEmailChangeDto).GetProperties())
        {
            var attributes = property.GetCustomAttributes(inherit: true)
                .Select(a => a.GetType().Name)
                .Where(n => n is "RequiredAttribute" or "EmailAddressAttribute")
                .ToList();

            Assert.True(attributes.Count == 0,
                $"InitiateEmailChangeDto.{property.Name} carries {string.Join(", ", attributes)}; " +
                "validate it in AuthService.InitiateEmailChange so the refusal reaches the customer.");
        }
    }
}

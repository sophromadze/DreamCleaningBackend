using System.Reflection;
using System.Security.Claims;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Tests.Billing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;
using Xunit;

namespace DreamCleaningBackend.Tests;

/// <summary>
/// "Buy a gift card for myself, send it later" (2026-10), on a real MariaDB (the send / resend
/// claims are conditional UPDATEs, which the InMemory provider cannot run). Holds down:
///   * a send-later purchase emails ONLY the buyer's receipt, never a recipient;
///   * only the purchasing account can list, send or resend a card (404 for anyone else);
///   * a card is sent once, to one recipient, and resend goes to that same recipient;
///   * the recipient email carries the REMAINING balance of a partly used card;
///   * the code is masked in the buyer's list once the card has been sent;
///   * confirm-payment refuses a payment that is not the card's own, and never emails twice;
///   * today's "send now" flow still emails recipient + buyer exactly as before.
/// Runs only when DC_TEST_MARIADB names a local server (see MariaDbFactAttribute).
/// </summary>
public class GiftCardSendLaterTests : IClassFixture<MariaDbDatabase>
{
    private readonly MariaDbDatabase _db;

    public GiftCardSendLaterTests(MariaDbDatabase db) => _db = db;

    // ── test doubles ──────────────────────────────────────────────────────────────────────────

    public sealed record Mail(string Method, object?[] Args);

    private sealed class MailSpy
    {
        public readonly List<Mail> Sent = new();
        public bool FailRecipientEmail;
        public IEnumerable<Mail> Notifications => Sent.Where(m => m.Method == nameof(IEmailService.SendGiftCardNotificationAsync));
        public IEnumerable<Mail> Receipts => Sent.Where(m => m.Method == nameof(IEmailService.SendGiftCardSenderConfirmationAsync));
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> run) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Run = run; return proxy;
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Run;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Run!(method!, args);
    }

    private static IEmailService Email(MailSpy spy) => Stub<IEmailService>((m, a) =>
    {
        if (m.Name == nameof(IEmailService.SendGiftCardNotificationAsync) && spy.FailRecipientEmail)
            throw new InvalidOperationException("SMTP is down");
        spy.Sent.Add(new Mail(m.Name, a ?? Array.Empty<object?>()));
        return Task.CompletedTask;
    });

    /// <summary>Every intent "succeeded"; CreatePaymentIntentAsync hands out pi_test_N.</summary>
    private static IStripeService Stripe()
    {
        var n = 0;
        return Stub<IStripeService>((m, a) => m.Name switch
        {
            nameof(IStripeService.GetPaymentIntentAsync) =>
                Task.FromResult(new PaymentIntent { Id = (string)a![0]!, Status = "succeeded" }),
            nameof(IStripeService.CreatePaymentIntentAsync) =>
                Task.FromResult(new PaymentIntent { Id = $"pi_test_{Guid.NewGuid():N}_{++n}", ClientSecret = "secret" }),
            _ => throw new InvalidOperationException($"Unexpected Stripe call: {m.Name}")
        });
    }

    private static GiftCardService Service(ApplicationDbContext db, MailSpy spy) =>
        new(db, new ConcurrentAudit(), Email(spy), NullLogger<GiftCardService>.Instance);

    private static GiftCardController Controller(ApplicationDbContext db, MailSpy spy, int? userId)
    {
        var controller = new GiftCardController(Service(db, spy), Email(spy), Stripe(), db,
            NullLogger<GiftCardController>.Instance);
        var identity = userId == null
            ? new ClaimsIdentity()
            : new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private async Task<User> NewUser(string first)
    {
        await using var db = _db.CreateContext();
        var user = new User
        {
            FirstName = first, LastName = "Buyer", Email = $"{first.ToLower()}.{Guid.NewGuid():N}@example.com",
            Phone = "212" + Random.Shared.Next(1000000, 9999999), Role = UserRole.Customer,
            IsActive = true, CanReceiveEmails = true, CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Buys through the real controller (create + confirm-payment), like the page does.</summary>
    private async Task<int> Buy(User? buyer, MailSpy spy, bool sendLater, decimal amount = 100m)
    {
        await using var db = _db.CreateContext();
        var controller = Controller(db, spy, buyer?.Id);
        var create = await controller.CreateGiftCard(new CreateGiftCardDto
        {
            Amount = amount,
            SendLater = sendLater,
            RecipientName = sendLater ? null : "Rita",
            RecipientEmail = sendLater ? null : "rita@example.com",
            SenderName = buyer != null ? $"{buyer.FirstName} {buyer.LastName}" : "Guest Person",
            SenderEmail = buyer?.Email ?? "guest@example.com",
            Message = sendLater ? null : "Enjoy!"
        });
        var response = (GiftCardPurchaseResponseDto)((OkObjectResult)create.Result!).Value!;

        var confirm = await controller.ConfirmGiftCardPayment(response.GiftCardId,
            new ConfirmPaymentDto { PaymentIntentId = response.PaymentIntentId });
        Assert.IsType<OkObjectResult>(confirm);
        return response.GiftCardId;
    }

    private static SendMyGiftCardDto Recipient(string name = "Nina", string email = "nina@example.com") => new()
    {
        RecipientName = name, RecipientEmail = email, SenderName = "Anna Buyer", Message = "Happy birthday <3"
    };

    // ── masking ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MaskCode_ShowsTheLastFourOnly_AndIsIdempotent()
    {
        Assert.Equal("••••-••••-EF56", GiftCardService.MaskCode("AB12-CD34-EF56"));
        // EmailService re-applies it to whatever the caller passed, so masking twice must not change it.
        Assert.Equal("••••-••••-EF56", GiftCardService.MaskCode(GiftCardService.MaskCode("AB12-CD34-EF56")));
    }

    // ── purchase ──────────────────────────────────────────────────────────────────────────────

    [MariaDbFact]
    public async Task SendLater_Purchase_EmailsOnlyTheBuyersReceipt_AndKeepsTheCardUnsent()
    {
        var buyer = await NewUser("Anna");
        var spy = new MailSpy();
        var id = await Buy(buyer, spy, sendLater: true);

        Assert.Empty(spy.Notifications);
        var receipt = Assert.Single(spy.Receipts);
        Assert.Equal(buyer.Email, receipt.Args[0]);
        Assert.Equal(true, receipt.Args[7]);           // sendLater variant of the receipt
        Assert.Null(receipt.Args[2]);                  // no recipient

        await using var db = _db.CreateContext();
        var card = await db.GiftCards.SingleAsync(g => g.Id == id);
        Assert.True(card.IsPendingSend);
        Assert.True(card.IsPaid);
        Assert.Null(card.RecipientName);
        Assert.Null(card.RecipientEmail);
        Assert.Equal(buyer.Id, card.PurchasedByUserId);
        Assert.Equal("Anna Buyer", card.SenderName);   // from the account, not the form
        Assert.Equal(card.Code, receipt.Args[4]);      // still the buyer's card: full code
    }

    [MariaDbFact]
    public async Task SendLater_Purchase_RequiresAnAccount()
    {
        await using var db = _db.CreateContext();
        var result = await Controller(db, new MailSpy(), userId: null).CreateGiftCard(new CreateGiftCardDto
        {
            Amount = 100m, SendLater = true, SenderName = "Guest", SenderEmail = "guest@example.com"
        });
        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [MariaDbFact]
    public async Task SendNow_Purchase_IsUnchanged_RecipientAndBuyerEmailed_CardCountsAsSent()
    {
        var spy = new MailSpy();
        var id = await Buy(null, spy, sendLater: false, amount: 150m);   // anonymous, like today

        var notification = Assert.Single(spy.Notifications);
        Assert.Equal("rita@example.com", notification.Args[0]);
        Assert.Equal(150m, notification.Args[4]);
        var receipt = Assert.Single(spy.Receipts);
        Assert.Equal("guest@example.com", receipt.Args[0]);
        Assert.Equal(false, receipt.Args[7]);

        await using var db = _db.CreateContext();
        var card = await db.GiftCards.SingleAsync(g => g.Id == id);
        Assert.False(card.IsPendingSend);
        Assert.Equal(card.Code, notification.Args[3]);                      // recipient: full code
        Assert.Equal($"••••-••••-{card.Code[^4..]}", receipt.Args[4]);      // buyer: last 4 only
        Assert.DoesNotContain(card.Code[..9], (string)receipt.Args[4]!);
    }

    [MariaDbFact]
    public async Task SendNow_Purchase_WithoutARecipient_IsRefused()
    {
        await using var db = _db.CreateContext();
        var result = await Controller(db, new MailSpy(), null).CreateGiftCard(new CreateGiftCardDto
        {
            Amount = 100m, SenderName = "Guest", SenderEmail = "guest@example.com"
        });
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [MariaDbFact]
    public async Task ConfirmPayment_RefusesAPaymentThatIsNotTheCards_AndNeverEmailsTwice()
    {
        var spy = new MailSpy();
        var id = await Buy(null, spy, sendLater: false);
        Assert.Equal(2, spy.Sent.Count);

        await using var db = _db.CreateContext();
        var card = await db.GiftCards.AsNoTracking().SingleAsync(g => g.Id == id);
        var controller = Controller(db, spy, null);

        var foreign = await controller.ConfirmGiftCardPayment(id, new ConfirmPaymentDto { PaymentIntentId = "pi_someone_else" });
        Assert.IsType<BadRequestObjectResult>(foreign);

        var repeat = await controller.ConfirmGiftCardPayment(id, new ConfirmPaymentDto { PaymentIntentId = card.PaymentIntentId! });
        Assert.IsType<OkObjectResult>(repeat);
        Assert.Equal(2, spy.Sent.Count);   // nothing new went out
    }

    // ── profile list ──────────────────────────────────────────────────────────────────────────

    [MariaDbFact]
    public async Task MyGiftCards_ListsOnlyTheBuyersPaidCards_FullCodeUntilSent()
    {
        var anna = await NewUser("Anna");
        var ben = await NewUser("Ben");
        var spy = new MailSpy();
        var annaCard = await Buy(anna, spy, sendLater: true);
        var annaSentNow = await Buy(anna, spy, sendLater: false);
        await Buy(ben, spy, sendLater: true);

        await using var db = _db.CreateContext();
        var list = await Service(db, spy).GetMyGiftCards(anna.Id);
        Assert.Equal(new[] { annaCard, annaSentNow }.OrderBy(x => x), list.Select(c => c.Id).OrderBy(x => x));

        var full = await db.GiftCards.SingleAsync(g => g.Id == annaCard);
        var unsent = list.Single(c => c.Id == annaCard);
        Assert.Equal(full.Code, unsent.Code);
        Assert.Equal("NotSent", unsent.Status);
        Assert.True(unsent.CanSend);
        Assert.False(unsent.CanResend);

        var sent = list.Single(c => c.Id == annaSentNow);
        Assert.Equal("Sent", sent.Status);
        Assert.True(sent.IsCodeMasked);
        Assert.StartsWith("••••-••••-", sent.Code);
        Assert.Equal("rita@example.com", sent.RecipientEmail);
    }

    // ── send / resend ─────────────────────────────────────────────────────────────────────────

    [MariaDbFact]
    public async Task Send_PartlyUsedCard_EmailsTheRemainingBalance_ThenMasksTheCode_AndCannotBeSentAgain()
    {
        var anna = await NewUser("Anna");
        var spy = new MailSpy();
        var id = await Buy(anna, spy, sendLater: true, amount: 100m);

        // The buyer used $35 of it on their own booking first.
        await using (var db = _db.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "SET FOREIGN_KEY_CHECKS=0; " +
                "INSERT INTO GiftCardUsages (GiftCardId, OrderId, UserId, AmountUsed, BalanceAfterUsage, UsedAt) " +
                "VALUES ({0}, 999999, {1}, 35.00, 65.00, UTC_TIMESTAMP(6)); " +
                "SET FOREIGN_KEY_CHECKS=1; " +
                "UPDATE GiftCards SET CurrentBalance = 65.00 WHERE Id = {0};", id, anna.Id);
        }
        spy.Sent.Clear();

        await using (var db = _db.CreateContext())
        {
            var result = await Service(db, spy).SendMyGiftCard(id, anna.Id, Recipient());
            Assert.Equal(GiftCardSendResult.ResultKind.Ok, result.Kind);
            Assert.Equal("Sent", result.Card!.Status);
            Assert.True(result.Card.IsCodeMasked);
            Assert.Equal("nina@example.com", result.Card.RecipientEmail);
            Assert.Single(result.Card.Usages);
        }

        var notification = Assert.Single(spy.Notifications);
        Assert.Equal("nina@example.com", notification.Args[0]);
        Assert.Equal("Nina", notification.Args[1]);
        Assert.Equal("Anna Buyer", notification.Args[2]);
        Assert.Equal(65.00m, notification.Args[4]);              // remaining balance, not the original $100
        Assert.Equal("Happy birthday <3", notification.Args[5]);  // escaped inside the template
        var buyerConfirmation = Assert.Single(spy.Receipts);
        Assert.Equal(anna.Email, buyerConfirmation.Args[0]);
        Assert.Equal(65.00m, buyerConfirmation.Args[5]);
        Assert.Equal(false, buyerConfirmation.Args[7]);
        await using (var db = _db.CreateContext())
        {
            var code = (await db.GiftCards.AsNoTracking().SingleAsync(g => g.Id == id)).Code;
            Assert.Equal(code, notification.Args[3]);                                  // recipient: full code
            Assert.Equal($"••••-••••-{code[^4..]}", buyerConfirmation.Args[4]);        // buyer: last 4 only
        }

        await using (var db = _db.CreateContext())
        {
            var again = await Service(db, spy).SendMyGiftCard(id, anna.Id, Recipient("Someone", "else@example.com"));
            Assert.Equal(GiftCardSendResult.ResultKind.Invalid, again.Kind);
            var card = await db.GiftCards.SingleAsync(g => g.Id == id);
            Assert.Equal("nina@example.com", card.RecipientEmail);   // recipient can't be changed
            Assert.False(card.IsPendingSend);
            Assert.NotNull(card.SentAt);
        }
    }

    [MariaDbFact]
    public async Task AnotherUser_CannotList_Send_OrResend_SomeoneElsesCard()
    {
        var anna = await NewUser("Anna");
        var mallory = await NewUser("Mallory");
        var spy = new MailSpy();
        var id = await Buy(anna, spy, sendLater: true);
        spy.Sent.Clear();

        await using var db = _db.CreateContext();
        var asMallory = Controller(db, spy, mallory.Id);

        var list = (List<MyGiftCardDto>)((OkObjectResult)(await asMallory.GetMyGiftCards()).Result!).Value!;
        Assert.DoesNotContain(list, c => c.Id == id);

        Assert.IsType<NotFoundObjectResult>((await asMallory.SendMyGiftCard(id, Recipient())).Result);
        Assert.IsType<NotFoundObjectResult>((await asMallory.ResendMyGiftCard(id)).Result);
        Assert.Empty(spy.Sent);

        var card = await db.GiftCards.AsNoTracking().SingleAsync(g => g.Id == id);
        Assert.True(card.IsPendingSend);
    }

    [MariaDbFact]
    public async Task Send_RejectsABadEmail_WithTheActualMistake()
    {
        var anna = await NewUser("Anna");
        var spy = new MailSpy();
        var id = await Buy(anna, spy, sendLater: true);
        spy.Sent.Clear();

        await using var db = _db.CreateContext();
        var result = await Service(db, spy).SendMyGiftCard(id, anna.Id, Recipient(email: "nina.example.com"));
        Assert.Equal(GiftCardSendResult.ResultKind.Invalid, result.Kind);
        Assert.Contains("@", result.Message);
        Assert.Empty(spy.Sent);
        Assert.True((await db.GiftCards.AsNoTracking().SingleAsync(g => g.Id == id)).IsPendingSend);
    }

    [MariaDbFact]
    public async Task Resend_GoesToTheSameRecipient_AndHasACooldown()
    {
        var anna = await NewUser("Anna");
        var spy = new MailSpy();
        var id = await Buy(anna, spy, sendLater: true);
        await using (var db = _db.CreateContext())
            Assert.Equal(GiftCardSendResult.ResultKind.Ok, (await Service(db, spy).SendMyGiftCard(id, anna.Id, Recipient())).Kind);
        spy.Sent.Clear();

        await using (var db = _db.CreateContext())
        {
            var tooSoon = await Service(db, spy).ResendMyGiftCard(id, anna.Id);
            Assert.Equal(GiftCardSendResult.ResultKind.TooMany, tooSoon.Kind);
            Assert.Empty(spy.Sent);

            await db.Database.ExecuteSqlRawAsync(
                "UPDATE GiftCards SET LastEmailSentAt = UTC_TIMESTAMP(6) - INTERVAL 11 MINUTE WHERE Id = {0}", id);
        }

        await using (var db = _db.CreateContext())
        {
            var resent = await Service(db, spy).ResendMyGiftCard(id, anna.Id);
            Assert.Equal(GiftCardSendResult.ResultKind.Ok, resent.Kind);
        }
        var notification = Assert.Single(spy.Notifications);
        Assert.Equal("nina@example.com", notification.Args[0]);
        Assert.Empty(spy.Receipts);   // resend: in-app confirmation only
    }

    [MariaDbFact]
    public async Task Send_WhenTheEmailFails_KeepsTheRecipient_AndAllowsResendImmediately()
    {
        var anna = await NewUser("Anna");
        var spy = new MailSpy();
        var id = await Buy(anna, spy, sendLater: true);
        spy.FailRecipientEmail = true;

        await using (var db = _db.CreateContext())
        {
            var result = await Service(db, spy).SendMyGiftCard(id, anna.Id, Recipient());
            Assert.Equal(GiftCardSendResult.ResultKind.EmailFailed, result.Kind);
            Assert.True(result.Card!.CanResend);
        }

        spy.FailRecipientEmail = false;
        await using (var db = _db.CreateContext())
            Assert.Equal(GiftCardSendResult.ResultKind.Ok, (await Service(db, spy).ResendMyGiftCard(id, anna.Id)).Kind);
    }

    [MariaDbFact]
    public async Task ProfileEmails_AreCappedPerUserPerHour()
    {
        var anna = await NewUser("Anna");
        var spy = new MailSpy();
        var ids = new List<int>();
        for (var i = 0; i < GiftCardService.MaxProfileEmailsPerUserPerHour + 1; i++)
            ids.Add(await Buy(anna, spy, sendLater: true, amount: 50m));

        for (var i = 0; i < GiftCardService.MaxProfileEmailsPerUserPerHour; i++)
        {
            await using var db = _db.CreateContext();
            Assert.Equal(GiftCardSendResult.ResultKind.Ok, (await Service(db, spy).SendMyGiftCard(ids[i], anna.Id, Recipient())).Kind);
        }

        await using (var db = _db.CreateContext())
        {
            var lastId = ids[ids.Count - 1];
            var capped = await Service(db, spy).SendMyGiftCard(lastId, anna.Id, Recipient());
            Assert.Equal(GiftCardSendResult.ResultKind.TooMany, capped.Kind);
            Assert.True((await db.GiftCards.AsNoTracking().SingleAsync(g => g.Id == lastId)).IsPendingSend);
        }
    }
}

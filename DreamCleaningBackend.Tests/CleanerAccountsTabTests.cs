using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Helpers;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE ADMIN "CLEANERS" TAB (2026-09) - the cleaner LOGIN ACCOUNTS and the link from each one
    /// to a row in the Cleaners table.
    ///
    /// Two things this file pins, both of which were requested after the tab shipped:
    ///
    ///  1. <b>Finding somebody.</b> The link picker searches the ROSTER on the server - first name,
    ///     last name, the two together, email, phone. A client-side filter over whatever had
    ///     already been fetched would quietly stop finding people as the roster grew, and the
    ///     failure mode is an admin concluding a cleaner does not exist.
    ///  2. <b>Every change leaves a row.</b> Linking, unlinking and the automatic release that
    ///     happens when an account leaves the Cleaner role are all audited on the
    ///     <see cref="AuditEntityTypes.CleanerAccountLink"/> stream, keyed on the USER id. Before
    ///     this, an unlink left only a Cleaner row whose UserId had gone null for no stated reason,
    ///     and the release-on-demotion left nothing at all - so "why can this person no longer see
    ///     their jobs" was unanswerable from the Audits tab.
    /// </summary>
    public class CleanerAccountsTabTests : IDisposable
    {
        private readonly ApplicationDbContext _context;
        private readonly AuditService _audit;
        private readonly AdminCleanerAccountsController _controller;
        private readonly CleanerManagementService _cleaners;

        public CleanerAccountsTabTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"cleaner-accounts-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new ApplicationDbContext(options);
            _audit = new AuditService(_context, new HttpContextAccessor(), NullLogger<AuditService>.Instance);
            _controller = new AdminCleanerAccountsController(
                _context, _audit, NullLogger<AdminCleanerAccountsController>.Instance);
            _cleaners = new CleanerManagementService(
                _context, new ConfigurationBuilder().Build(), _audit);
        }

        public void Dispose() => _context.Dispose();

        // ── Fixtures ──────────────────────────────────────────────────────────────────────

        private User Account(int id, string first, string last, string email, UserRole role = UserRole.Cleaner)
        {
            var user = new User
            {
                Id = id,
                FirstName = first,
                LastName = last,
                Email = email,
                PasswordHash = "x",
                Role = role,
                IsActive = true
            };
            _context.Users.Add(user);
            _context.SaveChanges();
            return user;
        }

        private Cleaner CleanerRecord(int id, string first, string last, string? email = null,
            string? phone = null, bool isActive = true, int? userId = null)
        {
            var cleaner = new Cleaner
            {
                Id = id,
                FirstName = first,
                LastName = last,
                Email = email,
                Phone = phone,
                IsActive = isActive,
                UserId = userId
            };
            _context.Cleaners.Add(cleaner);
            _context.SaveChanges();
            return cleaner;
        }

        private static List<LinkableCleanerDto> Linkable(ActionResult<List<LinkableCleanerDto>> result) =>
            Assert.IsType<List<LinkableCleanerDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

        private async Task<List<AuditLog>> LinkAuditRowsAsync() =>
            await _context.AuditLogs
                .Where(a => a.EntityType == AuditEntityTypes.CleanerAccountLink)
                .OrderBy(a => a.Id)
                .ToListAsync();

        private static JObject Payload(AuditLog log) =>
            JsonConvert.DeserializeObject<JObject>(log.NewValues ?? "{}") ?? new JObject();

        // ── 1. Searching the roster ───────────────────────────────────────────────────────

        [Fact]
        public async Task LinkPickerSearch_MatchesFirstName_LastName_Email_AndPhone()
        {
            CleanerRecord(1, "Nino", "Beridze", "nino.b@example.com", "2125550134");
            CleanerRecord(2, "Giorgi", "Tsiklauri", "giorgi@example.com", "7185559911");

            foreach (var term in new[] { "nino", "beridze", "nino.b@", "2125550134" })
            {
                var rows = Linkable(await _controller.GetLinkableCleaners(term));
                var match = Assert.Single(rows);
                Assert.Equal(1, match.CleanerId);
            }
        }

        [Fact]
        public async Task LinkPickerSearch_MatchesTheFullNameAcrossTheGap()
        {
            // "nino b" spans both columns, so a term matched against each column alone finds
            // nothing - which is exactly how somebody types a name they half remember.
            CleanerRecord(1, "Nino", "Beridze", "nino.b@example.com");
            CleanerRecord(2, "Nino", "Kapanadze", "nino.k@example.com");

            var rows = Linkable(await _controller.GetLinkableCleaners("nino b"));

            Assert.Equal(1, Assert.Single(rows).CleanerId);
        }

        [Fact]
        public async Task LinkPickerSearch_IsCaseInsensitive_AndABlankTermReturnsEverybody()
        {
            CleanerRecord(1, "Nino", "Beridze");
            CleanerRecord(2, "Giorgi", "Tsiklauri");

            Assert.Equal(1, Assert.Single(Linkable(await _controller.GetLinkableCleaners("NINO"))).CleanerId);

            // The picker opens on the whole roster and the box only ever narrows it, so a blank
            // term must not be treated as "match nothing".
            Assert.Equal(2, Linkable(await _controller.GetLinkableCleaners(null)).Count);
            Assert.Equal(2, Linkable(await _controller.GetLinkableCleaners("   ")).Count);
        }

        [Fact]
        public async Task LinkPickerSearch_StillReturnsACleanerTakenByAnotherAccount_Named()
        {
            var holder = Account(7, "Nino", "B", "nino@example.com");
            CleanerRecord(1, "Nino", "Beridze", "nino.b@example.com", userId: holder.Id);

            var row = Assert.Single(Linkable(await _controller.GetLinkableCleaners("nino")));

            // Present, and carrying who holds it: "why is she not in the list" has to be answerable
            // from the list rather than from an absence.
            Assert.Equal(7, row.LinkedUserId);
            Assert.Equal("nino@example.com", row.LinkedUserEmail);
        }

        // ── 2. Every change leaves a readable row ─────────────────────────────────────────

        [Fact]
        public async Task Linking_LeavesAnAuditRow_NamingBothSidesAndTheEmailItOverwrote()
        {
            Account(11, "Nino", "B", "nino.login@example.com");
            CleanerRecord(1, "Nino", "Beridze", "old.address@example.com");

            var result = await _controller.LinkCleaner(11, new LinkCleanerAccountDto { CleanerId = 1 });
            Assert.IsType<OkObjectResult>(result.Result);

            var log = Assert.Single(await LinkAuditRowsAsync());
            Assert.Equal("Linked", log.Action);
            // Keyed on the ACCOUNT, because that is what the admin acted on and what they will
            // search the trail by. The cleaner record is named in the payload.
            Assert.Equal(11, log.EntityId);

            var payload = Payload(log);
            Assert.Equal(1, payload["CleanerId"]!.Value<int>());
            Assert.Equal("Nino Beridze", payload["CleanerName"]!.Value<string>());
            // The side effect worth recording: the assignment mail now goes somewhere else.
            Assert.Equal("old.address@example.com", payload["CleanerEmailBefore"]!.Value<string>());
            Assert.Equal("nino.login@example.com", payload["CleanerEmailAfter"]!.Value<string>());

            // And no phantom key on an ordinary link - a permanent empty "also detached" row reads
            // as a fact about the write.
            Assert.Null(payload["ReleasedCleanerIds"]);
        }

        [Fact]
        public async Task Relinking_NamesTheCleanerItSilentlyDetached()
        {
            Account(11, "Nino", "B", "nino.login@example.com");
            CleanerRecord(1, "Nino", "Beridze", userId: 11);
            CleanerRecord(2, "Nino", "Kapanadze");

            await _controller.LinkCleaner(11, new LinkCleanerAccountDto { CleanerId = 2 });

            var payload = Payload(Assert.Single(await LinkAuditRowsAsync()));
            Assert.Equal(2, payload["CleanerId"]!.Value<int>());
            // Cleaner #1 was released with no separate admin action, so the row that caused it is
            // the only place that can explain the absence.
            Assert.Equal("1", payload["ReleasedCleanerIds"]!.Value<string>());
            Assert.Null((await _context.Cleaners.FindAsync(1))!.UserId);
        }

        [Fact]
        public async Task Unlinking_LeavesAnAuditRow_AndIsWrittenAfterTheSaveSucceeds()
        {
            Account(11, "Nino", "B", "nino.login@example.com");
            CleanerRecord(1, "Nino", "Beridze", "nino.login@example.com", userId: 11);

            var result = await _controller.UnlinkCleaner(11);
            Assert.IsType<OkObjectResult>(result.Result);

            var log = Assert.Single(await LinkAuditRowsAsync());
            Assert.Equal("Unlinked", log.Action);
            Assert.Equal(11, log.EntityId);

            var payload = Payload(log);
            Assert.Equal(1, payload["CleanerId"]!.Value<int>());
            // Unlinking says the login no longer opens the portal, NOT that we have lost the
            // person's contact details - so the record keeps its address and the row says so.
            Assert.Equal("nino.login@example.com", payload["CleanerEmailKept"]!.Value<string>());

            Assert.Null((await _context.Cleaners.FindAsync(1))!.UserId);
        }

        [Fact]
        public async Task TheCleanerRecordsOwnHistoryIsWrittenToo_BecauseItAnswersADifferentQuestion()
        {
            Account(11, "Nino", "B", "nino.login@example.com");
            CleanerRecord(1, "Nino", "Beridze", "old.address@example.com");

            await _controller.LinkCleaner(11, new LinkCleanerAccountDto { CleanerId = 1 });

            // Two streams, on purpose: the Cleaner row records that UserId and Email moved on that
            // record (what its own history needs); the CleanerAccountLink row records who gave
            // which login access to whose schedule. Neither answers the other question.
            var cleanerRows = await _context.AuditLogs
                .Where(a => a.EntityType == nameof(Cleaner) && a.EntityId == 1)
                .ToListAsync();

            Assert.NotEmpty(cleanerRows);
            var changed = JsonConvert.DeserializeObject<List<string>>(cleanerRows[0].ChangedFields ?? "[]")!;
            Assert.Contains("UserId", changed);
            Assert.Contains("Email", changed);
        }

        [Fact]
        public void TheAccountLinkStream_IsUndoBlocked_WithAReason()
        {
            // A pseudo-entity the generic reflection undo cannot write back: replaying it would
            // resolve against a same-named class and edit the wrong record.
            Assert.True(AuditEntityTypes.UndoBlockedEntityTypes.Contains(AuditEntityTypes.CleanerAccountLink));
            Assert.False(string.IsNullOrWhiteSpace(
                AuditEntityTypes.UndoBlockedReasons[AuditEntityTypes.CleanerAccountLink]));
        }

        // ── 3. A linked cleaner's email belongs to their ACCOUNT ─────────────────────────
        //
        // Linking copies the account address onto the record so the assignment mail reaches whoever
        // reads the portal. Editing it on the Cleaners Dashboard afterwards would break exactly
        // that, and break it SILENTLY: the FK keeps the portal working, so nothing visibly fails
        // while the mail quietly goes somewhere else.

        private static UpdateCleanerDto UpdatePayload(Cleaner c, string? email) => new()
        {
            FirstName = c.FirstName,
            LastName = c.LastName,
            Email = email,
            Phone = c.Phone,
            Ranking = c.Ranking,
            IsActive = c.IsActive
        };

        [Fact]
        public void TheRuleNeedsBOTHALinkAndASendableAccountAddress()
        {
            Assert.True(CleanerAccountLink.EmailIsManagedByAccount(11, "nino@example.com"));

            // No account: the record's email is nobody else's business.
            Assert.False(CleanerAccountLink.EmailIsManagedByAccount(null, "nino@example.com"));

            // Linked, but the account has no sendable address (a no-email cash customer who was
            // promoted). Nothing was ever copied onto the record, so that email is its own contact
            // detail and locking it would strand the only address anybody has.
            Assert.False(CleanerAccountLink.EmailIsManagedByAccount(11, null));
            Assert.False(CleanerAccountLink.EmailIsManagedByAccount(11, "   "));
            Assert.False(CleanerAccountLink.EmailIsManagedByAccount(11, "nobody@no-email.invalid"));
        }

        [Fact]
        public async Task TheDetailSaysTheEmailIsManaged_SoTheFormCanLockIt()
        {
            Account(11, "Nino", "B", "nino.login@example.com");
            var cleaner = CleanerRecord(1, "Nino", "Beridze", "nino.login@example.com", userId: 11);

            var detail = await _cleaners.GetByIdAsync(cleaner.Id);

            Assert.NotNull(detail);
            Assert.True(detail!.IsEmailManagedByAccount);
            Assert.Equal(11, detail.LinkedUserId);
            Assert.Equal("nino.login@example.com", detail.LinkedAccountEmail);
            Assert.Equal("Nino B", detail.LinkedAccountName);
        }

        [Fact]
        public async Task AnUnlinkedCleanersEmailStaysEditable()
        {
            var cleaner = CleanerRecord(1, "Nino", "Beridze", "old@example.com");

            var detail = await _cleaners.GetByIdAsync(cleaner.Id);
            Assert.False(detail!.IsEmailManagedByAccount);
            Assert.Null(detail.LinkedUserId);

            var saved = await _cleaners.UpdateAsync(cleaner.Id, UpdatePayload(cleaner, "new@example.com"), adminId: 1);
            Assert.Equal("new@example.com", saved!.Email);
        }

        [Fact]
        public async Task ChangingALinkedCleanersEmailIsREFUSED_NotSilentlyIgnored()
        {
            Account(11, "Nino", "B", "nino.login@example.com");
            var cleaner = CleanerRecord(1, "Nino", "Beridze", "nino.login@example.com", userId: 11);

            // Refused rather than ignored: an admin who got past the read-only field typed that
            // address on purpose, and quietly keeping the old one would tell them it saved.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _cleaners.UpdateAsync(cleaner.Id, UpdatePayload(cleaner, "somewhere.else@example.com"), adminId: 1));

            // The message has to say where the change actually belongs.
            Assert.Contains("nino.login@example.com", ex.Message);
            Assert.Contains("account", ex.Message);
            Assert.Equal("nino.login@example.com", (await _context.Cleaners.FindAsync(1))!.Email);
        }

        [Fact]
        public async Task ClearingALinkedCleanersEmailIsRefusedToo()
        {
            Account(11, "Nino", "B", "nino.login@example.com");
            var cleaner = CleanerRecord(1, "Nino", "Beridze", "nino.login@example.com", userId: 11);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _cleaners.UpdateAsync(cleaner.Id, UpdatePayload(cleaner, null), adminId: 1));
        }

        [Fact]
        public async Task ASaveThatLeavesTheEmailAloneStillGoesThrough()
        {
            Account(11, "Nino", "B", "nino.login@example.com");
            var cleaner = CleanerRecord(1, "Nino", "Beridze", "nino.login@example.com", userId: 11);

            // A read-only field still round-trips its value, and case and surrounding space are not
            // changes - a save that touched the last name must not be refused.
            var payload = UpdatePayload(cleaner, "  NINO.Login@Example.com ");
            payload.LastName = "Beridze-Kapanadze";

            var saved = await _cleaners.UpdateAsync(cleaner.Id, payload, adminId: 1);

            Assert.Equal("Beridze-Kapanadze", saved!.LastName);
        }

        [Fact]
        public async Task ALinkedCleanerWhoseAccountHasNoEmailKeepsAnEditableAddress()
        {
            // The one case the rule deliberately leaves open - see
            // TheRuleNeedsBOTHALinkAndASendableAccountAddress.
            var noEmail = Account(11, "Cash", "Customer", "user11@no-email.invalid");
            noEmail.IsNoEmailUser = true;
            _context.SaveChanges();

            var cleaner = CleanerRecord(1, "Cash", "Customer", "reach.them.here@example.com", userId: 11);

            var detail = await _cleaners.GetByIdAsync(cleaner.Id);
            Assert.False(detail!.IsEmailManagedByAccount);

            var saved = await _cleaners.UpdateAsync(cleaner.Id, UpdatePayload(cleaner, "corrected@example.com"), adminId: 1);
            Assert.Equal("corrected@example.com", saved!.Email);
        }

        // ── Guard rails the tab already relied on ─────────────────────────────────────────

        [Fact]
        public async Task LinkingRefusesAnAccountThatIsNotOnTheCleanerRole()
        {
            Account(11, "Nino", "B", "nino@example.com", UserRole.Customer);
            CleanerRecord(1, "Nino", "Beridze");

            var result = await _controller.LinkCleaner(11, new LinkCleanerAccountDto { CleanerId = 1 });

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(await LinkAuditRowsAsync());
        }

        [Fact]
        public async Task LinkingRefusesACleanerHeldByAnotherAccount_RatherThanStealingIt()
        {
            Account(11, "Nino", "B", "nino@example.com");
            Account(12, "Ana", "P", "ana@example.com");
            CleanerRecord(1, "Nino", "Beridze", userId: 12);

            var result = await _controller.LinkCleaner(11, new LinkCleanerAccountDto { CleanerId = 1 });

            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal(12, (await _context.Cleaners.FindAsync(1))!.UserId);
        }
    }
}

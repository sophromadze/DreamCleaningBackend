using System.Net;
using System.Reflection;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// PRIVATE UPLOADS (2026-10) — exercised over real HTTP with the cookie a same-origin
    /// &lt;img src&gt; sends. The contract: the right people get the bytes with private headers;
    /// EVERYONE else — anonymous, another customer, another cleaner, a staff role the page does
    /// not admit — gets the same 404 an unknown id gets, so the endpoints never confirm that a
    /// file exists. Stored paths that try to leave their folder resolve to nothing.
    /// </summary>
    public class PrivateFileAccessTests : IAsyncLifetime
    {
        private PrivateFileTestHost _host = null!;

        // Accounts
        private const int OwnerCleanerUserId = 40;    // login account of cleaner 1
        private const int OtherCleanerUserId = 41;    // login account of cleaner 2
        private const int CustomerId = 50;            // owns cleaning photo 10
        private const int OtherCustomerId = 51;
        private const int StaffUserId = 1;

        private static readonly Guid SessionA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid SessionB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        private const string ChatFileA = "0123456789abcdef0123456789abcdef.jpg";
        private const string UnsentChatFile = "fedcba9876543210fedcba9876543210.png";

        public async Task InitializeAsync()
        {
            _host = await PrivateFileTestHost.StartAsync(
                new[] { typeof(PrivateFilesController), typeof(ChatController) },
                services =>
                {
                    services.AddSingleton(NullService<IChatCatalogService>());
                    services.AddSingleton(NullService<IChatAgentService>());
                },
                (app, root) => PublicUploadStaticFiles.Use(app, root));

            var photo1 = _host.WriteUpload("cleaners/photos/cleaner-1-20260101000000000.webp");
            var doc1 = _host.WriteUpload("cleaners/documents/cleaner-1-doc-20260427191758357.webp");
            var doc2 = _host.WriteUpload("cleaners/documents/cleaner-2-doc.webp");
            var cleaningPhoto = _host.WriteUpload("user-cleaning-photos/user-50-a.webp");
            var chatA = _host.WriteUpload("chat-photos/" + ChatFileA);
            _host.WriteUpload("chat-photos/" + UnsentChatFile);
            _host.WriteUpload("images/logo.webp");
            // A file in the uploads root but outside every private folder, for traversal rows.
            _host.WriteUpload("secret.webp");

            await _host.SeedAsync(db =>
            {
                db.Cleaners.Add(new Cleaner { Id = 1, FirstName = "A", LastName = "One", UserId = OwnerCleanerUserId, PhotoUrl = photo1, DocumentUrl = doc1 });
                db.Cleaners.Add(new Cleaner { Id = 2, FirstName = "B", LastName = "Two", UserId = OtherCleanerUserId, DocumentUrl = doc2 });
                // Corrupted rows: one climbs out of its folder, one names a file in the WRONG folder.
                db.Cleaners.Add(new Cleaner { Id = 3, FirstName = "C", LastName = "Three", DocumentUrl = "/cleaners/documents/../../secret.webp" });
                db.Cleaners.Add(new Cleaner { Id = 4, FirstName = "D", LastName = "Four", PhotoUrl = doc1 });
                db.Cleaners.Add(new Cleaner { Id = 5, FirstName = "E", LastName = "Five" });

                db.UserCleaningPhotos.Add(new UserCleaningPhoto { Id = 10, UserId = CustomerId, PhotoUrl = cleaningPhoto });
                db.UserCleaningPhotos.Add(new UserCleaningPhoto { Id = 11, UserId = CustomerId, PhotoUrl = "/user-cleaning-photos/../secret.webp" });

                db.ChatAgentSettings.Add(new ChatAgentSettings { Id = 1, VisibilityMode = ChatWidgetVisibility.Public });
                db.ChatAgentSessions.Add(new ChatAgentSession { Id = SessionA, CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow });
                db.ChatAgentSessions.Add(new ChatAgentSession { Id = SessionB, CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow });
                db.ChatAgentMessages.Add(new ChatAgentMessage { Id = Guid.NewGuid(), ChatSessionId = SessionA, Role = ChatMessageRole.User, ImagePath = chatA, CreatedAt = DateTime.UtcNow });
                db.ChatAgentMessages.Add(new ChatAgentMessage { Id = Guid.NewGuid(), ChatSessionId = SessionB, Role = ChatMessageRole.User, Content = "hi", CreatedAt = DateTime.UtcNow });
            });
        }

        public async Task DisposeAsync() => await _host.DisposeAsync();

        // ── Cleaner documents ────────────────────────────────────────────────────

        [Theory]
        [InlineData(UserRole.Moderator)]
        [InlineData(UserRole.Admin)]
        [InlineData(UserRole.SuperAdmin)]
        public async Task CleanerDocument_EveryRoleThatOpensTheCleanersDashboard_SeesIt(UserRole role)
        {
            var response = await _host.GetAsync("/api/files/cleaners/1/document", StaffUserId, role);

            HttpStatusAssert.Is(HttpStatusCode.OK, response, $"{role} sees cleaner documents on the dashboard today");
            Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(PrivateFileTestHost.WebpBytes, await response.Content.ReadAsByteArrayAsync());
            AssertNoStore(response);
            Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
            var disposition = response.Content.Headers.ContentDisposition;
            Assert.Equal("inline", disposition?.DispositionType);
            Assert.Equal("cleaner-1-document.webp", disposition?.FileName?.Trim('"'));
        }

        [Fact]
        public async Task CleanerDocument_TheCleanerThemself_SeesOnlyTheirOwn()
        {
            HttpStatusAssert.Is(HttpStatusCode.OK,
                await _host.GetAsync("/api/files/cleaners/1/document", OwnerCleanerUserId, UserRole.Cleaner),
                "the owning cleaner (Cleaner.UserId) may see their own document");
            HttpStatusAssert.Is(HttpStatusCode.NotFound,
                await _host.GetAsync("/api/files/cleaners/2/document", OwnerCleanerUserId, UserRole.Cleaner),
                "a cleaner must not see another cleaner's document");
        }

        [Fact]
        public async Task CleanerDocument_AnonymousAndCustomers_Get404()
        {
            HttpStatusAssert.Is(HttpStatusCode.NotFound, await _host.GetAsync("/api/files/cleaners/1/document"), "anonymous");
            HttpStatusAssert.Is(HttpStatusCode.NotFound,
                await _host.GetAsync("/api/files/cleaners/1/document", CustomerId, UserRole.Customer), "a customer");
        }

        [Fact]
        public async Task Refusals_LookExactlyLikeAMissingFile()
        {
            // Unknown cleaner, cleaner with no document, and a refused caller: same answer.
            var unknown = await _host.GetAsync("/api/files/cleaners/999/document", StaffUserId, UserRole.Admin);
            var noDocument = await _host.GetAsync("/api/files/cleaners/5/document", StaffUserId, UserRole.Admin);
            var refused = await _host.GetAsync("/api/files/cleaners/1/document", CustomerId, UserRole.Customer);

            foreach (var r in new[] { unknown, noDocument, refused })
                HttpStatusAssert.Is(HttpStatusCode.NotFound, r, "every refusal is a plain 404");
            Assert.Equal(WithoutTraceId(await unknown.Content.ReadAsStringAsync()), WithoutTraceId(await refused.Content.ReadAsStringAsync()));
        }

        [Fact]
        public async Task StoredPathsThatLeaveTheirFolder_ResolveToNothing()
        {
            HttpStatusAssert.Is(HttpStatusCode.NotFound,
                await _host.GetAsync("/api/files/cleaners/3/document", StaffUserId, UserRole.SuperAdmin),
                "a stored ../ path must not reach a file outside cleaners/documents");
            HttpStatusAssert.Is(HttpStatusCode.NotFound,
                await _host.GetAsync("/api/files/cleaners/4/photo", StaffUserId, UserRole.SuperAdmin),
                "a photo field naming a documents-folder file must not serve it");
            HttpStatusAssert.Is(HttpStatusCode.NotFound,
                await _host.GetAsync("/api/files/cleaning-photos/11", StaffUserId, UserRole.SuperAdmin),
                "a cleaning-photo row climbing out of its folder");
        }

        [Theory]
        [InlineData("/api/files/cleaners/1/document/../../../secret.webp")]
        [InlineData("/api/files/cleaners/..%2F..%2Fsecret.webp/document")]
        [InlineData("/api/files/chat/..%2Fsecret.webp")]
        [InlineData("/api/files/chat/..%5Csecret.webp")]
        [InlineData("/api/chat/session/aaaaaaaa-0000-0000-0000-000000000001/images/..%2F..%2Fsecret.webp")]
        public async Task RequestPathTraversal_NeverReachesAFile(string url)
        {
            var response = await _host.GetAsync(url, StaffUserId, UserRole.SuperAdmin);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }

        // ── Cleaner photos ───────────────────────────────────────────────────────

        [Fact]
        public async Task CleanerPhoto_StaffSeeIt_WithAShortPrivateCache()
        {
            var response = await _host.GetAsync("/api/files/cleaners/1/photo", StaffUserId, UserRole.Admin);
            HttpStatusAssert.Is(HttpStatusCode.OK, response, "admin sees the cleaner photo");
            var cache = response.Headers.CacheControl!;
            Assert.True(cache.Private);
            Assert.Equal(TimeSpan.FromSeconds(300), cache.MaxAge);
            Assert.False(cache.Public);
        }

        [Fact]
        public async Task CleanerPhoto_IsNotForCustomersOrAnonymous()
        {
            HttpStatusAssert.Is(HttpStatusCode.NotFound, await _host.GetAsync("/api/files/cleaners/1/photo"), "anonymous");
            HttpStatusAssert.Is(HttpStatusCode.NotFound,
                await _host.GetAsync("/api/files/cleaners/1/photo", CustomerId, UserRole.Customer), "a customer");
            HttpStatusAssert.Is(HttpStatusCode.NotFound,
                await _host.GetAsync("/api/files/cleaners/1/photo", OtherCleanerUserId, UserRole.Cleaner), "another cleaner");
        }

        [Fact]
        public void CleanerPhotoUrl_ChangesWhenThePhotoIsReplaced()
        {
            var before = PrivateFileUrls.CleanerPhoto(1, "/cleaners/photos/a.webp");
            var after = PrivateFileUrls.CleanerPhoto(1, "/cleaners/photos/b.webp");
            Assert.StartsWith("/api/files/cleaners/1/photo?v=", before);
            Assert.NotEqual(before, after);
            Assert.Null(PrivateFileUrls.CleanerPhoto(1, null));
        }

        // ── Customer cleaning photos ─────────────────────────────────────────────

        [Fact]
        public async Task CleaningPhoto_OwnerAndStaffSeeIt_NobodyElse()
        {
            HttpStatusAssert.Is(HttpStatusCode.OK, await _host.GetAsync("/api/files/cleaning-photos/10", CustomerId, UserRole.Customer), "the customer who owns it");
            HttpStatusAssert.Is(HttpStatusCode.OK, await _host.GetAsync("/api/files/cleaning-photos/10", StaffUserId, UserRole.Moderator), "a Moderator (Users/Orders panels hold View)");
            HttpStatusAssert.Is(HttpStatusCode.NotFound, await _host.GetAsync("/api/files/cleaning-photos/10", OtherCustomerId, UserRole.Customer), "another customer");
            HttpStatusAssert.Is(HttpStatusCode.NotFound, await _host.GetAsync("/api/files/cleaning-photos/10", OwnerCleanerUserId, UserRole.Cleaner), "a cleaner");
            HttpStatusAssert.Is(HttpStatusCode.NotFound, await _host.GetAsync("/api/files/cleaning-photos/10"), "anonymous");
        }

        // ── Chat photos ──────────────────────────────────────────────────────────

        [Fact]
        public async Task ChatPhoto_VisitorSeesItThroughTheirOwnSession_Only()
        {
            var own = await _host.GetAsync($"/api/chat/session/{SessionA}/images/{ChatFileA}");
            HttpStatusAssert.Is(HttpStatusCode.OK, own, "the visitor holding the session sees their photo");
            AssertNoStore(own);

            HttpStatusAssert.Is(HttpStatusCode.NotFound,
                await _host.GetAsync($"/api/chat/session/{SessionB}/images/{ChatFileA}"),
                "another session's id paired with this file");
            HttpStatusAssert.Is(HttpStatusCode.NotFound,
                await _host.GetAsync($"/api/chat/session/{Guid.NewGuid()}/images/{ChatFileA}"),
                "an unknown session");
        }

        [Fact]
        public async Task ChatPhoto_IsGoneForEveryoneWhileTheChatIsDisabled()
        {
            await _host.SeedAsync(db => db.ChatAgentSettings.Single().VisibilityMode = ChatWidgetVisibility.Disabled);
            try
            {
                HttpStatusAssert.Is(HttpStatusCode.NotFound,
                    await _host.GetAsync($"/api/chat/session/{SessionA}/images/{ChatFileA}"), "chat disabled");
            }
            finally
            {
                await _host.SeedAsync(db => db.ChatAgentSettings.Single().VisibilityMode = ChatWidgetVisibility.Public);
            }
        }

        [Theory]
        [InlineData(UserRole.Admin, HttpStatusCode.OK)]
        [InlineData(UserRole.SuperAdmin, HttpStatusCode.OK)]
        [InlineData(UserRole.Moderator, HttpStatusCode.NotFound)] // the Chats tab is Admin/SuperAdmin
        [InlineData(UserRole.Customer, HttpStatusCode.NotFound)]
        public async Task ChatPhoto_StaffRoute_FollowsTheChatsTabGate(UserRole role, HttpStatusCode expected)
        {
            var response = await _host.GetAsync($"/api/files/chat/{ChatFileA}", StaffUserId, role);
            HttpStatusAssert.Is(expected, response, $"{role} on the admin chat image route");
        }

        [Fact]
        public async Task ChatPhoto_AnUploadNoMessageUses_IsNobodys()
        {
            HttpStatusAssert.Is(HttpStatusCode.NotFound,
                await _host.GetAsync($"/api/files/chat/{UnsentChatFile}", StaffUserId, UserRole.SuperAdmin),
                "a file no message references");
            HttpStatusAssert.Is(HttpStatusCode.NotFound,
                await _host.GetAsync($"/api/chat/session/{SessionA}/images/{UnsentChatFile}"),
                "an unsent upload through a real session");
        }

        [Fact]
        public async Task ChatPhoto_SignedTranscriptLink_WorksWithoutACookie_UntilItExpires()
        {
            var secret = SigningSecret();
            var link = PrivateFileLinkSigner.SignedChatLink(secret, "https://example.test", "/chat-photos/" + ChatFileA, DateTime.UtcNow)!;
            var pathAndQuery = new Uri(link).PathAndQuery;

            HttpStatusAssert.Is(HttpStatusCode.OK, await _host.GetAsync(pathAndQuery), "a fresh signed link, no cookie (Telegram / mail client)");

            var expired = PrivateFileLinkSigner.SignedChatLink(secret, "https://example.test", "/chat-photos/" + ChatFileA,
                DateTime.UtcNow - PrivateFileLinkSigner.ChatTranscriptLifetime - TimeSpan.FromMinutes(1))!;
            HttpStatusAssert.Is(HttpStatusCode.NotFound, await _host.GetAsync(new Uri(expired).PathAndQuery), "an expired link");

            var tampered = pathAndQuery.Replace("exp=", "exp=9");
            HttpStatusAssert.Is(HttpStatusCode.NotFound, await _host.GetAsync(tampered), "a link whose expiry was edited");

            var otherFile = pathAndQuery.Replace(ChatFileA, UnsentChatFile);
            HttpStatusAssert.Is(HttpStatusCode.NotFound, await _host.GetAsync(otherFile), "a signature moved onto another file");

            Assert.Null(PrivateFileLinkSigner.SignedChatLink(null, "https://example.test", "/chat-photos/" + ChatFileA, DateTime.UtcNow));
        }

        // ── The API's own static-file mapping ────────────────────────────────────

        [Theory]
        [InlineData("/cleaners/documents/cleaner-1-doc-20260427191758357.webp")]
        [InlineData("/cleaners/photos/cleaner-1-20260101000000000.webp")]
        [InlineData("/user-cleaning-photos/user-50-a.webp")]
        [InlineData("/chat-photos/" + ChatFileA)]
        [InlineData("/secret.webp")]
        public async Task PrivateFolders_AreNoLongerStaticFiles(string url)
        {
            HttpStatusAssert.Is(HttpStatusCode.NotFound, await _host.GetAsync(url), $"{url} must not be served off disk");
        }

        [Fact]
        public async Task PublicFolders_AreStillStaticFiles()
        {
            HttpStatusAssert.Is(HttpStatusCode.OK, await _host.GetAsync("/images/logo.webp"), "public images keep working");
        }

        // ── New uploads get random names; DTOs never carry a stored private path ──

        [Fact]
        public async Task CleanerUploads_GetRandomNames_AndReturnEndpointUrls()
        {
            await using var scope = new ServiceScopeHolder(_host);
            var db = scope.Get<ApplicationDbContext>();
            var config = scope.Get<IConfiguration>();
            var service = new CleanerManagementService(db, config, scope.Get<IAuditService>());

            var result = await service.UploadDocumentAsync(5, PngFormFile());
            Assert.NotNull(result);
            Assert.StartsWith("/api/files/cleaners/5/document?v=", result!.Url);

            var stored = (await db.Cleaners.AsNoTracking().SingleAsync(c => c.Id == 5)).DocumentUrl!;
            Assert.Matches(@"^/cleaners/documents/[0-9a-f]{32}\.webp$", stored);

            var detail = await service.GetByIdAsync(5);
            Assert.Equal(result.Url, detail!.DocumentUrl);
            var list = await service.GetAllAsync(includeInactive: true);
            Assert.All(list.Where(c => c.PhotoUrl != null), c => Assert.StartsWith("/api/files/cleaners/", c.PhotoUrl));
        }

        [Fact]
        public async Task CleaningPhotoUploads_GetRandomNames()
        {
            await using var scope = new ServiceScopeHolder(_host);
            var service = new UserCleaningPhotoService(scope.Get<ApplicationDbContext>(), scope.Get<IConfiguration>());

            using var png = new MemoryStream(PngBytes());
            var photo = await service.SavePhotoFromStreamAsync(CustomerId, 77, png);
            Assert.Matches(@"^/user-cleaning-photos/[0-9a-f]{32}\.webp$", photo.PhotoUrl);
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private static void AssertNoStore(HttpResponseMessage response)
        {
            var cache = response.Headers.CacheControl!;
            Assert.True(cache.Private, "Cache-Control must be private");
            Assert.True(cache.NoStore, "Cache-Control must be no-store");
        }

        private static string WithoutTraceId(string body)
            => System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "");

        private static string SigningSecret()
            => "private-file-tests-signing-key-0123456789-abcdefghijklmnopqrstuvwxyz";

        private static byte[] PngBytes()
        {
            using var image = new Image<Rgba32>(4, 4);
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }

        private static IFormFile PngFormFile()
        {
            var bytes = PngBytes();
            return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "id-card.png");
        }

        private static T NullService<T>() where T : class => DispatchProxy.Create<T, NullProxy>();

        public class NullProxy : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
                => throw new NotSupportedException($"{targetMethod?.Name} is not used by these tests");
        }

        private sealed class ServiceScopeHolder : IAsyncDisposable
        {
            private readonly AsyncServiceScope _scope;
            public ServiceScopeHolder(PrivateFileTestHost host) => _scope = host.Services.CreateAsyncScope();
            public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();
            public ValueTask DisposeAsync() => _scope.DisposeAsync();
        }
    }
}

using System.Net;
using System.Text.Json;
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
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using static DreamCleaningBackend.Helpers.PrivateFileUrls;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// PRODUCTION FIX (2026-10): five cleaners' PhotoUrl is the Google profile picture they signed
    /// in with (https://lh3.googleusercontent.com/a/...=s96-c), not one of our uploads. The private
    /// files release turned EVERY stored PhotoUrl into /api/files/cleaners/{id}/photo, which serves
    /// local files only, so those avatars 404'd in admin.
    ///
    /// The rule now, per converter: a LOCAL upload path goes through the access-checked endpoint;
    /// an external https URL is returned as-is where that is safe (cleaner photos — already public
    /// on their own host); private kinds (documents, customer cleaning photos) never expose anything
    /// but a local upload and log a warning instead; plain http and junk are never exposed.
    /// </summary>
    public class ExternalStoredUrlTests : IAsyncLifetime
    {
        private const string Google = "https://lh3.googleusercontent.com/a/ACg8ocJxyz=s96-c";
        private const string PlainHttp = "http://example.com/me.jpg";
        private const string Local = "/cleaners/photos/abc.webp";

        private PrivateFileTestHost _host = null!;

        public async Task InitializeAsync()
        {
            _host = await PrivateFileTestHost.StartAsync(new[] { typeof(PrivateFilesController), typeof(AdminUserCareController) });
        }

        public async Task DisposeAsync() => await _host.DisposeAsync();

        // ── Classification ───────────────────────────────────────────────────────

        [Theory]
        [InlineData(null, StoredFileKind.None)]
        [InlineData("", StoredFileKind.None)]
        [InlineData("/cleaners/photos/a.webp", StoredFileKind.Local)]
        [InlineData(Google, StoredFileKind.ExternalHttps)]
        [InlineData(PlainHttp, StoredFileKind.Unsupported)]
        [InlineData("//evil.example/x.jpg", StoredFileKind.Unsupported)]
        [InlineData("javascript:alert(1)", StoredFileKind.Unsupported)]
        [InlineData("cleaners/photos/a.webp", StoredFileKind.Unsupported)]
        public void StoredValuesAreClassifiedOneWay(string? stored, StoredFileKind expected)
            => Assert.Equal(expected, Classify(stored));

        // ── Cleaner photos ───────────────────────────────────────────────────────

        [Fact]
        public void CleanerPhoto_Local_GoesThroughTheEndpointWithAVersion()
            => Assert.StartsWith("/api/files/cleaners/7/photo?v=", CleanerPhoto(7, Local));

        [Fact]
        public void CleanerPhoto_ExternalHttps_IsReturnedAsIs_WithNoVersionAppended()
            => Assert.Equal(Google, CleanerPhoto(7, Google));

        [Fact]
        public void CleanerPhoto_PlainHttp_IsNotExposed_AndIsLogged()
        {
            var log = new CapturingLogger();
            Assert.Null(CleanerPhoto(9101, PlainHttp, log));
            var warning = Assert.Single(log.Warnings);
            Assert.Contains("http://example.com/...", warning);
        }

        // ── Cleaner documents: only ever a local upload ──────────────────────────

        [Fact]
        public void CleanerDocument_Local_GoesThroughTheEndpoint()
            => Assert.StartsWith("/api/files/cleaners/7/document?v=", CleanerDocument(7, "/cleaners/documents/d.webp"));

        [Theory]
        [InlineData(Google, 9201)]
        [InlineData(PlainHttp, 9202)]
        public void CleanerDocument_AnyExternalUrl_IsNotExposed_AndLogsAWarning(string stored, int id)
        {
            var log = new CapturingLogger();
            Assert.Null(CleanerDocument(id, stored, log));
            var warning = Assert.Single(log.Warnings);
            Assert.DoesNotContain("ACg8oc", warning); // only scheme + host are logged
        }

        [Fact]
        public void TheWarning_IsLoggedOncePerRow_NotOnEveryListLoad()
        {
            var log = new CapturingLogger();
            CleanerDocument(9301, Google, log);
            CleanerDocument(9301, Google, log);
            Assert.Single(log.Warnings);
        }

        // ── Customer cleaning photos: private, same rule as documents ────────────

        [Theory]
        [InlineData("/user-cleaning-photos/p.webp", "/api/files/cleaning-photos/5")]
        [InlineData(Google, null)]
        [InlineData(PlainHttp, null)]
        public void CleaningPhoto_OnlyALocalUploadIsExposed(string stored, string? expected)
            => Assert.Equal(expected, CleaningPhoto(5, stored));

        // ── Chat photos and the gift card background ignore non-local values ─────

        [Theory]
        [InlineData(Google)]
        [InlineData(PlainHttp)]
        public void ChatImages_NeverPointAtAnExternalUrl(string stored)
        {
            Assert.Null(ChatImageForSession(Guid.NewGuid(), stored));
            Assert.Null(ChatImageForStaff(stored));
        }

        [Theory]
        [InlineData(Google)]
        [InlineData(PlainHttp)]
        public void GiftCardBackground_ExternalValue_FallsBackToTheDefault(string stored)
            => Assert.Equal(GiftCardBackground.DefaultUrl, GiftCardBackground.Resolve(_host.UploadRoot, stored).Url);

        // ── Through the real code paths ──────────────────────────────────────────

        [Fact]
        public async Task CleanerService_ListAndDetail_KeepTheGooglePicture_AndHideAnExternalDocument()
        {
            var local = _host.WriteUpload("cleaners/photos/local.webp");
            await _host.SeedAsync(db =>
            {
                db.Cleaners.Add(new Cleaner { Id = 50, FirstName = "G", LastName = "Oogle", IsActive = true, PhotoUrl = Google, DocumentUrl = Google });
                db.Cleaners.Add(new Cleaner { Id = 51, FirstName = "L", LastName = "Ocal", IsActive = true, PhotoUrl = local });
                db.Cleaners.Add(new Cleaner { Id = 52, FirstName = "H", LastName = "Ttp", IsActive = true, PhotoUrl = PlainHttp });
            });

            using var scope = _host.Services.CreateScope();
            var log = new CapturingLogger<CleanerManagementService>();
            var service = new CleanerManagementService(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                scope.ServiceProvider.GetRequiredService<IAuditService>(),
                log);

            var list = (await service.GetAllAsync(includeInactive: true)).ToDictionary(c => c.Id);
            Assert.Equal(Google, list[50].PhotoUrl);
            Assert.StartsWith("/api/files/cleaners/51/photo?v=", list[51].PhotoUrl);
            Assert.Null(list[52].PhotoUrl);

            var detail = await service.GetByIdAsync(50);
            Assert.Equal(Google, detail!.PhotoUrl);
            Assert.Null(detail.DocumentUrl);
            Assert.Contains(log.Warnings, w => w.Contains("cleaner document") && w.Contains("50"));
        }

        [Fact]
        public async Task ReplacingAGooglePicture_WithAnUpload_Works()
        {
            await _host.SeedAsync(db => db.Cleaners.Add(new Cleaner { Id = 60, FirstName = "R", LastName = "Eplace", PhotoUrl = Google }));
            using var scope = _host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var service = new CleanerManagementService(db, scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                scope.ServiceProvider.GetRequiredService<IAuditService>());

            var result = await service.UploadPhotoAsync(60, Png());

            Assert.StartsWith("/api/files/cleaners/60/photo?v=", result!.Url);
            var stored = (await db.Cleaners.AsNoTracking().SingleAsync(c => c.Id == 60)).PhotoUrl;
            Assert.Equal(StoredFileKind.Local, Classify(stored));
        }

        [Fact]
        public async Task ThePhotoEndpoint_NeverFetchesAnExternalPicture()
        {
            await _host.SeedAsync(db => db.Cleaners.Add(new Cleaner { Id = 70, FirstName = "N", LastName = "O", PhotoUrl = Google }));
            HttpStatusAssert.Is(HttpStatusCode.NotFound,
                await _host.GetAsync("/api/files/cleaners/70/photo", 1, UserRole.Admin),
                "the endpoint serves local uploads only; the browser loads the Google URL directly");
        }

        [Fact]
        public async Task AdminCleaningPhotoList_ReturnsNoUrlForANonLocalRow()
        {
            var local = _host.WriteUpload("user-cleaning-photos/ok.webp");
            await _host.SeedAsync(db =>
            {
                db.Users.Add(new User { Id = 80, FirstName = "C", LastName = "U", Email = "c@example.test" });
                db.UserCleaningPhotos.Add(new UserCleaningPhoto { Id = 801, UserId = 80, OrderId = 1, PhotoUrl = local, CreatedAt = DateTime.UtcNow });
                db.UserCleaningPhotos.Add(new UserCleaningPhoto { Id = 802, UserId = 80, OrderId = 1, PhotoUrl = Google, CreatedAt = DateTime.UtcNow });
            });

            var response = await _host.GetAsync("/api/admin/user-care/users/80/cleaning-photos", 1, UserRole.Admin);
            HttpStatusAssert.Is(HttpStatusCode.OK, response, "photo list");
            var photos = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
                .EnumerateArray().SelectMany(g => g.GetProperty("photos").EnumerateArray())
                .ToDictionary(p => p.GetProperty("id").GetInt32(), p => p.GetProperty("photoUrl").GetString());

            Assert.Equal("/api/files/cleaning-photos/801", photos[801]);
            Assert.Equal(string.Empty, photos[802]);
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private static IFormFile Png()
        {
            using var image = new Image<Rgba32>(4, 4);
            var ms = new MemoryStream();
            image.SaveAsPng(ms);
            ms.Position = 0;
            return new FormFile(ms, 0, ms.Length, "file", "new.png");
        }

        private class CapturingLogger : ILogger
        {
            public List<string> Warnings { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class CapturingLogger<T> : CapturingLogger, ILogger<T> { }
    }
}

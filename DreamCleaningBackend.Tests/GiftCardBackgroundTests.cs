using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// GIFT CARD BACKGROUND (2026-10). Uploads moved out of {uploads}/images (which Apache aliased
    /// over the site's own /images) into {uploads}/public/gift-cards, served at /uploads/gift-cards/.
    /// The rule every surface relies on: the configured path is used only when its file exists in
    /// that folder; anything else is the bundled default, so the page, the admin preview and the
    /// email never show a broken image. Production's configured file is missing today, which is
    /// exactly the "legacy path, file gone" case below.
    /// </summary>
    public class GiftCardBackgroundTests : IAsyncLifetime
    {
        private PrivateFileTestHost _host = null!;

        public async Task InitializeAsync()
        {
            _host = await PrivateFileTestHost.StartAsync(
                new[] { typeof(AdminGiftCardsController) },
                services => services.AddScoped(_ => DispatchProxy.Create<IGiftCardService, PrivateFileAccessTests.NullProxy>()),
                (app, root) => PublicUploadStaticFiles.Use(app, root));
        }

        public async Task DisposeAsync() => await _host.DisposeAsync();

        // ── The resolution rule ──────────────────────────────────────────────────

        [Fact]
        public void NothingConfigured_IsTheDefault_AndNotReportedMissing()
        {
            var r = GiftCardBackground.Resolve(_host.UploadRoot, null);
            Assert.Equal(GiftCardBackground.DefaultUrl, r.Url);
            Assert.True(r.IsDefault);
            Assert.False(r.ConfiguredImageMissing);
        }

        [Fact]
        public void TheProductionCase_ALegacyImagesPathWhoseFileIsGone_FallsBackToTheDefault()
        {
            var r = GiftCardBackground.Resolve(_host.UploadRoot, "/images/gift-card-bg-20260227215142.webp");
            Assert.Equal(GiftCardBackground.DefaultUrl, r.Url);
            Assert.True(r.ConfiguredImageMissing, "the admin preview must be told to re-upload");
        }

        [Fact]
        public void AnExistingUpload_IsUsedAsIs()
        {
            _host.WriteUpload("public/gift-cards/gift-card-bg-abc.webp");
            var r = GiftCardBackground.Resolve(_host.UploadRoot, "/uploads/gift-cards/gift-card-bg-abc.webp");
            Assert.Equal("/uploads/gift-cards/gift-card-bg-abc.webp", r.Url);
            Assert.False(r.IsDefault);
        }

        [Theory]
        [InlineData("/uploads/gift-cards/missing.webp")]
        [InlineData("/uploads/gift-cards/../../cleaners/documents/doc.webp")]
        [InlineData("/uploads/gift-cards/..\\..\\secret.webp")]
        public void AMissingOrEscapingUpload_IsTheDefault(string stored)
        {
            _host.WriteUpload("cleaners/documents/doc.webp");
            _host.WriteUpload("secret.webp");
            Assert.Equal(GiftCardBackground.DefaultUrl, GiftCardBackground.Resolve(_host.UploadRoot, stored).Url);
        }

        [Fact]
        public void TheEmail_FallsBackToTheCopyBundledInAssets()
        {
            var path = GiftCardBackground.EmailImagePath(_host.UploadRoot, "/images/gift-card-bg-20260227215142.webp");
            Assert.NotNull(path);
            Assert.Equal(GiftCardBackground.DefaultAssetFileName, Path.GetFileName(path));
            Assert.True(new FileInfo(path!).Length > 0);
        }

        [Fact]
        public void TheEmail_UsesTheUploadWhenItExists()
        {
            _host.WriteUpload("public/gift-cards/gift-card-bg-mail.webp");
            var path = GiftCardBackground.EmailImagePath(_host.UploadRoot, "/uploads/gift-cards/gift-card-bg-mail.webp");
            Assert.EndsWith("gift-card-bg-mail.webp", path);
        }

        // ── Over HTTP ────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheConfigEndpoint_AnswersWithTheBackgroundInEffect()
        {
            await _host.SeedAsync(db => db.GiftCardConfigs.Add(new GiftCardConfig
            {
                Id = 1, BackgroundImagePath = "/images/gift-card-bg-20260227215142.webp", LastUpdated = DateTime.UtcNow
            }));

            var json = await GetJson("/api/admin/gift-card-config");
            Assert.Equal(GiftCardBackground.DefaultUrl, json.GetProperty("backgroundImagePath").GetString());
            Assert.False(json.GetProperty("hasBackground").GetBoolean());
            Assert.True(json.GetProperty("configuredImageMissing").GetBoolean());
        }

        [Fact]
        public async Task AnUpload_LandsInItsOwnFolder_WithAUniqueName_AndIsServedUnderUploads()
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(PngBytes());
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "background.png");
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/upload-gift-card-background") { Content = form };
            request.Headers.Add("Cookie", "access_token=" + PrivateFileTestHost.TokenFor(1, UserRole.Admin));

            var response = await _host.Client.SendAsync(request);
            HttpStatusAssert.Is(HttpStatusCode.OK, response, "admin upload");
            var imagePath = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("imagePath").GetString()!;

            Assert.Matches(@"^/uploads/gift-cards/gift-card-bg-[0-9a-f]{32}\.webp$", imagePath);
            Assert.True(File.Exists(Path.Combine(_host.UploadRoot, "public", "gift-cards", Path.GetFileName(imagePath))));
            Assert.False(Directory.Exists(Path.Combine(_host.UploadRoot, "images")) &&
                         Directory.EnumerateFiles(Path.Combine(_host.UploadRoot, "images"), "gift-card-bg-*").Any(),
                "nothing may be written into the uploads images folder any more");

            HttpStatusAssert.Is(HttpStatusCode.OK, await _host.GetAsync(imagePath), "the new background is publicly served");
            var config = await GetJson("/api/admin/gift-card-config");
            Assert.Equal(imagePath, config.GetProperty("backgroundImagePath").GetString());
        }

        [Fact]
        public async Task UploadsServesOnlyThePublicFolder()
        {
            _host.WriteUpload("cleaners/documents/doc.webp");
            HttpStatusAssert.Is(HttpStatusCode.NotFound, await _host.GetAsync("/uploads/cleaners/documents/doc.webp"), "a private folder under /uploads");
            HttpStatusAssert.Is(HttpStatusCode.NotFound, await _host.GetAsync("/uploads/../cleaners/documents/doc.webp"), "climbing out of /uploads");
        }

        [Fact]
        public async Task TheAnonymousDebugEndpoint_IsGone()
        {
            HttpStatusAssert.Is(HttpStatusCode.NotFound, await _host.GetAsync("/api/admin/debug-gift-card-image"),
                "it printed server file paths to anyone");
        }

        private async Task<JsonElement> GetJson(string url)
        {
            var response = await _host.GetAsync(url);
            HttpStatusAssert.Is(HttpStatusCode.OK, response, url);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        private static byte[] PngBytes()
        {
            using var image = new Image<Rgba32>(8, 8);
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }
    }
}

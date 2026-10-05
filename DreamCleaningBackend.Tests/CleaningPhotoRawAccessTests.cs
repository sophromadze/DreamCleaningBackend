using System.Net;
using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// HOTFIX (2026-10): GET api/admin/user-care/cleaning-photos/{id}/raw used to be
    /// [AllowAnonymous]. The ids are sequential, so anyone could walk every customer's cleaning
    /// photos by counting. It now carries the same gate as the two list endpoints that hand those
    /// ids out (RequirePermission View under the controller's Admin/SuperAdmin/Moderator roles),
    /// and is exercised here over real HTTP with the cookie a same-origin &lt;img&gt; sends.
    /// </summary>
    public class CleaningPhotoRawAccessTests : IAsyncLifetime
    {
        private PrivateFileTestHost _host = null!;
        private const int PhotoId = 7;
        private const int TraversalPhotoId = 8;

        public async Task InitializeAsync()
        {
            _host = await PrivateFileTestHost.StartAsync(new[] { typeof(AdminUserCareController) });
            var stored = _host.WriteUpload("user-cleaning-photos/user-5-photo.webp");
            // A file outside the uploads root that a corrupted row points at.
            var outside = Path.Combine(Path.GetDirectoryName(_host.UploadRoot)!, "outside-" + Guid.NewGuid().ToString("N") + ".webp");
            File.WriteAllBytes(outside, PrivateFileTestHost.WebpBytes);
            _outsideFile = outside;

            await _host.SeedAsync(db =>
            {
                db.UserCleaningPhotos.Add(new UserCleaningPhoto { Id = PhotoId, UserId = 5, PhotoUrl = stored });
                db.UserCleaningPhotos.Add(new UserCleaningPhoto
                {
                    Id = TraversalPhotoId, UserId = 5, PhotoUrl = "/../" + Path.GetFileName(outside)
                });
            });
        }

        private string _outsideFile = "";

        public async Task DisposeAsync()
        {
            await _host.DisposeAsync();
            try { File.Delete(_outsideFile); } catch { }
        }

        private static string Url(int id) => $"/api/admin/user-care/cleaning-photos/{id}/raw";

        [Fact]
        public async Task Anonymous_IsRefused()
        {
            var response = await _host.GetAsync(Url(PhotoId));
            HttpStatusAssert.Is(HttpStatusCode.Unauthorized, response, "an anonymous request must not get a customer's photo");
        }

        [Theory]
        [InlineData(UserRole.Customer)]
        [InlineData(UserRole.Cleaner)]
        public async Task NonStaffAccounts_AreRefused(UserRole role)
        {
            var response = await _host.GetAsync(Url(PhotoId), userId: 5, role: role);
            Assert.False(response.IsSuccessStatusCode, $"{role} must not reach the admin photo stream");
        }

        [Theory]
        [InlineData(UserRole.Moderator)]
        [InlineData(UserRole.Admin)]
        [InlineData(UserRole.SuperAdmin)]
        public async Task EveryRoleThatCanOpenThePhotoLists_StillSeesThePhoto(UserRole role)
        {
            // Same audience as users/{id}/cleaning-photos and orders/{id}/cleaning-photos (View).
            var response = await _host.GetAsync(Url(PhotoId), userId: 1, role: role);

            HttpStatusAssert.Is(HttpStatusCode.OK, response, $"{role} opens the user/order panel and must see the photo");
            Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(PrivateFileTestHost.WebpBytes, await response.Content.ReadAsByteArrayAsync());
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
            Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        }

        [Fact]
        public async Task UnknownId_Is404()
        {
            var response = await _host.GetAsync(Url(99999), userId: 1, role: UserRole.Admin);
            HttpStatusAssert.Is(HttpStatusCode.NotFound, response, "a missing photo id");
        }

        [Fact]
        public async Task AStoredPathThatEscapesTheUploadsRoot_Is404_NotTheFile()
        {
            var response = await _host.GetAsync(Url(TraversalPhotoId), userId: 1, role: UserRole.SuperAdmin);
            HttpStatusAssert.Is(HttpStatusCode.NotFound, response, "a row pointing outside the uploads root");
        }
    }
}

using System.ComponentModel.DataAnnotations;
using System.Reflection;
using DreamCleaningBackend.DTOs;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// POST /api/auth/refresh-token MUST BE REACHABLE UNDER COOKIE AUTH (2026-09).
    ///
    /// Production runs cookie auth, where both tokens live in httpOnly cookies and the browser
    /// therefore posts an empty `{}` body. Two independent faults made that call fail 100% of the
    /// time, and between them no signed-in session on the live site could ever be renewed:
    ///
    ///   1. RefreshTokenDto carried [Required] on both strings. With [ApiController], automatic
    ///      model validation rejected the empty body with a 400 (a ValidationProblemDetails, which
    ///      carries no `message` for the frontend to read) BEFORE the action ran.
    ///   2. Even past that, the cookie branch built `new RefreshTokenDto { RefreshToken = … }` and
    ///      left Token null — but the access token is what identifies the user, so
    ///      AuthService.RefreshToken threw on it and the controller turned that into another 400.
    ///
    /// Paired with the frontend's overflowing 29-day refresh timer (see
    /// token-refresh.service.spec.ts), this is what produced the endless stream of failed
    /// refresh-token POSTs in the browser console.
    /// </summary>
    public class SessionRefreshTests
    {
        /// <summary>
        /// The cookie-auth body is `{}`, so neither property may be [Required]: the controller
        /// fills them from the cookies and validates what it actually needs itself.
        /// </summary>
        [Fact]
        public void RefreshTokenDto_BindsAnEmptyBody_SoTheCookieAuthCallReachesTheAction()
        {
            foreach (var name in new[] { nameof(RefreshTokenDto.Token), nameof(RefreshTokenDto.RefreshToken) })
            {
                var property = typeof(RefreshTokenDto).GetProperty(name);
                Assert.NotNull(property);

                var required = property!.GetCustomAttribute<RequiredAttribute>();
                Assert.True(
                    required == null,
                    $"RefreshTokenDto.{name} is [Required], so [ApiController] answers the empty " +
                    "cookie-auth body with an automatic 400 and the action never runs.");
            }
        }

        /// <summary>
        /// The access token names the user; without it there is nothing to renew. The cookie
        /// branch must read it from the cookie rather than leaving the DTO's Token null.
        /// </summary>
        [Fact]
        public void CookieAuthRefresh_SendsTheAccessTokenFromTheCookie_NotJustTheRefreshToken()
        {
            var source = ReadBackendFile(Path.Combine("Controllers", "AuthController.cs"));
            var action = ExtractRefreshTokenAction(source);

            Assert.Contains("Request.Cookies[\"refresh_token\"]", action);
            Assert.True(
                action.Contains("Request.Cookies[\"access_token\"]"),
                "The cookie-auth refresh branch never reads the access_token cookie, so the DTO's " +
                "Token stays null and AuthService.RefreshToken cannot identify the session.");

            var tokenAssignment = action.IndexOf("Token = accessToken", StringComparison.Ordinal);
            Assert.True(
                tokenAssignment >= 0,
                "The access token read from the cookie is not passed through as RefreshTokenDto.Token.");
        }

        /// <summary>
        /// A session that cannot be renewed is an AUTH failure, not a malformed request. The 400
        /// this used to answer with is what sent the last investigation after the signing secret
        /// instead of the cookies.
        /// </summary>
        [Fact]
        public void AFailedRefresh_Answers401_NotAMalformedRequest()
        {
            var action = ExtractRefreshTokenAction(
                ReadBackendFile(Path.Combine("Controllers", "AuthController.cs")));

            Assert.DoesNotContain("BadRequest", action);
            Assert.Contains("Unauthorized", action);
        }

        /// <summary>
        /// The DTO's properties are optional now, so the service is the backstop: a missing token
        /// must surface as a stated reason, never as a null dereference inside the JWT handler.
        /// </summary>
        [Fact]
        public void AuthService_RejectsAMissingTokenWithAReason_RatherThanDereferencingNull()
        {
            var source = ReadBackendFile(Path.Combine("Services", "AuthService.cs"));
            var start = source.IndexOf(
                "public async Task<AuthResponseDto> RefreshToken(RefreshTokenDto refreshTokenDto)",
                StringComparison.Ordinal);
            Assert.True(start >= 0, "AuthService.RefreshToken was not found.");

            var guardEnd = source.IndexOf("GetPrincipalFromExpiredToken", start, StringComparison.Ordinal);
            Assert.True(guardEnd > start, "RefreshToken no longer resolves the principal.");

            var guards = source.Substring(start, guardEnd - start);
            Assert.Contains("IsNullOrEmpty(suppliedAccessToken)", guards);
            Assert.Contains("IsNullOrEmpty(suppliedRefreshToken)", guards);
        }

        /// <summary>Pulls just the refresh-token action out of the controller source.</summary>
        private static string ExtractRefreshTokenAction(string source)
        {
            var start = source.IndexOf("[HttpPost(\"refresh-token\")]", StringComparison.Ordinal);
            Assert.True(start >= 0, "The refresh-token action was not found in AuthController.");

            var end = source.IndexOf("[HttpPost(\"logout\")]", start, StringComparison.Ordinal);
            Assert.True(end > start, "Could not find the end of the refresh-token action.");

            return source.Substring(start, end - start);
        }

        private static string ReadBackendFile(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningNG")))
                dir = dir.Parent;
            Assert.NotNull(dir);

            var path = Path.Combine(dir!.FullName, "DreamCleaningBackend", "DreamCleaningBackend", relativePath);
            Assert.True(File.Exists(path), $"{path} was not found.");
            return File.ReadAllText(path);
        }
    }
}

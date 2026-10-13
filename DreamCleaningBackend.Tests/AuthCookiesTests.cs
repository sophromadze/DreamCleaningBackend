using DreamCleaningBackend.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// 2026-10: the session cookies are written AND removed with one set of attributes
    /// (Helpers/AuthCookies). Logout used to delete them with none - no Secure, no SameSite.
    /// </summary>
    public class AuthCookiesTests
    {
        private static IConfiguration Config(bool useHttp = false) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Development:UseHttp"] = useHttp ? "true" : "false" })
            .Build();

        private static string[] SetCookies(Action<HttpResponse> write)
        {
            var context = new DefaultHttpContext();
            write(context.Response);
            return context.Response.Headers.SetCookie.Select(v => v ?? "").ToArray();
        }

        [Fact]
        public void Both_cookies_are_HttpOnly_Secure_Strict_on_path_root_for_30_days()
        {
            var headers = SetCookies(r => AuthCookies.Append(r, Config(), "tok", "ref"));

            Assert.Equal(2, headers.Length);
            Assert.StartsWith("access_token=tok;", headers[0]);
            Assert.StartsWith("refresh_token=ref;", headers[1]);
            foreach (var h in headers.Select(h => h.ToLowerInvariant()))
            {
                Assert.Contains("path=/", h);
                Assert.Contains("secure", h);
                Assert.Contains("samesite=strict", h);
                Assert.Contains("httponly", h);
                Assert.Contains("expires=", h);
            }
        }

        [Fact]
        public void Logout_deletes_with_the_same_attributes()
        {
            var headers = SetCookies(r => AuthCookies.Delete(r, Config())).Select(h => h.ToLowerInvariant()).ToArray();

            Assert.Equal(2, headers.Length);
            Assert.StartsWith("access_token=;", headers[0]);
            Assert.StartsWith("refresh_token=;", headers[1]);
            foreach (var h in headers)
            {
                Assert.Contains("expires=thu, 01 jan 1970", h);
                Assert.Contains("path=/", h);
                Assert.Contains("secure", h);
                Assert.Contains("samesite=strict", h);
                Assert.Contains("httponly", h);
            }
        }

        [Fact]
        public void Local_http_development_drops_Secure_only()
        {
            var headers = SetCookies(r => AuthCookies.Append(r, Config(useHttp: true), "tok", "ref")).Select(h => h.ToLowerInvariant());

            foreach (var h in headers)
            {
                Assert.DoesNotContain("secure", h);
                Assert.Contains("httponly", h);
                Assert.Contains("samesite=strict", h);
            }
        }
    }
}

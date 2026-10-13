namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// The session cookies (access_token / refresh_token) under cookie auth, written and removed with
    /// ONE set of attributes (2026-10).
    ///
    /// HttpOnly (the browser never reads them), Secure (off only when <c>Development:UseHttp</c>, for
    /// local http), SameSite=Strict, Path=/, 30 days to match the refresh token. Login, refresh and
    /// the guest auto-registration each used to build their own copy, and logout deleted with no
    /// attributes at all - a removal must name the same Path, and carrying Secure/SameSite keeps the
    /// deletion acceptable to browsers that hold a Secure cookie to stricter rules.
    /// </summary>
    public static class AuthCookies
    {
        public const string AccessToken = "access_token";
        public const string RefreshToken = "refresh_token";
        public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

        public static CookieOptions Options(IConfiguration configuration) => new()
        {
            HttpOnly = true,
            Secure = !configuration.GetValue<bool>("Development:UseHttp", false),
            SameSite = SameSiteMode.Strict,
            Path = "/"
        };

        public static void Append(HttpResponse response, IConfiguration configuration, string token, string refreshToken)
        {
            var options = Options(configuration);
            options.Expires = DateTime.UtcNow.Add(Lifetime);
            response.Cookies.Append(AccessToken, token, options);
            response.Cookies.Append(RefreshToken, refreshToken, options);
        }

        public static void Delete(HttpResponse response, IConfiguration configuration)
        {
            var options = Options(configuration);
            response.Cookies.Delete(AccessToken, options);
            response.Cookies.Delete(RefreshToken, options);
        }
    }
}

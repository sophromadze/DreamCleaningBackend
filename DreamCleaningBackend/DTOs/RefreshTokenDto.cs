namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// Body of POST /api/auth/refresh-token.
    ///
    /// BOTH PROPERTIES ARE DELIBERATELY OPTIONAL, and must stay that way (2026-09).
    ///
    /// Under cookie auth the tokens are in httpOnly cookies, so the browser cannot put them in a
    /// body and posts `{}`. `[Required]` here meant `[ApiController]`'s automatic model validation
    /// rejected that empty body with a 400 (a ValidationProblemDetails, which carries no `message`
    /// property) BEFORE the action ran — so the cookie-auth refresh endpoint could never succeed,
    /// and the frontend's "Invalid refresh token" recovery branch never matched the response it
    /// got. The controller reads the cookies itself and validates what it actually needs.
    /// </summary>
    public class RefreshTokenDto
    {
        public string? Token { get; set; }

        public string? RefreshToken { get; set; }
    }
}

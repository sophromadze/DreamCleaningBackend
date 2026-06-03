using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Caching.Memory;

namespace DreamCleaningBackend.Controllers
{
    public class ResendVerificationDto
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly IAccountMergeService _accountMergeService;
        private readonly IConfiguration _configuration;
        private readonly bool _useCookieAuth;
        private readonly IMemoryCache _cache;
        private readonly ITwoFactorService _twoFactorService;
        private readonly ApplicationDbContext _dbContext;

        public AuthController(
            IAuthService authService,
            IAccountMergeService accountMergeService,
            IConfiguration configuration,
            IMemoryCache cache,
            ITwoFactorService twoFactorService,
            ApplicationDbContext dbContext)
        {
            _authService = authService;
            _accountMergeService = accountMergeService;
            _configuration = configuration;
            _useCookieAuth = configuration.GetValue<bool>("Authentication:UseCookieAuth", false);
            _cache = cache;
            _twoFactorService = twoFactorService;
            _dbContext = dbContext;
        }

        // Header name where the frontend sends the trusted-device token (when present).
        private const string DeviceTokenHeader = "X-Device-Token";

        // Pulls request metadata used by the 2FA service (UA + IP).
        private (string UserAgent, string IpAddress) GetClientContext()
        {
            var ua = Request.Headers.UserAgent.ToString();
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
            return (ua, ip);
        }

        private string? GetDeviceTokenFromRequest() =>
            Request.Headers.TryGetValue(DeviceTokenHeader, out var v) ? v.ToString() : null;

        // Wraps a normal AuthResponseDto with the 2FA gate. Three branches:
        //   1. Not staff, or trusted device → unchanged (JWT issued normally).
        //   2. Staff with NO PIN yet → unchanged (JWT issued). Frontend's pinSetupGuard
        //      will catch them and force PIN setup; /2fa/set-pin auto-trusts the device
        //      on completion. This avoids a chicken-and-egg "challenge a PIN that doesn't exist".
        //   3. Staff with PIN + untrusted device → hide JWT, return TwoFactor envelope.
        //      The client must clear /2fa/verify-email then /2fa/verify-pin to get tokens.
        private async Task<AuthResponseDto> ApplyTwoFactorGateAsync(AuthResponseDto response)
        {
            if (response.User == null || response.TwoFactor != null || response.RequiresEmailVerification)
                return response;

            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == response.User.Id);
            if (user == null || !_twoFactorService.RequiresTwoFactor(user))
                return response;

            var deviceToken = GetDeviceTokenFromRequest();
            if (await _twoFactorService.IsDeviceTrustedAsync(user.Id, deviceToken))
                return response;

            // No PIN yet → let them in with the JWT; the frontend guard forces setup next.
            if (string.IsNullOrEmpty(user.TwoFactorPinHash))
            {
                response.RequiresPinSetup = true;
                return response;
            }

            var ctx = GetClientContext();
            var challengeId = await _twoFactorService.CreateChallengeAsync(user.Id, ctx.UserAgent, ctx.IpAddress);

            return new AuthResponseDto
            {
                User = null,
                Token = null,
                RefreshToken = null,
                TwoFactor = new TwoFactorRequiredDto
                {
                    RequiresTwoFactor = true,
                    ChallengeId = challengeId,
                    HasPin = true,
                    MaskedEmail = MaskEmail(user.Email)
                }
            };
        }

        private static string MaskEmail(string email)
        {
            if (string.IsNullOrEmpty(email)) return "";
            var at = email.IndexOf('@');
            if (at <= 1) return email;
            var name = email[..at];
            var domain = email[at..];
            var visible = name.Length <= 2 ? name[..1] : name[..2];
            return $"{visible}{new string('•', Math.Max(2, name.Length - visible.Length))}{domain}";
        }

        [HttpPost("register")]
        public async Task<ActionResult<AuthResponseDto>> Register(RegisterDto registerDto)
        {
            try
            {
                var response = await _authService.Register(registerDto);
                
                if (_useCookieAuth && response.Token != null)
                {
                    // Auto-login the new account via cookies even when email verification is
                    // still pending. The verify-email-notice page needs an authenticated session
                    // (the pendingVerificationGuard requires a current user) so the user can enter
                    // their 6-digit code. Previously we only set cookies when verification was NOT
                    // required, which left cookie-auth (production) signups with no session and
                    // bounced them straight to /login. Don't leak tokens in the body for cookie auth.
                    SetAuthCookies(response.Token, response.RefreshToken);
                    return Ok(new { user = response.User, requiresEmailVerification = response.RequiresEmailVerification });
                }

                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("login")]
        public async Task<ActionResult<AuthResponseDto>> Login(LoginDto loginDto)
        {
            try
            {
                var response = await _authService.Login(loginDto);
                response = await ApplyTwoFactorGateAsync(response);

                // 2FA gate caught it — return the challenge envelope; no cookies set yet.
                if (response.TwoFactor != null)
                    return Ok(response);

                if (_useCookieAuth)
                {
                    SetAuthCookies(response.Token, response.RefreshToken);
                    // Don't send tokens in response for cookie auth
                    return Ok(new { user = response.User });
                }

                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Two-factor authentication (staff: Admin / SuperAdmin / Moderator)
        // ═══════════════════════════════════════════════════════════════════════

        // Step 1 of the challenge — verify the 6-digit code sent by email.
        [HttpPost("2fa/verify-email")]
        public async Task<ActionResult> VerifyTwoFactorEmail([FromBody] VerifyTwoFactorEmailDto dto)
        {
            try
            {
                await _twoFactorService.VerifyEmailCodeAsync(dto.ChallengeId, dto.Code);
                return Ok(new { verified = true });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Step 2 — verify PIN. On success returns the final auth tokens. When
        // rememberDevice = true the response also carries `deviceToken` for the client
        // to store as `tf_device_token` for future X-Device-Token headers.
        [HttpPost("2fa/verify-pin")]
        public async Task<ActionResult<AuthResponseDto>> VerifyTwoFactorPin([FromBody] VerifyTwoFactorPinDto dto)
        {
            try
            {
                var ctx = GetClientContext();
                var result = await _twoFactorService.VerifyPinAsync(
                    dto.ChallengeId, dto.Pin, dto.RememberDevice, ctx.UserAgent, ctx.IpAddress);

                var auth = await _authService.RefreshUserToken(result.UserId);
                auth.DeviceToken = result.DeviceToken;

                if (_useCookieAuth)
                {
                    SetAuthCookies(auth.Token, auth.RefreshToken);
                    return Ok(new { user = auth.User, deviceToken = auth.DeviceToken });
                }
                return Ok(auth);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Forced first-time PIN setup. Caller must already be authenticated with a normal
        // password JWT (so we know who they are) and must be a staff user. After successful
        // setup the device is trusted automatically (it's the device they just set up on).
        [HttpPost("2fa/set-pin")]
        [Authorize]
        public async Task<ActionResult<AuthResponseDto>> SetTwoFactorPin([FromBody] SetTwoFactorPinDto dto)
        {
            try
            {
                var userId = int.Parse(User.FindFirst("UserId")?.Value
                    ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                if (userId == 0) return Unauthorized();

                if (dto.Pin != dto.ConfirmPin)
                    return BadRequest(new { message = "PINs don't match." });

                var ctx = GetClientContext();
                var result = await _twoFactorService.SetPinAsync(userId, dto.Pin, true, ctx.UserAgent, ctx.IpAddress);
                return Ok(new
                {
                    success = true,
                    deviceToken = result?.DeviceToken
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("2fa/resend-email")]
        public async Task<ActionResult> ResendTwoFactorEmail([FromBody] ResendTwoFactorCodeDto dto)
        {
            try
            {
                await _twoFactorService.ResendEmailCodeAsync(dto.ChallengeId);
                return Ok(new { resent = true });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // List & revoke trusted devices for the current user (self-service).
        [HttpGet("2fa/trusted-devices")]
        [Authorize]
        public async Task<ActionResult<List<TrustedDeviceDto>>> ListTrustedDevices()
        {
            var userId = int.Parse(User.FindFirst("UserId")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            if (userId == 0) return Unauthorized();

            var devices = await _twoFactorService.ListTrustedDevicesAsync(userId);

            // Mark the device making this request, so the UI can warn before revoking it.
            var currentTokenHash = HashCurrentDeviceToken();
            if (!string.IsNullOrEmpty(currentTokenHash))
            {
                foreach (var d in devices)
                {
                    // We can't expose the hash here, so we rely on a join-by-id approach:
                    // re-query the row's hash via a lightweight lookup.
                }
                var match = await _dbContext.TrustedDevices
                    .Where(d => d.UserId == userId && d.TokenHash == currentTokenHash && d.RevokedAt == null)
                    .Select(d => d.Id)
                    .FirstOrDefaultAsync();
                if (match != 0)
                {
                    var dev = devices.FirstOrDefault(d => d.Id == match);
                    if (dev != null) dev.IsCurrentDevice = true;
                }
            }
            return Ok(devices);
        }

        [HttpDelete("2fa/trusted-devices/{id}")]
        [Authorize]
        public async Task<ActionResult> RevokeTrustedDevice(int id)
        {
            var userId = int.Parse(User.FindFirst("UserId")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            if (userId == 0) return Unauthorized();

            await _twoFactorService.RevokeTrustedDeviceAsync(userId, id);
            return NoContent();
        }

        private string? HashCurrentDeviceToken()
        {
            var raw = GetDeviceTokenFromRequest();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        [HttpPost("google-login")]
        public async Task<ActionResult<AuthResponseDto>> GoogleLogin(GoogleLoginDto googleLoginDto)
        {
            try
            {
                var response = await _authService.GoogleLogin(googleLoginDto);
                response = await ApplyTwoFactorGateAsync(response);

                if (response.TwoFactor != null)
                    return Ok(response);

                if (_useCookieAuth)
                {
                    SetAuthCookies(response.Token, response.RefreshToken);
                    // Don't send tokens in response for cookie auth
                    return Ok(new { user = response.User });
                }

                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Endpoint to receive Apple's POST request (form_post response mode)
        // This endpoint must accept form data from Apple and redirect to Angular
        [HttpPost("apple-callback")]
        [IgnoreAntiforgeryToken] // Apple's POST doesn't include CSRF token
        [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
        public async Task<IActionResult> AppleCallback([FromForm] AppleCallbackFormDto? formData)
        {
            try
            {
                var frontendUrl = _configuration["Frontend:Url"] ?? "http://localhost:4200";
                
                // Handle case where formData might be null or fields are empty
                // Apple sends form data with specific field names
                if (formData == null || string.IsNullOrEmpty(formData.IdToken))
                {
                    // Try to read form data manually from request
                    var form = await Request.ReadFormAsync();
                    formData = new AppleCallbackFormDto
                    {
                        Code = form["code"].FirstOrDefault() ?? string.Empty,
                        IdToken = form["id_token"].FirstOrDefault() ?? string.Empty,
                        State = form["state"].FirstOrDefault() ?? string.Empty,
                        User = form["user"].FirstOrDefault() ?? string.Empty,
                        Error = form["error"].FirstOrDefault() ?? string.Empty,
                        ErrorDescription = form["error_description"].FirstOrDefault() ?? string.Empty
                    };
                }
                
                // Check if we have an error from Apple
                if (!string.IsNullOrEmpty(formData.Error))
                {
                    var errorRedirect = $"{frontendUrl}/auth/apple-callback?error={Uri.EscapeDataString(formData.Error)}";
                    if (!string.IsNullOrEmpty(formData.ErrorDescription))
                    {
                        errorRedirect += $"&error_description={Uri.EscapeDataString(formData.ErrorDescription)}";
                    }
                    return Redirect(errorRedirect);
                }

                // Extract id_token and code from form data
                if (string.IsNullOrEmpty(formData.IdToken))
                {
                    return Redirect($"{frontendUrl}/auth/apple-callback?error=missing_token");
                }

                // Redirect to Angular callback page with the token data
                var redirectUrl = $"{frontendUrl}/auth/apple-callback?id_token={Uri.EscapeDataString(formData.IdToken)}";
                if (!string.IsNullOrEmpty(formData.Code))
                {
                    redirectUrl += $"&code={Uri.EscapeDataString(formData.Code)}";
                }
                if (!string.IsNullOrEmpty(formData.User))
                {
                    redirectUrl += $"&user={Uri.EscapeDataString(formData.User)}";
                }
                if (!string.IsNullOrEmpty(formData.State))
                {
                    redirectUrl += $"&state={Uri.EscapeDataString(formData.State)}";
                }

                return Redirect(redirectUrl);
            }
            catch (Exception ex)
            {
                var frontendUrl = _configuration["Frontend:Url"] ?? "http://localhost:4200";
                Console.WriteLine($"Apple callback error: {ex.Message}");
                return Redirect($"{frontendUrl}/auth/apple-callback?error=server_error&error_description={Uri.EscapeDataString(ex.Message)}");
            }
        }

        [HttpPost("apple-login")]
        public async Task<ActionResult<AuthResponseDto>> AppleLogin(AppleLoginDto appleLoginDto)
        {
            try
            {
                if (string.IsNullOrEmpty(appleLoginDto.IdentityToken))
                {
                    return BadRequest(new { message = "Identity token is required" });
                }

                var response = await _authService.AppleLogin(appleLoginDto);
                response = await ApplyTwoFactorGateAsync(response);

                if (response.TwoFactor != null)
                    return Ok(response);

                if (_useCookieAuth)
                {
                    SetAuthCookies(response.Token, response.RefreshToken);
                    return Ok(new { user = response.User, requiresRealEmail = response.RequiresRealEmail });
                }

                return Ok(response);
            }
            catch (Exception ex)
            {
                // Log the full exception for debugging
                Console.WriteLine($"Apple login error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                }
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("refresh-token")]
        public async Task<ActionResult<AuthResponseDto>> RefreshToken(RefreshTokenDto refreshTokenDto)
        {
            try
            {
                AuthResponseDto response;
                
                if (_useCookieAuth)
                {
                    // Get refresh token from cookie
                    var refreshToken = Request.Cookies["refresh_token"];
                    if (string.IsNullOrEmpty(refreshToken))
                    {
                        return Unauthorized(new { message = "No refresh token provided" });
                    }
                    
                    response = await _authService.RefreshToken(new RefreshTokenDto { RefreshToken = refreshToken });
                    SetAuthCookies(response.Token, response.RefreshToken);
                    
                    // Don't send tokens in response for cookie auth
                    return Ok(new { user = response.User });
                }
                else
                {
                    response = await _authService.RefreshToken(refreshTokenDto);
                    return Ok(response);
                }
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("logout")]
        [Authorize]
        public ActionResult Logout()
        {
            if (_useCookieAuth)
            {
                // Clear cookies
                Response.Cookies.Delete("access_token");
                Response.Cookies.Delete("refresh_token");
            }
            
            return Ok(new { message = "Logged out successfully" });
        }

        [HttpGet("current-user")]
        [Authorize]
        public async Task<ActionResult<UserDto>> GetCurrentUser()
        {
            var userIdClaim = User.FindFirst("UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized();
            }

            var user = await _authService.GetUserById(userId);
            if (user == null)
            {
                return NotFound();
            }

            return Ok(user);
        }

        [HttpGet("check-email/{email}")]
        public async Task<ActionResult<bool>> CheckEmail(string email)
        {
            var exists = await _authService.UserExists(email);
            return Ok(new { exists });
        }

        [HttpPost("change-password")]
        [Authorize]
        public async Task<ActionResult> ChangePassword(ChangePasswordDto changePasswordDto)
        {
            try
            {
                var userId = int.Parse(User.FindFirst("UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                if (userId == 0)
                    return Unauthorized(new { message = "User not found" });

                await _authService.ChangePassword(userId, changePasswordDto);

                // Security: a password change invalidates every trusted device for the user.
                // Forces re-authentication with 2FA on every device they were signed in on.
                await _twoFactorService.RevokeAllTrustedDevicesAsync(userId);

                return Ok(new { message = "Password changed successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("set-password")]
        [Authorize]
        public async Task<ActionResult> SetPassword(SetPasswordDto setPasswordDto)
        {
            try
            {
                var userId = int.Parse(User.FindFirst("UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
                if (userId == 0)
                    return Unauthorized(new { message = "User not found" });

                await _authService.SetPassword(userId, setPasswordDto);
                return Ok(new { message = "Password set successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("refresh-user-token")]
        [Authorize]
        public async Task<ActionResult<AuthResponseDto>> RefreshUserToken()
        {
            try
            {
                // Get user ID from JWT token
                var userIdClaim = User.FindFirst("UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!int.TryParse(userIdClaim, out int userId))
                {
                    return BadRequest(new { message = "Invalid user ID" });
                }

                var response = await _authService.RefreshUserToken(userId);
                
                if (_useCookieAuth)
                {
                    SetAuthCookies(response.Token, response.RefreshToken);
                    // Don't send tokens in response for cookie auth
                    return Ok(new { user = response.User });
                }
                
                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("verify-email")]
        public async Task<ActionResult> VerifyEmail(VerifyEmailDto verifyDto)
        {
            try
            {
                await _authService.VerifyEmail(verifyDto.Token);
                return Ok(new { message = "Email verified successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("resend-verification")]
        public async Task<ActionResult> ResendVerification(ResendVerificationDto dto)
        {
            try
            {
                await _authService.ResendVerificationEmail(dto.Email);
                // Always return same message for security
                return Ok(new { message = "If an account exists with this email, a verification email has been sent." });
            }
            catch (Exception ex)
            {
                // Don't reveal actual error
                return Ok(new { message = "If an account exists with this email, a verification email has been sent." });
            }
        }

        [HttpPost("check-email-status")]
        public async Task<ActionResult<CheckEmailStatusResponse>> CheckEmailStatus(CheckEmailStatusDto dto)
        {
            try
            {
                var (exists, hasPassword) = await _authService.CheckEmailStatusAsync(dto.Email);
                return Ok(new CheckEmailStatusResponse { Exists = exists, HasPassword = hasPassword });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("send-email-verification-otp")]
        public async Task<ActionResult> SendEmailVerificationOtp(SendLoginOtpDto dto)
        {
            try
            {
                await _authService.SendEmailVerificationOtpAsync(dto.Email);
                return Ok(new { message = "A 6-digit verification code has been sent to your email." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("send-login-otp")]
        public async Task<ActionResult> SendLoginOtp(SendLoginOtpDto dto)
        {
            try
            {
                await _authService.SendLoginOtpAsync(dto.Email);
                return Ok(new { message = "A 6-digit code has been sent to your email." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("verify-login-otp")]
        public async Task<ActionResult<AuthResponseDto>> VerifyLoginOtp(VerifyLoginOtpDto dto)
        {
            try
            {
                var response = await _authService.VerifyLoginOtpAsync(dto.Email, dto.Code);

                if (_useCookieAuth)
                {
                    SetAuthCookies(response.Token, response.RefreshToken);
                    return Ok(new { user = response.User, requiresPasswordSetup = response.RequiresPasswordSetup });
                }

                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("forgot-password")]
        public async Task<ActionResult> ForgotPassword(ForgotPasswordDto forgotDto)
        {
            try
            {
                await _authService.InitiatePasswordReset(forgotDto.Email);
                return Ok(new { message = "If an account exists, a reset link has been sent" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("reset-password-info")]
        public async Task<ActionResult<object>> GetResetPasswordInfo([FromQuery] string token)
        {
            var info = await _authService.GetResetPasswordInfoAsync(token);
            if (!info.HasValue || string.IsNullOrEmpty(info.Value.Email))
                return NotFound(new { message = "Invalid or expired link" });
            return Ok(new { email = info.Value.Email, isSetPassword = info.Value.IsSetPassword });
        }

        [HttpPost("reset-password")]
        public async Task<ActionResult> ResetPassword(ResetPasswordDto resetDto)
        {
            try
            {
                // Look up the user from the reset token *before* the reset clears it,
                // so we can revoke their trusted devices after the password change.
                var userId = await _dbContext.Users
                    .Where(u => u.PasswordResetToken == resetDto.Token
                                && u.PasswordResetTokenExpiry > DateTime.UtcNow)
                    .Select(u => u.Id)
                    .FirstOrDefaultAsync();

                await _authService.ResetPassword(resetDto);

                if (userId != 0)
                    await _twoFactorService.RevokeAllTrustedDevicesAsync(userId);

                return Ok(new { message = "Password reset successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("initiate-email-change")]
        [Authorize]
        public async Task<ActionResult<EmailChangeResponseDto>> InitiateEmailChange(InitiateEmailChangeDto dto)
        {
            try
            {
                // Get user ID from JWT token
                var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");

                if (userId == 0)
                    return Unauthorized(new { message = "User not found" });

                var response = await _authService.InitiateEmailChange(userId, dto);
                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("request-email-verification")]
        [Authorize]
        public async Task<ActionResult> RequestEmailVerification(RequestRealEmailVerificationDto dto)
        {
            try
            {
                var userIdClaim = User.FindFirst("UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!int.TryParse(userIdClaim, out int userId))
                    return Unauthorized();
                await _authService.RequestRealEmailVerification(userId, dto.Email);
                return Ok(new { message = "Verification code sent to your email." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("verify-email-code")]
        [Authorize]
        public async Task<ActionResult> VerifyEmailCode(VerifyRealEmailCodeDto dto)
        {
            try
            {
                var userIdClaim = User.FindFirst("UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!int.TryParse(userIdClaim, out int userId))
                    return Unauthorized();
                var result = await _authService.VerifyRealEmailCode(userId, dto.Email, dto.Code);
                if (result.IsMergeScenario)
                {
                    return Ok(result.AccountExistsResponse);
                }
                var response = result.AuthResponse!;
                if (_useCookieAuth)
                {
                    SetAuthCookies(response.Token, response.RefreshToken);
                    return Ok(new { user = response.User });
                }
                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("confirm-account-merge")]
        [Authorize]
        public async Task<ActionResult<MergeResultDto>> ConfirmAccountMerge(ConfirmAccountMergeDto dto)
        {
            try
            {
                var userIdClaim = User.FindFirst("UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!int.TryParse(userIdClaim, out int userId))
                    return Unauthorized();
                var result = await _accountMergeService.ConfirmAndMergeAsync(userId, dto.VerificationMethod, dto.VerificationToken);
                if (_useCookieAuth)
                {
                    SetAuthCookies(result.NewToken, result.RefreshToken ?? "");
                    return Ok(new { status = result.Status, message = result.Message, mergedData = result.MergedData, user = result.User });
                }
                return Ok(result);
            }
            catch (Exception ex)
            {
                var msg = ex.InnerException?.Message ?? ex.Message;
                if (msg.Contains("entity changes") || msg.Contains("inner exception"))
                    msg = "Merge failed. Please try again or use Merge with Email.";
                return BadRequest(new { message = msg });
            }
        }

        [HttpPost("resend-merge-code")]
        [Authorize]
        public async Task<ActionResult> ResendMergeCode()
        {
            try
            {
                var userIdClaim = User.FindFirst("UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!int.TryParse(userIdClaim, out int userId))
                    return Unauthorized();
                await _accountMergeService.ResendMergeCodeAsync(userId);
                return Ok(new { message = "Merge confirmation code sent." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("confirm-email-change")]
        public async Task<ActionResult> ConfirmEmailChange(ConfirmEmailChangeDto dto)
        {
            try
            {
                await _authService.ConfirmEmailChange(dto.Token);
                return Ok(new { message = "Email changed successfully! You can now use your new email address to log in." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        private void SetAuthCookies(string token, string refreshToken)
        {
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = !_configuration.GetValue<bool>("Development:UseHttp", false), // Use HTTPS in production
                SameSite = SameSiteMode.Strict,
                Expires = DateTime.UtcNow.AddDays(7) // Changed from 2 hours to 7 days to match refresh token
            };

            Response.Cookies.Append("access_token", token, cookieOptions);

            var refreshCookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = !_configuration.GetValue<bool>("Development:UseHttp", false),
                SameSite = SameSiteMode.Strict,
                Expires = DateTime.UtcNow.AddDays(7)
            };

            Response.Cookies.Append("refresh_token", refreshToken, refreshCookieOptions);
        }
    }
}
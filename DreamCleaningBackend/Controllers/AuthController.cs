using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

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
        private readonly IConfiguration _configuration;
        private readonly bool _useCookieAuth;

        public AuthController(IAuthService authService, IConfiguration configuration)
        {
            _authService = authService;
            _configuration = configuration;
            _useCookieAuth = configuration.GetValue<bool>("Authentication:UseCookieAuth", false);
        }

        [HttpPost("register")]
        public async Task<ActionResult<AuthResponseDto>> Register(RegisterDto registerDto)
        {
            try
            {
                var response = await _authService.Register(registerDto);
                
                if (_useCookieAuth && response.Token != null && !response.RequiresEmailVerification)
                {
                    SetAuthCookies(response.Token, response.RefreshToken);
                    // Don't send tokens in response for cookie auth
                    return Ok(new { user = response.User, requiresEmailVerification = false });
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

        [HttpPost("google-login")]
        public async Task<ActionResult<AuthResponseDto>> GoogleLogin(GoogleLoginDto googleLoginDto)
        {
            try
            {
                var response = await _authService.GoogleLogin(googleLoginDto);
                
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
                // Get user ID from JWT token
                var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");

                if (userId == 0)
                    return Unauthorized(new { message = "User not found" });

                await _authService.ChangePassword(userId, changePasswordDto);
                return Ok(new { message = "Password changed successfully" });
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

        [HttpPost("reset-password")]
        public async Task<ActionResult> ResetPassword(ResetPasswordDto resetDto)
        {
            try
            {
                await _authService.ResetPassword(resetDto);
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
                Expires = DateTime.UtcNow.AddHours(2)
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
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

        public AuthController(IAuthService authService)
        {
            _authService = authService;
        }

        [HttpPost("register")]
        public async Task<ActionResult<AuthResponseDto>> Register(RegisterDto registerDto)
        {
            try
            {
                var response = await _authService.Register(registerDto);
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
                var response = await _authService.RefreshToken(refreshTokenDto);
                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
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
    }
}
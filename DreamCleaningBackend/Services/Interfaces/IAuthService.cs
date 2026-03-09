using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IAuthService
    {
        Task<AuthResponseDto> Register(RegisterDto registerDto);
        Task<AuthResponseDto> Login(LoginDto loginDto);
        Task<bool> UserExists(string email);
        // Add these for OAuth and refresh token support
        Task<AuthResponseDto> GoogleLogin(GoogleLoginDto googleLoginDto);
        Task<AuthResponseDto> RefreshToken(RefreshTokenDto refreshTokenDto);
        Task<AuthResponseDto> RefreshUserToken(int userId);
        Task<bool> ChangePassword(int userId, ChangePasswordDto changePasswordDto);
        Task<bool> SetPassword(int userId, SetPasswordDto setPasswordDto);
        Task<bool> VerifyEmail(string token);
        Task<bool> ResendVerificationEmail(string email);
        Task<bool> InitiatePasswordReset(string email);
        /// <summary>Returns the email and isSetPassword for a valid token, or null if invalid or expired. IsSetPassword is true when user has no password (e.g. admin-created).</summary>
        Task<(string? Email, bool IsSetPassword)?> GetResetPasswordInfoAsync(string token);
        /// <summary>Generates a set-password token for a user (e.g. admin-created). Token valid 7 days. Returns the token.</summary>
        Task<string> CreateSetPasswordTokenAsync(int userId);
        Task<bool> ResetPassword(ResetPasswordDto resetDto);
        Task<EmailChangeResponseDto> InitiateEmailChange(int userId, InitiateEmailChangeDto dto);
        Task<bool> ConfirmEmailChange(string token);
        Task<UserDto> GetUserById(int userId);
        Task<AuthResponseDto> AppleLogin(AppleLoginDto appleLoginDto);
        Task RequestRealEmailVerification(int userId, string email);
        Task<VerifyRealEmailResultDto> VerifyRealEmailCode(int userId, string email, string code);
        /// <summary>Validates Google id_token and returns the email address, or null if invalid.</summary>
        Task<string?> GetEmailFromGoogleTokenAsync(string idToken);
    }
}

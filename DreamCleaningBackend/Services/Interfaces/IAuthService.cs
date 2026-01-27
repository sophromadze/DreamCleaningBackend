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
        Task<bool> VerifyEmail(string token);
        Task<bool> ResendVerificationEmail(string email);
        Task<bool> InitiatePasswordReset(string email);
        Task<bool> ResetPassword(ResetPasswordDto resetDto);
        Task<EmailChangeResponseDto> InitiateEmailChange(int userId, InitiateEmailChangeDto dto);
        Task<bool> ConfirmEmailChange(string token);
        Task<UserDto> GetUserById(int userId);
        Task<AuthResponseDto> AppleLogin(AppleLoginDto appleLoginDto);
    }
}

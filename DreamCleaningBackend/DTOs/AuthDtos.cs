using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace DreamCleaningBackend.DTOs
{
    public class AuthResponseDto
    {
        public UserDto User { get; set; }
        public string Token { get; set; }
        public string RefreshToken { get; set; }
        public bool RequiresEmailVerification { get; set; }
        /// <summary>True when user has Apple relay email and must verify a real email before using the platform.</summary>
        public bool RequiresRealEmail { get; set; }
        public string Message { get; set; }
    }

    public class RequestRealEmailVerificationDto
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; }
    }

    public class VerifyRealEmailCodeDto
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; }
        [Required]
        [StringLength(6, MinimumLength = 6)]
        public string Code { get; set; }
    }

    /// <summary>Returned when verify-email-code finds that the email already belongs to another account (merge flow).</summary>
    public class AccountExistsResponseDto
    {
        public string Status { get; set; } = "ACCOUNT_EXISTS";
        public string Message { get; set; } = "An account with this email already exists";
        public string ExistingAccountId { get; set; } = null!;
        public string ExistingAccountEmail { get; set; } = null!;
        public string ExistingAccountName { get; set; } = null!;
    }

    public class ConfirmAccountMergeDto
    {
        public string VerificationMethod { get; set; } = null!; // "email" (6-digit code only)
        public string VerificationToken { get; set; } = null!; // 6-digit code or Google id_token
    }

    public class MergeResultDto
    {
        public string Status { get; set; } = "MERGED";
        public string Message { get; set; } = "Accounts merged successfully";
        public MergeDataDto MergedData { get; set; } = null!;
        public string NewToken { get; set; } = null!;
        public string? RefreshToken { get; set; }
        public UserDto? User { get; set; }
    }

    public class MergeDataDto
    {
        public int OrdersTransferred { get; set; }
        public int AddressesTransferred { get; set; }
        public bool SubscriptionTransferred { get; set; }
    }

    /// <summary>Result of VerifyRealEmailCode — either verified (auth response) or existing account (merge flow).</summary>
    public class VerifyRealEmailResultDto
    {
        public bool IsMergeScenario { get; set; }
        public AuthResponseDto? AuthResponse { get; set; }
        public AccountExistsResponseDto? AccountExistsResponse { get; set; }
    }

    public class VerifyEmailDto
    {
        public string Token { get; set; }
    }

    public class ForgotPasswordDto
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; }
    }

    public class ResetPasswordDto
    {
        [Required]
        public string Token { get; set; }

        [Required]
        [MinLength(8)]
        public string NewPassword { get; set; }
    }
}

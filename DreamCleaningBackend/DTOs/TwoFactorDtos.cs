using System;
using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    // Returned by /api/auth/login when a staff user needs to clear a 2FA challenge.
    // The client then drives /verify-email and /verify-pin against `challengeId`.
    public class TwoFactorRequiredDto
    {
        public bool RequiresTwoFactor { get; set; } = true;
        public Guid ChallengeId { get; set; }
        public bool HasPin { get; set; }      // false = user has never set a PIN
        public string MaskedEmail { get; set; } = string.Empty;
    }

    public class VerifyTwoFactorEmailDto
    {
        [Required]
        public Guid ChallengeId { get; set; }

        [Required]
        [StringLength(10)]
        public string Code { get; set; } = string.Empty;
    }

    public class VerifyTwoFactorPinDto
    {
        [Required]
        public Guid ChallengeId { get; set; }

        [Required]
        [StringLength(12, MinimumLength = 4)]
        public string Pin { get; set; } = string.Empty;

        public bool RememberDevice { get; set; } = true;
    }

    public class SetTwoFactorPinDto
    {
        [Required]
        [StringLength(12, MinimumLength = 4)]
        public string Pin { get; set; } = string.Empty;

        [Required]
        [StringLength(12, MinimumLength = 4)]
        public string ConfirmPin { get; set; } = string.Empty;
    }

    public class ResendTwoFactorCodeDto
    {
        [Required]
        public Guid ChallengeId { get; set; }
    }

    public class TrustedDeviceDto
    {
        public int Id { get; set; }
        public string? DeviceName { get; set; }
        public string? Browser { get; set; }
        public string? OperatingSystem { get; set; }
        public string? IpAddress { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUsedAt { get; set; }
        // True when this row matches the device making the current request, so the UI
        // can mark it as "This device" and warn before revoking.
        public bool IsCurrentDevice { get; set; }
    }
}

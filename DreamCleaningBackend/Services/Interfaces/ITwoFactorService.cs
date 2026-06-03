using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface ITwoFactorService
    {
        // True when this user role must use 2FA — Admin / SuperAdmin / Moderator.
        bool RequiresTwoFactor(User user);

        // Validates a device token (raw) against the TrustedDevices table for this user.
        // Returns true if the device is currently trusted (row exists, not revoked).
        // Updates LastUsedAt on success.
        Task<bool> IsDeviceTrustedAsync(int userId, string? rawDeviceToken);

        // Creates a new 2FA challenge for this user. Sends a 6-digit code via email and
        // stores the session. Returns the session id (returned to client as challengeId).
        Task<Guid> CreateChallengeAsync(int userId, string userAgent, string ipAddress);

        // Resends the email code on an existing challenge (capped).
        Task ResendEmailCodeAsync(Guid challengeId);

        // Verifies the email code; on success marks the session's EmailVerifiedAt.
        // Throws InvalidOperationException with a human-readable message on failure.
        Task VerifyEmailCodeAsync(Guid challengeId, string code);

        // Step 2: verify PIN. Requires EmailVerifiedAt to be set on the session.
        // On success the session is deleted and a TrustedDevice row is created (when
        // rememberDevice = true). Returns the raw device token to give back to the client
        // (only place it ever leaves the server).
        Task<TwoFactorVerificationResult> VerifyPinAsync(
            Guid challengeId,
            string pin,
            bool rememberDevice,
            string userAgent,
            string ipAddress);

        // First-time PIN setup. Used both by the forced setup flow (after first password
        // login) and by SuperAdmin reset. Returns the raw device token issued for the
        // current device when issueTrustedDevice is true.
        Task<TwoFactorVerificationResult?> SetPinAsync(
            int userId,
            string pin,
            bool issueTrustedDevice,
            string userAgent,
            string ipAddress);

        // Lists this user's currently-active trusted devices.
        Task<List<TrustedDeviceDto>> ListTrustedDevicesAsync(int userId);

        // Revokes one trusted device (no-op if it doesn't belong to this user).
        Task RevokeTrustedDeviceAsync(int userId, int trustedDeviceId);

        // Revokes ALL of a user's trusted devices. Called on password change/reset and
        // when SuperAdmin resets a staff PIN.
        Task RevokeAllTrustedDevicesAsync(int userId);
    }

    public class TwoFactorVerificationResult
    {
        // Final JWT-ready: caller should issue the access token + refresh token.
        public int UserId { get; set; }

        // Set only when a new trusted device was created. Null means the client requested
        // "don't remember" or the call was a PIN setup without remembering.
        public string? DeviceToken { get; set; }
    }
}

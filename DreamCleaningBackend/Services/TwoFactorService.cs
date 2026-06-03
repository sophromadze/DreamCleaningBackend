using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace DreamCleaningBackend.Services
{
    public class TwoFactorService : ITwoFactorService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly ILogger<TwoFactorService> _logger;

        // ─── Tuning constants ─────────────────────────────────────────────────
        // Email code: 6 digits, expires after this many minutes.
        private const int EmailCodeTtlMinutes = 10;
        // Hard cap on wrong code entries; after this the challenge is destroyed.
        private const int MaxEmailCodeAttempts = 5;
        // Hard cap on /resend hits per challenge — protects SMTP quota.
        private const int MaxEmailCodeResends = 3;
        // PIN: hard cap on wrong PIN attempts before lockout.
        private const int MaxPinAttempts = 5;
        // Lockout duration after MaxPinAttempts is reached.
        private const int PinLockoutMinutes = 15;
        // Full challenge lifespan from creation (even if user does nothing).
        private const int ChallengeLifetimeMinutes = 15;

        public TwoFactorService(
            ApplicationDbContext context,
            IEmailService emailService,
            ILogger<TwoFactorService> logger)
        {
            _context = context;
            _emailService = emailService;
            _logger = logger;
        }

        public bool RequiresTwoFactor(User user) =>
            user.Role == UserRole.Admin
            || user.Role == UserRole.SuperAdmin
            || user.Role == UserRole.Moderator;

        // ──────────────────────────────────────────────────────────────────────
        //  Trusted device verification (called by login flow)
        // ──────────────────────────────────────────────────────────────────────

        public async Task<bool> IsDeviceTrustedAsync(int userId, string? rawDeviceToken)
        {
            if (string.IsNullOrWhiteSpace(rawDeviceToken)) return false;
            var hash = HashToken(rawDeviceToken);

            var device = await _context.TrustedDevices
                .FirstOrDefaultAsync(d =>
                    d.UserId == userId
                    && d.TokenHash == hash
                    && d.RevokedAt == null);

            if (device == null) return false;

            device.LastUsedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return true;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Challenge lifecycle
        // ──────────────────────────────────────────────────────────────────────

        public async Task<Guid> CreateChallengeAsync(int userId, string userAgent, string ipAddress)
        {
            // Reap any stale sessions for this user — keeps the table tidy and prevents
            // a stale challengeId from accidentally being reused by the client.
            var stale = await _context.TwoFactorSessions
                .Where(s => s.UserId == userId && s.ExpiresAt < DateTime.UtcNow)
                .ToListAsync();
            if (stale.Count > 0) _context.TwoFactorSessions.RemoveRange(stale);

            var (code, hash) = GenerateEmailCode();
            var session = new TwoFactorSession
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EmailCodeHash = hash,
                CodeSentAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(ChallengeLifetimeMinutes),
                CreatedAt = DateTime.UtcNow
            };
            _context.TwoFactorSessions.Add(session);
            await _context.SaveChangesAsync();

            await SendCodeEmailAsync(userId, code);
            return session.Id;
        }

        public async Task ResendEmailCodeAsync(Guid challengeId)
        {
            var session = await LoadActiveSessionAsync(challengeId);
            if (session.EmailCodeResends >= MaxEmailCodeResends)
                throw new InvalidOperationException("Too many resends. Restart the login.");

            var (code, hash) = GenerateEmailCode();
            session.EmailCodeHash = hash;
            session.CodeSentAt = DateTime.UtcNow;
            session.EmailCodeResends++;
            // Each resend also resets the attempts counter so a user who mistyped a stale
            // code isn't punished for the typo on the freshly-sent one.
            session.EmailCodeAttempts = 0;
            await _context.SaveChangesAsync();

            await SendCodeEmailAsync(session.UserId, code);
        }

        public async Task VerifyEmailCodeAsync(Guid challengeId, string code)
        {
            var session = await LoadActiveSessionAsync(challengeId);

            if (session.EmailCodeAttempts >= MaxEmailCodeAttempts)
            {
                _context.TwoFactorSessions.Remove(session);
                await _context.SaveChangesAsync();
                throw new InvalidOperationException("Too many wrong codes. Please restart the login.");
            }

            var enteredHash = HashToken((code ?? string.Empty).Trim());
            if (enteredHash != session.EmailCodeHash)
            {
                session.EmailCodeAttempts++;
                await _context.SaveChangesAsync();
                throw new InvalidOperationException("Incorrect code.");
            }

            session.EmailVerifiedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        public async Task<TwoFactorVerificationResult> VerifyPinAsync(
            Guid challengeId,
            string pin,
            bool rememberDevice,
            string userAgent,
            string ipAddress)
        {
            var session = await LoadActiveSessionAsync(challengeId);
            if (session.EmailVerifiedAt == null)
                throw new InvalidOperationException("Verify the email code first.");

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == session.UserId)
                ?? throw new InvalidOperationException("User not found.");
            if (string.IsNullOrEmpty(user.TwoFactorPinHash))
                throw new InvalidOperationException("This account has no PIN set.");

            EnsureNotPinLocked(user);

            if (!VerifyPin(pin, user.TwoFactorPinHash))
            {
                user.TwoFactorPinFailedAttempts++;
                if (user.TwoFactorPinFailedAttempts >= MaxPinAttempts)
                    user.TwoFactorPinLockedUntil = DateTime.UtcNow.AddMinutes(PinLockoutMinutes);
                await _context.SaveChangesAsync();
                throw new InvalidOperationException("Incorrect PIN.");
            }

            // Success — reset counters, kill the challenge, issue device token if asked.
            user.TwoFactorPinFailedAttempts = 0;
            user.TwoFactorPinLockedUntil = null;

            string? deviceToken = null;
            if (rememberDevice)
                deviceToken = await CreateTrustedDeviceAsync(user.Id, userAgent, ipAddress);

            _context.TwoFactorSessions.Remove(session);
            await _context.SaveChangesAsync();

            return new TwoFactorVerificationResult
            {
                UserId = user.Id,
                DeviceToken = deviceToken
            };
        }

        public async Task<TwoFactorVerificationResult?> SetPinAsync(
            int userId,
            string pin,
            bool issueTrustedDevice,
            string userAgent,
            string ipAddress)
        {
            ValidatePinShape(pin);

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new InvalidOperationException("User not found.");

            if (!RequiresTwoFactor(user))
                throw new InvalidOperationException("This account does not use 2FA.");

            user.TwoFactorPinHash = HashPin(pin);
            user.TwoFactorPinSetAt = DateTime.UtcNow;
            user.TwoFactorPinFailedAttempts = 0;
            user.TwoFactorPinLockedUntil = null;
            await _context.SaveChangesAsync();

            if (!issueTrustedDevice)
                return new TwoFactorVerificationResult { UserId = user.Id, DeviceToken = null };

            var token = await CreateTrustedDeviceAsync(user.Id, userAgent, ipAddress);
            return new TwoFactorVerificationResult { UserId = user.Id, DeviceToken = token };
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Trusted device management
        // ──────────────────────────────────────────────────────────────────────

        public async Task<List<TrustedDeviceDto>> ListTrustedDevicesAsync(int userId)
        {
            var rows = await _context.TrustedDevices
                .Where(d => d.UserId == userId && d.RevokedAt == null)
                .OrderByDescending(d => d.LastUsedAt)
                .ToListAsync();

            return rows.Select(d => new TrustedDeviceDto
            {
                Id = d.Id,
                DeviceName = d.DeviceName,
                Browser = d.Browser,
                OperatingSystem = d.OperatingSystem,
                IpAddress = d.IpAddress,
                CreatedAt = d.CreatedAt,
                LastUsedAt = d.LastUsedAt,
                IsCurrentDevice = false
            }).ToList();
        }

        public async Task RevokeTrustedDeviceAsync(int userId, int trustedDeviceId)
        {
            var row = await _context.TrustedDevices
                .FirstOrDefaultAsync(d => d.Id == trustedDeviceId && d.UserId == userId);
            if (row == null) return;
            row.RevokedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        public async Task RevokeAllTrustedDevicesAsync(int userId)
        {
            var rows = await _context.TrustedDevices
                .Where(d => d.UserId == userId && d.RevokedAt == null)
                .ToListAsync();
            if (rows.Count == 0) return;
            var now = DateTime.UtcNow;
            foreach (var r in rows) r.RevokedAt = now;
            await _context.SaveChangesAsync();
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Private helpers
        // ──────────────────────────────────────────────────────────────────────

        private async Task<TwoFactorSession> LoadActiveSessionAsync(Guid id)
        {
            var session = await _context.TwoFactorSessions.FirstOrDefaultAsync(s => s.Id == id);
            if (session == null) throw new InvalidOperationException("Challenge not found.");
            if (session.ExpiresAt < DateTime.UtcNow)
            {
                _context.TwoFactorSessions.Remove(session);
                await _context.SaveChangesAsync();
                throw new InvalidOperationException("Challenge expired. Please log in again.");
            }
            return session;
        }

        private async Task<string> CreateTrustedDeviceAsync(int userId, string userAgent, string ipAddress)
        {
            // 32 random bytes → 256 bits of entropy in the raw token. Plenty.
            var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var (browser, os) = ParseUserAgent(userAgent);

            _context.TrustedDevices.Add(new TrustedDevice
            {
                UserId = userId,
                TokenHash = HashToken(rawToken),
                DeviceName = ComposeDeviceName(browser, os),
                Browser = browser,
                OperatingSystem = os,
                IpAddress = ipAddress,
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
            return rawToken;
        }

        private static void EnsureNotPinLocked(User user)
        {
            if (user.TwoFactorPinLockedUntil.HasValue
                && user.TwoFactorPinLockedUntil.Value > DateTime.UtcNow)
            {
                var minutesLeft = (int)Math.Ceiling((user.TwoFactorPinLockedUntil.Value - DateTime.UtcNow).TotalMinutes);
                throw new InvalidOperationException(
                    $"PIN entry locked. Try again in {Math.Max(1, minutesLeft)} minute(s).");
            }
        }

        private async Task SendCodeEmailAsync(int userId, string code)
        {
            var user = await _context.Users
                .Where(u => u.Id == userId)
                .Select(u => new { u.Email, u.FirstName })
                .FirstOrDefaultAsync();
            if (user == null) return;

            var subject = "Your sign-in code";
            // Plain template — readable across email clients without depending on CSS.
            var html = $@"
                <div style=""font-family: Arial, sans-serif; max-width: 520px;"">
                  <h2 style=""color:#111827;"">Sign-in verification</h2>
                  <p>Hi {System.Web.HttpUtility.HtmlEncode(user.FirstName ?? "")},</p>
                  <p>Use this code to finish signing in. It expires in <strong>{EmailCodeTtlMinutes} minutes</strong>.</p>
                  <p style=""font-size:32px;font-weight:bold;letter-spacing:6px;color:#2563eb;
                            padding:12px 18px;background:#eff6ff;border-radius:8px;display:inline-block;"">
                    {code}
                  </p>
                  <p style=""color:#6b7280;font-size:13px;"">If you didn't try to sign in, ignore this email and change your password.</p>
                </div>";
            try
            {
                await _emailService.SendEmailAsync(user.Email, subject, html);
            }
            catch (Exception ex)
            {
                // Don't leak SMTP issues to the client; log + let the user re-request.
                _logger.LogError(ex, "Failed sending 2FA email to user {UserId}", userId);
            }
        }

        // 6-digit numeric code. Random.Shared is fine for non-cryptographic uniqueness
        // here because the value is hashed before storage and rate-limited on entry.
        private static (string Code, string Hash) GenerateEmailCode()
        {
            var n = RandomNumberGenerator.GetInt32(0, 1_000_000);
            var code = n.ToString("D6");
            return (code, HashToken(code));
        }

        private static string HashToken(string raw)
        {
            // SHA-256 hex. Used for both the email code and the device token. Both are
            // high-entropy short-lived secrets — no key-stretching needed (bcrypt is
            // reserved for the PIN, which is user-chosen and low-entropy).
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // PIN hashing: HMAC-SHA512 with a per-user random salt, base64-encoded as
        // "salt$hash". Matches the existing password-hashing style in AuthService.
        private static string HashPin(string pin)
        {
            using var hmac = new HMACSHA512();
            var salt = hmac.Key;
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(pin));
            return $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        private static bool VerifyPin(string pin, string stored)
        {
            var parts = stored.Split('$', 2);
            if (parts.Length != 2) return false;
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            using var hmac = new HMACSHA512(salt);
            var actual = hmac.ComputeHash(Encoding.UTF8.GetBytes(pin));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        private static void ValidatePinShape(string pin)
        {
            if (string.IsNullOrWhiteSpace(pin)) throw new InvalidOperationException("PIN is required.");
            if (pin.Length < 4 || pin.Length > 12)
                throw new InvalidOperationException("PIN must be 4–12 digits.");
            foreach (var c in pin)
                if (c < '0' || c > '9')
                    throw new InvalidOperationException("PIN must contain digits only.");
        }

        // Coarse-grained UA parsing — good enough for "Chrome on macOS" labels.
        // Avoid pulling a UA parser dependency for the single place we use it.
        private static (string? Browser, string? Os) ParseUserAgent(string? ua)
        {
            if (string.IsNullOrWhiteSpace(ua)) return (null, null);
            string? browser = ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? "Edge"
                : ua.Contains("OPR/", StringComparison.OrdinalIgnoreCase) ? "Opera"
                : ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? "Firefox"
                : ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? "Chrome"
                : ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? "Safari"
                : "Browser";

            string? os = ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
                : ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iOS (iPhone)"
                : ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iOS (iPad)"
                : ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
                : ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) ? "macOS"
                : ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
                : "Unknown OS";

            return (browser, os);
        }

        private static string? ComposeDeviceName(string? browser, string? os)
        {
            if (browser == null && os == null) return null;
            if (browser != null && os != null) return $"{browser} on {os}";
            return browser ?? os;
        }
    }
}

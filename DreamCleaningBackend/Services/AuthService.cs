using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using Google.Apis.Auth;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Repositories.Interfaces;

namespace DreamCleaningBackend.Services
{
    public class AuthService : IAuthService
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IEmailService _emailService;
        private readonly ILogger<AuthService> _logger;
        private readonly ISpecialOfferService _specialOfferService;
        private readonly IAuditService _auditService;
        private readonly IUserRepository _userRepository;

        public AuthService(ApplicationDbContext context, IConfiguration configuration, IEmailService emailService, ILogger<AuthService> logger, ISpecialOfferService specialOfferService, IAuditService auditService, IUserRepository userRepository)
        {
            _context = context;
            _configuration = configuration;
            _emailService = emailService;
            _logger = logger;
            _specialOfferService = specialOfferService;
            _auditService = auditService;
            _userRepository = userRepository;
        }

        // Update your AuthService.cs Register method with better error handling

        public async Task<AuthResponseDto> Register(RegisterDto registerDto)
        {
            try
            {
                if (await UserExists(registerDto.Email))
                    throw new Exception("User already exists");

                // Validate password requirements
                if (!PasswordValidator.IsValidPassword(registerDto.Password, out string passwordError))
                    throw new Exception(passwordError);

                CreatePasswordHash(registerDto.Password, out string passwordHash, out string passwordSalt);

                var user = new User
                {
                    FirstName = registerDto.FirstName,
                    LastName = registerDto.LastName,
                    Email = registerDto.Email.ToLower(),
                    Phone = registerDto.Phone,
                    PasswordHash = passwordHash,
                    PasswordSalt = passwordSalt,
                    AuthProvider = "Local",
                    CreatedAt = DateTime.UtcNow,
                    FirstTimeOrder = true,
                    IsEmailVerified = false,
                    EmailVerificationToken = GenerateVerificationToken(),
                    EmailVerificationTokenExpiry = DateTime.UtcNow.AddHours(24)
                };

                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                try
                {
                    await _specialOfferService.GrantAllActiveOffersToNewUser(user.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to grant first-time offer to user {UserId}", user.Id);
                    // Don't fail registration if special offer fails
                }

                try
                {
                    // Send verification email
                    var verificationLink = $"{_configuration["Frontend:Url"]}/auth/verify-email?token={user.EmailVerificationToken}";
                    await _emailService.SendEmailVerificationAsync(user.Email, user.FirstName, verificationLink);
                }
                catch (Exception emailEx)
                {
                    _logger.LogError(emailEx, "Failed to send verification email to {Email}", user.Email);

                    // Delete the user if email sending fails
                    _context.Users.Remove(user);
                    await _context.SaveChangesAsync();

                    throw new Exception("Failed to send verification email. Please check your email address and try again.");
                }

                // Return response WITHOUT token - user must verify email first
                return new AuthResponseDto
                {
                    User = MapUserToDto(user),
                    Token = null, // No token until email is verified
                    RefreshToken = null, // No refresh token until email is verified
                    RequiresEmailVerification = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Registration failed for email: {Email}", registerDto.Email);
                throw;
            }
        }

        public async Task<AuthResponseDto> Login(LoginDto loginDto)
        {
            var user = await _context.Users
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Email == loginDto.Email.ToLower());

            if (user == null || user.AuthProvider != "Local")
                throw new Exception("Invalid email or password");

            if (!user.IsActive)
                throw new Exception("Account is deactivated");

            if (!VerifyPasswordHash(loginDto.Password, user.PasswordHash, user.PasswordSalt))
                throw new Exception("Invalid email or password");

            // Check if email is verified
            if (!user.IsEmailVerified)
            {
                throw new Exception("Please verify your email before logging in. Check your inbox for the verification link.");
            }

            // Update refresh token
            user.RefreshToken = GenerateRefreshToken();
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(30);

            // Update the UpdatedAt field instead of LastLogin (which doesn't exist)
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return new AuthResponseDto
            {
                User = MapUserToDto(user),
                Token = CreateToken(user),
                RefreshToken = user.RefreshToken
            };
        }

        public async Task<AuthResponseDto> GoogleLogin(GoogleLoginDto googleLoginDto)
        {
            try
            {
                // Validate Google token
                var payload = await ValidateGoogleToken(googleLoginDto.IdToken);

                if (payload == null)
                    throw new Exception("Invalid Google token");

                var email = payload.Email;
                var googleId = payload.Subject;
                var firstName = payload.GivenName ?? "User";
                var lastName = payload.FamilyName ?? "";
                var profilePicture = payload.Picture; // Add this line

                // Check if user exists
                var user = await _context.Users
                    .Include(u => u.Subscription)
                    .FirstOrDefaultAsync(u => u.Email == email.ToLower());

                if (user == null)
                {
                    // Create new user
                    user = new User
                    {
                        Email = email.ToLower(),
                        FirstName = firstName,
                        LastName = lastName,
                        AuthProvider = "Google",
                        ExternalAuthId = googleId,
                        ProfilePictureUrl = profilePicture, // Add this line
                        CreatedAt = DateTime.UtcNow,
                        FirstTimeOrder = true,
                        IsActive = true,
                        IsEmailVerified = true
                    };

                    _context.Users.Add(user);
                }
                else if (user.AuthProvider != "Google")
                {
                    // Keep original AuthProvider (Local) but add Google support
                    user.ExternalAuthId = googleId;
                    user.IsEmailVerified = true;

                    // Update profile picture from Google
                    user.ProfilePictureUrl = profilePicture; // Add this line

                    // Optionally update names if they were empty or different
                    if (string.IsNullOrEmpty(user.FirstName) || user.FirstName == "User")
                        user.FirstName = firstName;
                    if (string.IsNullOrEmpty(user.LastName))
                        user.LastName = lastName;

                    user.UpdatedAt = DateTime.UtcNow;

                    _logger.LogInformation("Linked Google to existing local account {Email}", email);
                }
                else
                {
                    // Existing Google user - update profile picture in case it changed
                    user.ProfilePictureUrl = profilePicture; // Add this line
                    user.UpdatedAt = DateTime.UtcNow;
                }


                // Update refresh token
                user.RefreshToken = GenerateRefreshToken();
                user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(30);
                await _context.SaveChangesAsync();

                try
                {
                    await _specialOfferService.GrantAllActiveOffersToNewUser(user.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to grant first-time offer to user {UserId}", user.Id);
                    // Don't fail registration if special offer fails
                }

                return new AuthResponseDto
                {
                    User = MapUserToDto(user),
                    Token = CreateToken(user),
                    RefreshToken = user.RefreshToken
                };
            }
            catch (Exception ex)
            {
                throw new Exception($"Google login failed: {ex.Message}");
            }
        }

        public async Task<AuthResponseDto> RefreshToken(RefreshTokenDto refreshTokenDto)
        {
            try
            {
                _logger.LogInformation("Starting token refresh process");
                
                var principal = GetPrincipalFromExpiredToken(refreshTokenDto.Token);
                // PRESERVED: Try both claim types for user ID
                var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                             principal.FindFirst("UserId")?.Value;

                if (string.IsNullOrEmpty(userId))
                {
                    _logger.LogWarning("Invalid token - no user ID found");
                    throw new Exception("Invalid token");
                }

                var user = await _context.Users
                    .Include(u => u.Subscription)
                    .FirstOrDefaultAsync(u => u.Id == int.Parse(userId));

                if (user == null)
                {
                    _logger.LogWarning($"User not found for ID: {userId}");
                    throw new Exception("User not found");
                }

                // NEW: Check if user is still active
                if (!user.IsActive)
                {
                    _logger.LogWarning($"User account is blocked for ID: {userId}");
                    throw new Exception("User account is blocked");
                }

                // PRESERVED: Validate refresh token
                if (user.RefreshToken != refreshTokenDto.RefreshToken)
                {
                    _logger.LogWarning($"Invalid refresh token for user ID: {userId}");
                    _logger.LogWarning($"Expected refresh token: {user.RefreshToken}");
                    _logger.LogWarning($"Received refresh token: {refreshTokenDto.RefreshToken}");
                    _logger.LogWarning($"Token lengths - Expected: {user.RefreshToken?.Length}, Received: {refreshTokenDto.RefreshToken?.Length}");
                    throw new Exception("Invalid refresh token");
                }

                // PRESERVED: Check if refresh token is expired
                if (user.RefreshTokenExpiryTime <= DateTime.UtcNow)
                {
                    _logger.LogWarning($"Refresh token expired for user ID: {userId}");
                    throw new Exception("Refresh token expired");
                }

                // Generate new tokens
                var newAccessToken = CreateToken(user);
                var newRefreshToken = GenerateRefreshToken();

                // Update user's refresh token
                user.RefreshToken = newRefreshToken;
                user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(30); // Extended for better UX
                user.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                _logger.LogInformation($"Token refresh successful for user ID: {userId}");

                return new AuthResponseDto
                {
                    User = MapUserToDto(user),
                    Token = newAccessToken,
                    RefreshToken = newRefreshToken
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Token refresh failed");
                throw;
            }
        }

        public async Task<AuthResponseDto> RefreshUserToken(int userId)
        {
            // Get fresh user data from database
            var user = await _context.Users
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
                throw new Exception("User not found");

            if (!user.IsActive)
                throw new Exception("User account is blocked");

            // Generate new token with fresh role
            var token = CreateToken(user);
            var refreshToken = GenerateRefreshToken();

            // Update refresh token in database
            user.RefreshToken = refreshToken;
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(30);

            await _context.SaveChangesAsync();

            return new AuthResponseDto
            {
                Token = token,
                RefreshToken = refreshToken,
                User = new UserDto
                {
                    Id = user.Id,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Email = user.Email,
                    Phone = user.Phone,
                    Role = user.Role.ToString(),
                    FirstTimeOrder = user.FirstTimeOrder
                }
            };
        }

        public async Task<bool> UserExists(string email)
        {
            return await _context.Users.AnyAsync(u => u.Email == email.ToLower());
        }

        private async Task<GoogleJsonWebSignature.Payload> ValidateGoogleToken(string idToken)
        {
            try
            {
                var settings = new GoogleJsonWebSignature.ValidationSettings()
                {
                    Audience = new List<string>() { _configuration["Authentication:Google:ClientId"] }
                };

                var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
                return payload;
            }
            catch
            {
                return null;
            }
        }

        private ClaimsPrincipal GetPrincipalFromExpiredToken(string token)
        {
            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = false,
                ValidateIssuer = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["AppSettings:Token"])),
                ValidateLifetime = false // Don't validate lifetime for expired tokens
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out SecurityToken securityToken);
            var jwtSecurityToken = securityToken as JwtSecurityToken;

            if (jwtSecurityToken == null || !jwtSecurityToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha512, StringComparison.InvariantCultureIgnoreCase))
                throw new SecurityTokenException("Invalid token");

            return principal;
        }

        private void CreatePasswordHash(string password, out string passwordHash, out string passwordSalt)
        {
            using (var hmac = new HMACSHA512())
            {
                var salt = hmac.Key;
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(password));
                passwordSalt = Convert.ToBase64String(salt);
                passwordHash = Convert.ToBase64String(hash);
            }
        }

        private bool VerifyPasswordHash(string password, string storedHash, string storedSalt)
        {
            var salt = Convert.FromBase64String(storedSalt);
            using (var hmac = new HMACSHA512(salt))
            {
                var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(password));
                var hash = Convert.FromBase64String(storedHash);
                return computedHash.SequenceEqual(hash);
            }
        }

        private string CreateToken(User user)
        {
            var claims = new List<Claim>
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Email, user.Email),
        new Claim(ClaimTypes.Name, $"{user.FirstName} {user.LastName}"),
        // IMPORTANT: Add both ClaimTypes.Role and custom "Role" claim (PRESERVED FROM ORIGINAL)
        new Claim(ClaimTypes.Role, user.Role.ToString()),
        new Claim("Role", user.Role.ToString()),
        new Claim("UserId", user.Id.ToString()),
        new Claim("FirstName", user.FirstName),
        new Claim("LastName", user.LastName),
        new Claim("FirstTimeOrder", user.FirstTimeOrder.ToString()),
        new Claim("AuthProvider", user.AuthProvider ?? "Local")
    };

            if (user.SubscriptionId.HasValue)
            {
                claims.Add(new Claim("SubscriptionId", user.SubscriptionId.Value.ToString()));
            }

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                _configuration.GetSection("AppSettings:Token").Value));

            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha512Signature);

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddDays(7), // Extended to 7 days to match cookie expiration
                SigningCredentials = creds
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var token = tokenHandler.CreateToken(tokenDescriptor);

            return tokenHandler.WriteToken(token);
        }

        private string GenerateRefreshToken()
        {
            var randomNumber = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomNumber);
                return Convert.ToBase64String(randomNumber);
            }
        }

        public async Task<bool> ChangePassword(int userId, ChangePasswordDto changePasswordDto)
        {
            var user = await _context.Users.FindAsync(userId);

            if (user == null)
                throw new Exception("User not found");

            // Only allow password change for local accounts
            if (user.AuthProvider != "Local")
                throw new Exception("Password change is not allowed for OAuth accounts");

            // Verify current password
            if (!VerifyPasswordHash(changePasswordDto.CurrentPassword, user.PasswordHash, user.PasswordSalt))
                throw new Exception("Current password is incorrect");

            // Validate new password requirements
            if (!PasswordValidator.IsValidPassword(changePasswordDto.NewPassword, out string passwordError))
                throw new Exception(passwordError);

            // Create new password hash
            CreatePasswordHash(changePasswordDto.NewPassword, out string passwordHash, out string passwordSalt);

            // Update user password
            user.PasswordHash = passwordHash;
            user.PasswordSalt = passwordSalt;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return true;
        }

        private UserDto MapUserToDto(User user)
        {
            return new UserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                Phone = user.Phone,
                FirstTimeOrder = user.FirstTimeOrder,
                SubscriptionId = user.SubscriptionId,
                AuthProvider = user.AuthProvider,
                Role = user.Role.ToString(),
                ProfilePictureUrl = user.ProfilePictureUrl
            };
        }

        public async Task<bool> VerifyEmail(string token)
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.EmailVerificationToken == token
                    && u.EmailVerificationTokenExpiry > DateTime.UtcNow);

            if (user == null)
                throw new Exception("Invalid or expired verification token");

            user.IsEmailVerified = true;
            user.EmailVerificationToken = null;
            user.EmailVerificationTokenExpiry = null;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Send welcome email
            await _emailService.SendWelcomeEmailAsync(user.Email, user.FirstName);

            return true;
        }

        public async Task<bool> ResendVerificationEmail(string email)
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Email == email.ToLower()
                    && !u.IsEmailVerified
                    && u.AuthProvider == "Local");

            if (user == null)
                return false; // Don't throw exception - for security

            // Generate new verification token
            user.EmailVerificationToken = GenerateVerificationToken();
            user.EmailVerificationTokenExpiry = DateTime.UtcNow.AddHours(24);
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            var verificationLink = $"{_configuration["Frontend:Url"]}/auth/verify-email?token={user.EmailVerificationToken}";

            // SEND EMAIL IN BACKGROUND
            _ = Task.Run(async () =>
            {
                try
                {
                    await _emailService.SendEmailVerificationAsync(user.Email, user.FirstName, verificationLink);
                    _logger.LogInformation($"Verification email sent successfully to {user.Email}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send verification email to {user.Email}");
                }
            });

            return true;
        }

        public async Task<bool> InitiatePasswordReset(string email)
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Email == email.ToLower());

            if (user == null || user.AuthProvider != "Local")
                return true; // Don't reveal if user exists

            user.PasswordResetToken = GenerateVerificationToken();
            user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            var resetLink = $"{_configuration["Frontend:Url"]}/auth/reset-password?token={user.PasswordResetToken}";

            // SEND EMAIL IN BACKGROUND - This makes the response instant!
            _ = Task.Run(async () =>
            {
                try
                {
                    await _emailService.SendPasswordResetAsync(user.Email, user.FirstName, resetLink);
                    _logger.LogInformation($"Password reset email sent successfully to {user.Email}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send password reset email to {user.Email}");
                }
            });

            return true;
        }

        public async Task<bool> ResetPassword(ResetPasswordDto resetDto)
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.PasswordResetToken == resetDto.Token
                    && u.PasswordResetTokenExpiry > DateTime.UtcNow);

            if (user == null)
                throw new Exception("Invalid or expired reset token");

            // Validate new password requirements
            if (!PasswordValidator.IsValidPassword(resetDto.NewPassword, out string passwordError))
                throw new Exception(passwordError);

            CreatePasswordHash(resetDto.NewPassword, out string passwordHash, out string passwordSalt);

            user.PasswordHash = passwordHash;
            user.PasswordSalt = passwordSalt;
            user.PasswordResetToken = null;
            user.PasswordResetTokenExpiry = null;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return true;
        }

        private string GenerateVerificationToken()
        {
            var randomNumber = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomNumber);
                return Convert.ToBase64String(randomNumber)
                    .Replace("+", "")
                    .Replace("/", "")
                    .Replace("=", "");
            }
        }

        public async Task<EmailChangeResponseDto> InitiateEmailChange(int userId, InitiateEmailChangeDto dto)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);

            if (user == null)
                throw new Exception("User not found");

            if (user.AuthProvider != "Local")
                throw new Exception("Email change is only available for local accounts");

            // Verify current password
            if (!VerifyPasswordHash(dto.CurrentPassword, user.PasswordHash, user.PasswordSalt))
                throw new Exception("Current password is incorrect");

            // Check if new email already exists
            var emailExists = await _context.Users
                .AnyAsync(u => u.Email == dto.NewEmail.ToLower() && u.Id != userId);

            if (emailExists)
                throw new Exception("This email address is already in use");

            // Check if it's the same as current email
            if (user.Email.ToLower() == dto.NewEmail.ToLower())
                throw new Exception("New email must be different from current email");

            // Generate email change token
            user.PendingEmail = dto.NewEmail.ToLower();
            user.EmailChangeToken = GenerateVerificationToken();
            user.EmailChangeTokenExpiry = DateTime.UtcNow.AddHours(1); // 1 hour expiry
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Send verification email to NEW email address
            // Updated to use the same /change-email route with token parameter
            var verificationLink = $"{_configuration["Frontend:Url"]}/change-email?token={user.EmailChangeToken}";

            try
            {
                await _emailService.SendEmailChangeVerificationAsync(dto.NewEmail, user.FirstName, verificationLink, user.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email change verification to {Email}", dto.NewEmail);

                // Clear the pending change if email fails
                user.PendingEmail = null;
                user.EmailChangeToken = null;
                user.EmailChangeTokenExpiry = null;
                await _context.SaveChangesAsync();

                throw new Exception("Failed to send verification email. Please check the email address and try again.");
            }

            return new EmailChangeResponseDto
            {
                Message = "A verification email has been sent to your new email address. Please check your inbox and click the verification link to complete the email change.",
                RequiresVerification = true
            };
        }

        public async Task<bool> ConfirmEmailChange(string token)
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.EmailChangeToken == token
                    && u.EmailChangeTokenExpiry > DateTime.UtcNow
                    && !string.IsNullOrEmpty(u.PendingEmail));

            if (user == null)
                throw new Exception("Invalid or expired email change token");

            // Check if the pending email is still available
            var emailExists = await _context.Users
                .AnyAsync(u => u.Email == user.PendingEmail && u.Id != user.Id);

            if (emailExists)
                throw new Exception("This email address is no longer available");

            // ADDED: Capture user state before email change for auditing
            var userBeforeUpdate = new User
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email, // This is the OLD email
                Phone = user.Phone,
                Role = user.Role,
                IsActive = user.IsActive,
                FirstTimeOrder = user.FirstTimeOrder,
                SubscriptionId = user.SubscriptionId,
                CreatedAt = user.CreatedAt,
                UpdatedAt = user.UpdatedAt
            };

            // Update the email
            user.Email = user.PendingEmail;
            user.PendingEmail = null;
            user.EmailChangeToken = null;
            user.EmailChangeTokenExpiry = null;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // ADDED: Log the email change in audit history
            try
            {
                await _auditService.LogUpdateAsync(userBeforeUpdate, user);
            }
            catch (Exception ex)
            {
                // Don't fail the email change if audit logging fails
                Console.WriteLine($"Audit logging failed for email change: {ex.Message}");
            }

            // Send confirmation email to the new email address
            try
            {
                await _emailService.SendEmailChangeConfirmationAsync(user.Email, user.FirstName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email change confirmation to {Email}", user.Email);
                // Don't fail the email change if confirmation email fails
            }

            return true;
        }

        public async Task<UserDto> GetUserById(int userId)
        {
            var user = await _userRepository.GetByIdAsync(userId);
            
            if (user == null)
            {
                throw new Exception("User not found");
            }

            // Map User entity to UserDto
            return new UserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                Phone = user.Phone,
                FirstTimeOrder = user.FirstTimeOrder,
                SubscriptionId = user.SubscriptionId,
                AuthProvider = user.AuthProvider,
                Role = user.Role.ToString(),
                ProfilePictureUrl = user.ProfilePictureUrl
            };
        }
    }
}
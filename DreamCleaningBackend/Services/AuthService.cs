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

namespace DreamCleaningBackend.Services
{
    public class AuthService : IAuthService
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IEmailService _emailService;
        private readonly ILogger<AuthService> _logger;
        private readonly ISpecialOfferService _specialOfferService;

        public AuthService(ApplicationDbContext context, IConfiguration configuration, IEmailService emailService, ILogger<AuthService> logger, ISpecialOfferService specialOfferService)
        {
            _context = context;
            _configuration = configuration;
            _emailService = emailService;
            _logger = logger;
            _specialOfferService = specialOfferService;
        }

        // Update your AuthService.cs Register method with better error handling

        public async Task<AuthResponseDto> Register(RegisterDto registerDto)
        {
            try
            {
                if (await UserExists(registerDto.Email))
                    throw new Exception("User already exists");

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
                    CreatedAt = DateTime.Now,
                    FirstTimeOrder = true,
                    IsEmailVerified = false,
                    EmailVerificationToken = GenerateVerificationToken(),
                    EmailVerificationTokenExpiry = DateTime.Now.AddHours(24)
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
            user.RefreshTokenExpiryTime = DateTime.Now.AddDays(30);

            // Update the UpdatedAt field instead of LastLogin (which doesn't exist)
            user.UpdatedAt = DateTime.Now;

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
                        CreatedAt = DateTime.Now,
                        FirstTimeOrder = true,
                        IsActive = true
                    };

                    _context.Users.Add(user);
                }
                else if (user.AuthProvider != "Google")
                {
                    throw new Exception("Email already registered with different provider");
                }

                // Update refresh token
                user.RefreshToken = GenerateRefreshToken();
                user.RefreshTokenExpiryTime = DateTime.Now.AddDays(30);
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
                var principal = GetPrincipalFromExpiredToken(refreshTokenDto.Token);
                // PRESERVED: Try both claim types for user ID
                var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                             principal.FindFirst("UserId")?.Value;

                if (string.IsNullOrEmpty(userId))
                    throw new Exception("Invalid token");

                var user = await _context.Users
                    .Include(u => u.Subscription)
                    .FirstOrDefaultAsync(u => u.Id == int.Parse(userId));

                if (user == null)
                    throw new Exception("User not found");

                // NEW: Check if user is still active
                if (!user.IsActive)
                    throw new Exception("User account is blocked");

                // PRESERVED: Validate refresh token
                if (user.RefreshToken != refreshTokenDto.RefreshToken)
                    throw new Exception("Invalid refresh token");

                // PRESERVED: Check if refresh token is expired
                if (user.RefreshTokenExpiryTime <= DateTime.Now)
                    throw new Exception("Refresh token expired");

                // Generate new tokens
                var newAccessToken = CreateToken(user);
                var newRefreshToken = GenerateRefreshToken();

                // Update user's refresh token
                user.RefreshToken = newRefreshToken;
                user.RefreshTokenExpiryTime = DateTime.Now.AddDays(30); // Extended for better UX
                user.UpdatedAt = DateTime.Now;

                await _context.SaveChangesAsync();

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
            user.RefreshTokenExpiryTime = DateTime.Now.AddDays(30);

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
                Expires = DateTime.Now.AddHours(2), // Extended from 1 hour to 2 hours for better UX
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

            // Create new password hash
            CreatePasswordHash(changePasswordDto.NewPassword, out string passwordHash, out string passwordSalt);

            // Update user password
            user.PasswordHash = passwordHash;
            user.PasswordSalt = passwordSalt;
            user.UpdatedAt = DateTime.Now;

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
                Role = user.Role.ToString()
            };
        }

        public async Task<bool> VerifyEmail(string token)
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.EmailVerificationToken == token
                    && u.EmailVerificationTokenExpiry > DateTime.Now);

            if (user == null)
                throw new Exception("Invalid or expired verification token");

            user.IsEmailVerified = true;
            user.EmailVerificationToken = null;
            user.EmailVerificationTokenExpiry = null;
            user.UpdatedAt = DateTime.Now;

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
            user.EmailVerificationTokenExpiry = DateTime.Now.AddHours(24);
            user.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            // Send verification email
            var verificationLink = $"{_configuration["Frontend:Url"]}/auth/verify-email?token={user.EmailVerificationToken}";
            await _emailService.SendEmailVerificationAsync(user.Email, user.FirstName, verificationLink);

            return true;
        }

        public async Task<bool> InitiatePasswordReset(string email)
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Email == email.ToLower());

            if (user == null || user.AuthProvider != "Local")
                return true; // Don't reveal if user exists

            user.PasswordResetToken = GenerateVerificationToken();
            user.PasswordResetTokenExpiry = DateTime.Now.AddHours(1);
            user.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            var resetLink = $"{_configuration["Frontend:Url"]}/auth/reset-password?token={user.PasswordResetToken}";
            await _emailService.SendPasswordResetAsync(user.Email, user.FirstName, resetLink);

            return true;
        }

        public async Task<bool> ResetPassword(ResetPasswordDto resetDto)
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.PasswordResetToken == resetDto.Token
                    && u.PasswordResetTokenExpiry > DateTime.Now);

            if (user == null)
                throw new Exception("Invalid or expired reset token");

            CreatePasswordHash(resetDto.NewPassword, out string passwordHash, out string passwordSalt);

            user.PasswordHash = passwordHash;
            user.PasswordSalt = passwordSalt;
            user.PasswordResetToken = null;
            user.PasswordResetTokenExpiry = null;
            user.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            return true;
        }

        public async Task<AuthResponseDto> FacebookLogin(FacebookLoginDto facebookLoginDto)
        {
            try
            {
                // Validate Facebook token
                var fbUser = await ValidateFacebookToken(facebookLoginDto.AccessToken);

                if (fbUser == null)
                    throw new Exception("Invalid Facebook token");

                var email = fbUser.Email?.ToLower();
                if (string.IsNullOrEmpty(email))
                    throw new Exception("Email not provided by Facebook");

                // Check if user exists
                var user = await _context.Users
                    .Include(u => u.Subscription)
                    .FirstOrDefaultAsync(u => u.Email == email);

                if (user == null)
                {
                    // Create new user
                    user = new User
                    {
                        Email = email,
                        FirstName = fbUser.FirstName ?? "User",
                        LastName = fbUser.LastName ?? "",
                        AuthProvider = "Facebook",
                        ExternalAuthId = fbUser.Id,
                        CreatedAt = DateTime.Now,
                        FirstTimeOrder = true,
                        IsActive = true,
                        IsEmailVerified = true // Facebook emails are pre-verified
                    };

                    _context.Users.Add(user);
                }
                else if (user.AuthProvider != "Facebook")
                {
                    throw new Exception("Email already registered with different provider");
                }

                // Update refresh token
                user.RefreshToken = GenerateRefreshToken();
                user.RefreshTokenExpiryTime = DateTime.Now.AddDays(30);
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
                _logger.LogError(ex, "Facebook login failed");
                throw new Exception($"Facebook login failed: {ex.Message}");
            }
        }

        // Add helper method to generate verification tokens
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

        // Add helper method to validate Facebook token
        private async Task<FacebookUserInfo> ValidateFacebookToken(string accessToken)
        {
            using var client = new HttpClient();
            var response = await client.GetAsync(
                $"https://graph.facebook.com/me?fields=id,email,first_name,last_name&access_token={accessToken}"
            );

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<FacebookUserInfo>(content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }

            return null;
        }
    }
}
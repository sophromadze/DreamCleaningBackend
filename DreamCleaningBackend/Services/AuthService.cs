using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using Google.Apis.Auth;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Repositories.Interfaces;
using System.Security.Cryptography;

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
        private readonly IReferralService _referralService;
        private readonly IBubblePointsService _bubblePointsService;

        public AuthService(ApplicationDbContext context, IConfiguration configuration, IEmailService emailService, ILogger<AuthService> logger, ISpecialOfferService specialOfferService, IAuditService auditService, IUserRepository userRepository, IReferralService referralService, IBubblePointsService bubblePointsService)
        {
            _context = context;
            _configuration = configuration;
            _emailService = emailService;
            _logger = logger;
            _specialOfferService = specialOfferService;
            _auditService = auditService;
            _userRepository = userRepository;
            _referralService = referralService;
            _bubblePointsService = bubblePointsService;
        }

        private async Task TryGrantWelcomeBonusAsync(int userId)
        {
            try
            {
                await _bubblePointsService.GrantWelcomeBonus(userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GrantWelcomeBonus failed for user {UserId}", userId);
            }
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
                    IsEmailVerified = false
                };

                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                // Generate referral code for new user
                try
                {
                    user.ReferralCode = await GenerateUniqueReferralCode();
                    await _context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to generate referral code for user {UserId}", user.Id);
                }

                try
                {
                    await _specialOfferService.GrantAllActiveOffersToNewUser(user.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to grant first-time offer to user {UserId}", user.Id);
                    // Don't fail registration if special offer fails
                }

                await TryGrantWelcomeBonusAsync(user.Id);

                // Process referral if a code was provided (adds extra points for friend when enabled)
                if (!string.IsNullOrWhiteSpace(registerDto.ReferralCode))
                {
                    try
                    {
                        await _referralService.ProcessReferralRegistration(user.Id, registerDto.ReferralCode);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process referral for user {UserId}", user.Id);
                        // Don't fail registration
                    }
                }

                // Generate and send OTP for email verification
                var otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
                user.LoginOtpCode = otp;
                user.LoginOtpExpiry = DateTime.UtcNow.AddMinutes(10);
                user.LoginOtpAttempts = 0;

                // Auto-login: generate token so user is immediately signed in
                user.RefreshToken = GenerateRefreshToken();
                user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(30);
                user.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                try
                {
                    await _emailService.SendLoginOtpAsync(user.Email, user.FirstName, otp);
                }
                catch (Exception emailEx)
                {
                    _logger.LogError(emailEx, "Failed to send verification OTP to {Email}", user.Email);
                }

                // Return token immediately — user is logged in but email not yet verified
                return new AuthResponseDto
                {
                    User = MapUserToDto(user),
                    Token = CreateToken(user),
                    RefreshToken = user.RefreshToken,
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

            if (user == null || user.PasswordHash == null)
                throw new Exception("Invalid email or password");

            if (!user.IsActive)
                throw new Exception("Account is deactivated");

            if (!VerifyPasswordHash(loginDto.Password, user.PasswordHash, user.PasswordSalt))
                throw new Exception("Invalid email or password");

            // Check if email is verified
            if (!user.IsEmailVerified)
            {
                throw new Exception("Please verify your email before logging in. Check your inbox for the 6-digit verification code.");
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

                // Check if user exists by email (email is the primary identifier for account merging)
                var user = await _context.Users
                    .Include(u => u.Subscription)
                    .FirstOrDefaultAsync(u => u.Email == email.ToLower());

                var isNewGoogleUser = false;
                if (user == null)
                {
                    // Create new user
                    isNewGoogleUser = true;
                    user = new User
                    {
                        Email = email.ToLower(),
                        FirstName = firstName,
                        LastName = lastName,
                        AuthProvider = "Google",
                        ExternalAuthId = $"Google:{googleId}",
                        ProfilePictureUrl = profilePicture,
                        CreatedAt = DateTime.UtcNow,
                        FirstTimeOrder = true,
                        IsActive = true,
                        IsEmailVerified = true
                    };

                    _context.Users.Add(user);
                }
                else
                {
                // Account exists - block inactive users (same as local login)
                if (!user.IsActive)
                    throw new Exception("Account is deactivated");

                // Same email = same user, allow login with any method
                // Link Google to existing account (user can now use email/password, Google, OR Apple)
                
                if (user.AuthProvider == "Local")
                {
                    // Keep Local as primary, but allow Google login
                    // Store Google ID for future Google logins
                    user.ExternalAuthId = googleId;
                    user.IsEmailVerified = true;
                    user.ProfilePictureUrl = profilePicture;
                    _logger.LogInformation("Linked Google to existing local account {Email}", email);
                }
                else if (user.AuthProvider == "Apple")
                {
                    // User already has Apple, now adding Google - same email = same user
                    // Store Google ID (user can login with either Apple or Google now)
                    user.ExternalAuthId = googleId; // Update to Google ID (or we could keep Apple ID, but Google is current login)
                    user.ProfilePictureUrl = profilePicture;
                    _logger.LogInformation("Linked Google to existing Apple account {Email}", email);
                }
                else if (user.AuthProvider == "Google")
                {
                    // Already a Google user, update profile picture and ensure ID is correct
                    user.ExternalAuthId = googleId;
                    user.ProfilePictureUrl = profilePicture;
                }

                // Update profile info if missing
                if (string.IsNullOrEmpty(user.FirstName) || user.FirstName == "User")
                    user.FirstName = firstName;
                if (string.IsNullOrEmpty(user.LastName))
                    user.LastName = lastName;

                user.UpdatedAt = DateTime.UtcNow;
                }


                // Update refresh token
                user.RefreshToken = GenerateRefreshToken();
                user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(30);

                // Generate referral code for new OAuth users
                if (string.IsNullOrEmpty(user.ReferralCode))
                {
                    try { user.ReferralCode = await GenerateUniqueReferralCode(); } catch { }
                }

                await _context.SaveChangesAsync();

                if (isNewGoogleUser)
                {
                    await TryGrantWelcomeBonusAsync(user.Id);
                    try
                    {
                        await _specialOfferService.GrantAllActiveOffersToNewUser(user.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to grant first-time offer to user {UserId}", user.Id);
                    }

                    if (!string.IsNullOrWhiteSpace(googleLoginDto.ReferralCode))
                    {
                        try { await _referralService.ProcessReferralRegistration(user.Id, googleLoginDto.ReferralCode); }
                        catch (Exception ex) { _logger.LogError(ex, "Failed to process referral for Google user {UserId}", user.Id); }
                    }
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

        public async Task<AuthResponseDto> AppleLogin(AppleLoginDto appleLoginDto)
        {
            try
            {
                if (string.IsNullOrEmpty(appleLoginDto.IdentityToken))
                {
                    throw new Exception("Identity token is required");
                }

                var appleUser = await ValidateAppleToken(appleLoginDto.IdentityToken);
                
                if (appleUser == null)
                    throw new Exception("Invalid Apple token - token validation failed");

            // Get email: prefer user object (from Apple's first-auth response) when it's a real email.
            // When user chooses "Share My Email", Apple sends real email in both token and user object.
            // When user chooses "Hide My Email", both have relay. Prefer non-relay when we have both.
            var tokenEmail = appleUser.Email;
            var userObjEmail = appleLoginDto.User?.Email;
            var email = tokenEmail;

            if (!string.IsNullOrEmpty(userObjEmail) && !userObjEmail.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase))
            {
                // User object has real email (user chose Share My Email) - trust it
                email = userObjEmail;
            }
            else if (string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(userObjEmail))
            {
                email = userObjEmail;
            }

            var appleId = appleUser.Subject;
            var firstName = appleLoginDto.User?.Name?.FirstName ?? "User";
            var lastName = appleLoginDto.User?.Name?.LastName ?? "";

            // Shared email = real address (not @privaterelay). Hide My Email = relay address.
            // Use final email as source of truth: when user shared, we have real email from token or user object.
            var isRelayEmail = string.IsNullOrEmpty(email)
                || email.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase);

            // PRIMARY: Find user by email (email is the main identifier - same email = same user)
            // SECONDARY: Find by Apple ID only if email is not available (private relay email case)
            User? user = null;
            
            if (!string.IsNullOrEmpty(email))
            {
                user = await _context.Users
                    .Include(u => u.Subscription)
                    .FirstOrDefaultAsync(u => u.Email == email.ToLower());
            }
            
            // If not found by email, try by Apple ID (for private relay emails)
            if (user == null)
            {
                user = await _context.Users
                    .Include(u => u.Subscription)
                    .FirstOrDefaultAsync(u => u.ExternalAuthId == appleId || 
                                             (u.ExternalAuthId != null && u.ExternalAuthId.Contains(appleId)));
            }

            if (user == null)
            {
                // Create new user
                // If email is provided and is NOT a relay email, trust it (like Google login)
                user = new User
                {
                    Email = email?.ToLower() ?? $"{appleId}@privaterelay.appleid.com",
                    FirstName = firstName,
                    LastName = lastName,
                    AuthProvider = "Apple",
                    ExternalAuthId = appleId,
                    CreatedAt = DateTime.UtcNow,
                    FirstTimeOrder = true,
                    IsActive = true,
                    // Trust real emails from Apple (like Google) - no verification needed
                    IsEmailVerified = !isRelayEmail,
                    RequiresRealEmail = isRelayEmail
                };
                _context.Users.Add(user);
            }
            else
            {
                // Account exists - block inactive users (same as local login)
                if (!user.IsActive)
                    throw new Exception("Account is deactivated");

                // Same email = same user, allow login with any method
                // When existing account is found by email, just log in (no merge needed)
                
                // Update email if it was a private relay and now we have real email
                if (string.IsNullOrEmpty(user.Email) || user.Email.Contains("@privaterelay.appleid.com"))
                {
                    if (!string.IsNullOrEmpty(email) && !email.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase))
                    {
                        user.Email = email.ToLower();
                        user.RequiresRealEmail = false;
                        user.IsEmailVerified = true; // Trust real emails from Apple
                    }
                    else
                    {
                        user.RequiresRealEmail = true;
                        user.IsEmailVerified = false;
                    }
                }
                else if (!isRelayEmail && !string.IsNullOrEmpty(email))
                {
                    // User logged in with shared email and account exists - ensure email is verified (trust Apple emails)
                    user.IsEmailVerified = true;
                    user.RequiresRealEmail = false;
                }

                // Store Apple ID (if not already stored)
                // Since ExternalAuthId can only store one, we'll store the latest used provider
                // But email matching ensures they can login with any method
                if (string.IsNullOrEmpty(user.ExternalAuthId) || 
                    (!user.ExternalAuthId.Contains(appleId) && user.AuthProvider != "Apple"))
                {
                    // If user has Local account, keep AuthProvider as Local but store Apple ID
                    if (user.AuthProvider == "Local")
                    {
                        user.ExternalAuthId = appleId; // Store Apple ID for future Apple logins
                        user.IsEmailVerified = true;
                        _logger.LogInformation("Linked Apple to existing local account {Email}", email ?? user.Email);
                    }
                    // If user has Google, they can now also use Apple (same email = same user)
                    else if (user.AuthProvider == "Google")
                    {
                        // Keep Google as primary AuthProvider, but store Apple ID
                        // User can login with either Google or Apple now
                        user.ExternalAuthId = appleId; // Update to Apple ID (or we could keep Google ID, but Apple is current login)
                        _logger.LogInformation("Linked Apple to existing Google account {Email}", email ?? user.Email);
                    }
                    else
                    {
                        // Already Apple user, just ensure ID is correct
                        user.ExternalAuthId = appleId;
                    }
                }

                // Update profile info if missing
                if (string.IsNullOrEmpty(user.FirstName) || user.FirstName == "User")
                    user.FirstName = firstName;
                if (string.IsNullOrEmpty(user.LastName))
                    user.LastName = lastName;
            }

            user.RefreshToken = GenerateRefreshToken();
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(30);
            user.UpdatedAt = DateTime.UtcNow;

            // Generate referral code for new Apple users
            if (string.IsNullOrEmpty(user.ReferralCode))
            {
                try { user.ReferralCode = await GenerateUniqueReferralCode(); } catch { }
            }

            await _context.SaveChangesAsync();

            // Only grant special offers to truly new users (not when linking accounts)
            var isNewUser = user.CreatedAt > DateTime.UtcNow.AddMinutes(-1); // Created within last minute
            if (isNewUser)
            {
                await TryGrantWelcomeBonusAsync(user.Id);
                try
                {
                    await _specialOfferService.GrantAllActiveOffersToNewUser(user.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to grant first-time offer to user {UserId}", user.Id);
                }

                if (!string.IsNullOrWhiteSpace(appleLoginDto.ReferralCode))
                {
                    try { await _referralService.ProcessReferralRegistration(user.Id, appleLoginDto.ReferralCode); }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to process referral for Apple user {UserId}", user.Id); }
                }
            }

                var requiresRealEmail = user.RequiresRealEmail || (user.Email?.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase) == true);
                return new AuthResponseDto
                {
                    User = MapUserToDto(user),
                    Token = CreateToken(user),
                    RefreshToken = user.RefreshToken,
                    RequiresRealEmail = requiresRealEmail
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Apple login failed: {Message}", ex.Message);
                throw new Exception($"Apple login failed: {ex.Message}", ex);
            }
        }

        private async Task<AppleTokenPayload?> ValidateAppleToken(string identityToken)
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var jsonToken = handler.ReadToken(identityToken) as JwtSecurityToken;
                
                using var httpClient = new HttpClient();
                var response = await httpClient.GetStringAsync("https://appleid.apple.com/auth/keys");
                var appleKeys = System.Text.Json.JsonSerializer.Deserialize<AppleKeyResponse>(response);
                
                var kid = jsonToken?.Header.Kid;
                var appleKey = appleKeys?.Keys.FirstOrDefault(k => k.Kid == kid);
                
                if (appleKey == null) return null;

                var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Base64UrlDecode(appleKey.N),
                    Exponent = Base64UrlDecode(appleKey.E)
                });

                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = "https://appleid.apple.com",
                    ValidateAudience = true,
                    ValidAudience = _configuration["Authentication:Apple:ClientId"],
                    ValidateLifetime = true,
                    IssuerSigningKey = new RsaSecurityKey(rsa)
                };

                var principal = handler.ValidateToken(identityToken, validationParameters, out _);
                
                var isPrivateEmailClaim = principal.FindFirst("is_private_email")?.Value;
                var isPrivateEmail = string.Equals(isPrivateEmailClaim, "true", StringComparison.OrdinalIgnoreCase);

                return new AppleTokenPayload
                {
                    Subject = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value 
                              ?? principal.FindFirst("sub")?.Value ?? "",
                    Email = principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value 
                            ?? principal.FindFirst("email")?.Value,
                    IsPrivateEmail = isPrivateEmail
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Apple token validation failed: {Message}", ex.Message);
                return null;
            }
        }

        private byte[] Base64UrlDecode(string input)
        {
            var output = input.Replace('-', '+').Replace('_', '/');
            switch (output.Length % 4)
            {
                case 2: output += "=="; break;
                case 3: output += "="; break;
            }
            return Convert.FromBase64String(output);
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
                User = MapUserToDto(user)
            };
        }

        public async Task<bool> UserExists(string email)
        {
            return await _context.Users.AnyAsync(u => u.Email == email.ToLower());
        }

        private async Task<GoogleJsonWebSignature.Payload?> ValidateGoogleToken(string idToken)
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

        public async Task<string?> GetEmailFromGoogleTokenAsync(string idToken)
        {
            var payload = await ValidateGoogleToken(idToken);
            return payload?.Email;
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
        new Claim("AuthProvider", user.AuthProvider ?? "Local"),
        new Claim("RequiresRealEmail", (user.RequiresRealEmail || (user.Email?.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase) == true)).ToString())
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

            // User must have a password to change it (otherwise they should use Set password)
            if (user.PasswordHash == null)
                throw new Exception("You don't have a password set. Use Set password instead.");

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

        public async Task<bool> SetPassword(int userId, SetPasswordDto setPasswordDto)
        {
            var user = await _context.Users.FindAsync(userId);

            if (user == null)
                throw new Exception("User not found");

            if (user.PasswordHash != null)
                throw new Exception("You already have a password. Use Change password instead.");

            if (!PasswordValidator.IsValidPassword(setPasswordDto.NewPassword, out string passwordError))
                throw new Exception(passwordError);

            CreatePasswordHash(setPasswordDto.NewPassword, out string passwordHash, out string passwordSalt);

            user.PasswordHash = passwordHash;
            user.PasswordSalt = passwordSalt;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return true;
        }

        private UserDto MapUserToDto(User user)
        {
            var requiresRealEmail = user.RequiresRealEmail || (user.Email?.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase) == true);
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
                ProfilePictureUrl = user.ProfilePictureUrl,
                RequiresRealEmail = requiresRealEmail,
                HasPassword = user.PasswordHash != null,
                IsEmailVerified = user.IsEmailVerified
            };
        }

        public async Task<bool> VerifyEmail(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new Exception("Invalid or expired verification token");

            token = token.Trim();
            var tokenHash = HashVerificationToken(token);

            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.EmailVerificationToken == token
                    && u.EmailVerificationTokenExpiry > DateTime.UtcNow);

            if (user == null)
            {
                // Same link clicked again (e.g. double-click, prefetch)? Check if this token was already used.
                var alreadyVerifiedUser = await _context.Users
                    .FirstOrDefaultAsync(u => u.LastEmailVerificationTokenHash == tokenHash && u.IsEmailVerified);
                if (alreadyVerifiedUser != null)
                    return true; // Already verified with this token — return success so user sees success UI
                throw new Exception("Invalid or expired verification token");
            }

            user.IsEmailVerified = true;
            user.EmailVerificationToken = null;
            user.EmailVerificationTokenExpiry = null;
            user.LastEmailVerificationTokenHash = tokenHash;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Send welcome email
            await _emailService.SendWelcomeEmailAsync(user.Email, user.FirstName);

            return true;
        }

        private static string HashVerificationToken(string token)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(bytes);
        }

        public async Task<bool> ResendVerificationEmail(string email)
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Email == email.ToLower()
                    && !u.IsEmailVerified);

            if (user == null)
                return false; // Don't throw exception - for security

            // Generate new OTP
            var otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            user.LoginOtpCode = otp;
            user.LoginOtpExpiry = DateTime.UtcNow.AddMinutes(10);
            user.LoginOtpAttempts = 0;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // SEND OTP IN BACKGROUND
            _ = Task.Run(async () =>
            {
                try
                {
                    await _emailService.SendLoginOtpAsync(user.Email, user.FirstName, otp);
                    _logger.LogInformation($"Verification OTP sent successfully to {user.Email}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to send verification OTP to {user.Email}");
                }
            });

            return true;
        }

        public async Task<bool> InitiatePasswordReset(string email)
        {
            var emailLower = email?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(emailLower))
                return true;

            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Email.ToLower() == emailLower);

            if (user == null)
                return true; // Don't reveal if user exists

            // Send reset link for: Local (forgot password) or any user with no password (Admin-created, Google/Apple-only)
            bool canSetOrResetPassword = user.AuthProvider == "Local" || user.PasswordHash == null;
            if (!canSetOrResetPassword)
                return true; // Don't reveal; OAuth-only accounts don't use password reset

            user.PasswordResetToken = GenerateVerificationToken();
            user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            var resetLink = $"{_configuration["Frontend:Url"]}/auth/reset-password?token={user.PasswordResetToken}";

            try
            {
                await _emailService.SendPasswordResetAsync(user.Email, user.FirstName, resetLink);
                _logger.LogInformation("Password reset email sent successfully to {Email}", user.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send password reset email to {Email}", user.Email);
                throw new Exception("Failed to send the reset link. Please try again later.");
            }

            return true;
        }

        public async Task<(string? Email, bool IsSetPassword)?> GetResetPasswordInfoAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            var user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.PasswordResetToken == token
                    && u.PasswordResetTokenExpiry > DateTime.UtcNow);
            if (user == null) return null;
            return (user.Email, user.PasswordHash == null);
        }

        public async Task<string> CreateSetPasswordTokenAsync(int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                throw new Exception("User not found");
            user.PasswordResetToken = GenerateVerificationToken();
            user.PasswordResetTokenExpiry = DateTime.UtcNow.AddDays(7);
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return user.PasswordResetToken;
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

        public async Task<(bool Exists, bool HasPassword)> CheckEmailStatusAsync(string email)
        {
            var emailLower = email?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(emailLower))
                return (false, false);

            var user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email == emailLower && !u.IsDeleted);

            if (user == null)
                return (false, false);

            return (true, user.PasswordHash != null);
        }

        /// <summary>Sends a verification OTP to a user who registered locally (has a password). Used for email verification after registration.</summary>
        public async Task SendEmailVerificationOtpAsync(string email)
        {
            var emailLower = email?.Trim().ToLowerInvariant();
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Email == emailLower && !u.IsDeleted);

            if (user == null)
                throw new Exception("User not found");

            if (!user.IsActive)
                throw new Exception("Account is deactivated");

            if (user.IsEmailVerified)
                throw new Exception("Email is already verified.");

            // Generate 6-digit OTP
            var otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

            user.LoginOtpCode = otp;
            user.LoginOtpExpiry = DateTime.UtcNow.AddMinutes(10);
            user.LoginOtpAttempts = 0;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _emailService.SendLoginOtpAsync(user.Email, user.FirstName, otp);
        }

        public async Task SendLoginOtpAsync(string email)
        {
            var emailLower = email?.Trim().ToLowerInvariant();
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Email == emailLower && !u.IsDeleted);

            if (user == null)
                throw new Exception("User not found");

            if (user.PasswordHash != null)
                throw new Exception("This account already has a password. Please log in with your password.");

            if (!user.IsActive)
                throw new Exception("Account is deactivated");

            // Generate 6-digit OTP
            var otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

            user.LoginOtpCode = otp;
            user.LoginOtpExpiry = DateTime.UtcNow.AddMinutes(10);
            user.LoginOtpAttempts = 0;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _emailService.SendLoginOtpAsync(user.Email, user.FirstName, otp);
        }

        public async Task<AuthResponseDto> VerifyLoginOtpAsync(string email, string code)
        {
            var emailLower = email?.Trim().ToLowerInvariant();
            var user = await _context.Users
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Email == emailLower && !u.IsDeleted);

            if (user == null)
                throw new Exception("Invalid code");

            if (!user.IsActive)
                throw new Exception("Account is deactivated");

            if (user.LoginOtpCode == null || user.LoginOtpExpiry == null)
                throw new Exception("No active login code. Please request a new one.");

            if (user.LoginOtpExpiry < DateTime.UtcNow)
                throw new Exception("Code has expired. Please request a new one.");

            if (user.LoginOtpAttempts >= 5)
                throw new Exception("Too many failed attempts. Please request a new code.");

            if (user.LoginOtpCode != code.Trim())
            {
                user.LoginOtpAttempts++;
                await _context.SaveChangesAsync();
                throw new Exception("Invalid code");
            }

            // Clear OTP
            user.LoginOtpCode = null;
            user.LoginOtpExpiry = null;
            user.LoginOtpAttempts = 0;

            // Mark email as verified
            user.IsEmailVerified = true;

            // Generate refresh token so user is logged in
            user.RefreshToken = GenerateRefreshToken();
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(30);
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // If user already has a password (local registration), no password setup needed
            var requiresPasswordSetup = user.PasswordHash == null;

            return new AuthResponseDto
            {
                User = MapUserToDto(user),
                Token = CreateToken(user),
                RefreshToken = user.RefreshToken,
                RequiresPasswordSetup = requiresPasswordSetup
            };
        }

        public async Task<AuthResponseDto> CreateOrGetGuestUserAsync(string firstName, string lastName, string email, string phone, string? referralCode = null)
        {
            var emailLower = email?.Trim().ToLowerInvariant();

            var existingUser = await _context.Users
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Email == emailLower && !u.IsDeleted);

            User user;
            if (existingUser != null)
            {
                user = existingUser;
                // Update phone if user has none
                if (string.IsNullOrEmpty(user.Phone) && !string.IsNullOrEmpty(phone))
                {
                    user.Phone = phone;
                    user.UpdatedAt = DateTime.UtcNow;
                }
            }
            else
            {
                // Auto-create guest account with no password
                user = new User
                {
                    FirstName = firstName?.Trim() ?? "",
                    LastName = lastName?.Trim() ?? "",
                    Email = emailLower,
                    Phone = phone,
                    AuthProvider = "Local",
                    IsEmailVerified = true,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    FirstTimeOrder = true
                };
                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                await TryGrantWelcomeBonusAsync(user.Id);

                if (!string.IsNullOrWhiteSpace(referralCode))
                {
                    try { await _referralService.ProcessReferralRegistration(user.Id, referralCode); }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to process referral for guest user {UserId}", user.Id); }
                }
            }

            // Generate tokens for the guest auto-login
            user.RefreshToken = GenerateRefreshToken();
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(30);
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return new AuthResponseDto
            {
                User = MapUserToDto(user),
                Token = CreateToken(user),
                RefreshToken = user.RefreshToken,
                RequiresPasswordSetup = user.PasswordHash == null
            };
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

        private async Task<string> GenerateUniqueReferralCode()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var random = new Random();
            string code;
            int attempts = 0;
            do
            {
                var suffix = new string(Enumerable.Repeat(chars, 5)
                    .Select(s => s[random.Next(s.Length)]).ToArray());
                code = $"DREAM-{suffix}";
                attempts++;
                if (attempts > 50) break;
            }
            while (await _context.Users.AnyAsync(u => u.ReferralCode == code));
            return code;
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
            var requiresRealEmail = user.RequiresRealEmail || (user.Email?.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase) == true);
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
                ProfilePictureUrl = user.ProfilePictureUrl,
                RequiresRealEmail = requiresRealEmail,
                HasPassword = user.PasswordHash != null,
                IsEmailVerified = user.IsEmailVerified
            };
        }

        private static bool IsRelayEmail(string? email)
        {
            return !string.IsNullOrEmpty(email) && email.EndsWith("@privaterelay.appleid.com", StringComparison.OrdinalIgnoreCase);
        }

        public async Task RequestRealEmailVerification(int userId, string email)
        {
            var normalizedEmail = email.Trim().ToLower();
            if (IsRelayEmail(normalizedEmail))
                throw new Exception("Please enter your real email, not an Apple relay address.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(normalizedEmail, @"^[^@]+@[^@]+\.[^@]+$"))
                throw new Exception("Please enter a valid email address.");

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                throw new Exception("User not found");
            if (!user.RequiresRealEmail)
                throw new Exception("Your account does not require real email verification.");

            // Allow sending code even when email belongs to another account — after verify we'll offer account merge
            var code = new Random().Next(100000, 999999).ToString();
            var expiry = DateTime.UtcNow.AddMinutes(10);

            var existing = await _context.RealEmailVerifications.FirstOrDefaultAsync(r => r.UserId == userId);
            if (existing != null)
                _context.RealEmailVerifications.Remove(existing);

            _context.RealEmailVerifications.Add(new RealEmailVerification
            {
                UserId = userId,
                RequestedEmail = normalizedEmail,
                VerificationCode = code,
                ExpiresAt = expiry,
                Attempts = 0,
                CreatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();

            await _emailService.SendRealEmailVerificationCodeAsync(normalizedEmail, user.FirstName, code);
        }

        private const int MaxRealEmailVerificationAttempts = 5;

        public async Task<VerifyRealEmailResultDto> VerifyRealEmailCode(int userId, string email, string code)
        {
            int currentUserId = userId; // use local to avoid shadowing in any inner scope
            var normalizedEmail = email.Trim().ToLower();
            var pending = await _context.RealEmailVerifications
                .Include(r => r.User)
                .FirstOrDefaultAsync(r => r.UserId == currentUserId && r.RequestedEmail == normalizedEmail);

            if (pending == null)
                throw new Exception("Invalid or expired code. Please request a new verification code.");
            if (pending.ExpiresAt < DateTime.UtcNow)
            {
                _context.RealEmailVerifications.Remove(pending);
                await _context.SaveChangesAsync();
                throw new Exception("Code expired. Please request a new one.");
            }
            if (pending.Attempts >= MaxRealEmailVerificationAttempts)
            {
                _context.RealEmailVerifications.Remove(pending);
                await _context.SaveChangesAsync();
                throw new Exception("Too many failed attempts. Please request a new verification code.");
            }

            if (pending.VerificationCode != code.Trim())
            {
                pending.Attempts++;
                await _context.SaveChangesAsync();
                throw new Exception("Invalid code, please try again.");
            }

            _context.RealEmailVerifications.Remove(pending);

            // Check if this email already belongs to another account → offer merge
            var existingAccount = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.Id != currentUserId && !u.IsDeleted);
            if (existingAccount != null)
            {
                var mergeCode = new Random().Next(100000, 999999).ToString();
                var expiresAt = DateTime.UtcNow.AddMinutes(30);
                _context.AccountMergeRequests.Add(new AccountMergeRequest
                {
                    NewAccountId = currentUserId,
                    OldAccountId = existingAccount.Id,
                    VerifiedRealEmail = normalizedEmail,
                    VerificationCode = mergeCode,
                    Status = AccountMergeRequestStatus.Pending,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = expiresAt
                });
                await _context.SaveChangesAsync();
                await _emailService.SendAccountMergeConfirmationAsync(normalizedEmail, existingAccount.FirstName, mergeCode);
                return new VerifyRealEmailResultDto
                {
                    IsMergeScenario = true,
                    AccountExistsResponse = new AccountExistsResponseDto
                    {
                        ExistingAccountId = existingAccount.Id.ToString(),
                        ExistingAccountEmail = existingAccount.Email,
                        ExistingAccountName = $"{existingAccount.FirstName} {existingAccount.LastName}".Trim()
                    }
                };
            }

            // Reload user so we don't modify an entity that was attached via the pending we just removed
            var user = await _context.Users.FindAsync(currentUserId);
            if (user == null)
                throw new Exception("User not found.");

            // If a soft-deleted user holds this email, free it first (unique index applies to all rows)
            var softDeletedWithEmail = await _context.Users
                .FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.Id != currentUserId && u.IsDeleted);
            if (softDeletedWithEmail != null)
            {
                softDeletedWithEmail.Email = $"merged-{softDeletedWithEmail.Id}@deleted.local";
                softDeletedWithEmail.UpdatedAt = DateTime.UtcNow;
            }

            user.Email = normalizedEmail;
            user.RequiresRealEmail = false;
            user.IsEmailVerified = true;
            user.UpdatedAt = DateTime.UtcNow;
            user.RefreshToken = GenerateRefreshToken();
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(30);
            await _context.SaveChangesAsync();

            return new VerifyRealEmailResultDto
            {
                IsMergeScenario = false,
                AuthResponse = new AuthResponseDto
                {
                    User = MapUserToDto(user),
                    Token = CreateToken(user),
                    RefreshToken = user.RefreshToken,
                    RequiresRealEmail = false
                }
            };
        }
    }

    // Apple token validation helper classes
    public class AppleKeyResponse
    {
        [JsonPropertyName("keys")]
        public List<AppleKey> Keys { get; set; } = new();
    }

    public class AppleKey
    {
        [JsonPropertyName("kid")]
        public string Kid { get; set; } = "";
        [JsonPropertyName("n")]
        public string N { get; set; } = "";
        [JsonPropertyName("e")]
        public string E { get; set; } = "";
    }

    public class AppleTokenPayload
    {
        public string Subject { get; set; } = "";
        public string? Email { get; set; }
        /// <summary>
        /// True when user chose "Hide My Email" (relay address).
        /// False when user chose "Share My Email" (real email).
        /// </summary>
        public bool IsPrivateEmail { get; set; }
    }
}
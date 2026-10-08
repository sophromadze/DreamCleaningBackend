using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE .NET 10 UPGRADE MOVED TOKEN ISSUING TO JsonWebTokenHandler (2026-10).
    ///
    /// Access tokens live 30 days, so on deploy day every signed-in browser still holds one minted
    /// by JwtSecurityTokenHandler. Two things must therefore hold across the switch:
    ///
    ///   - a NEW token is the same token: same claim names (the old handler's outbound map wrote
    ///     "nameid", "email", "unique_name", "role"), same HS512 header, same lifetime claims;
    ///   - an OLD token keeps working: the bearer handler still accepts it with the same claims,
    ///     and the refresh path can still read the user out of it once it has expired.
    ///
    /// Breaking either signs the whole customer base out, or worse, leaves the role claim unread.
    /// </summary>
    public class JwtCompatibilityTests
    {
        private static readonly string SigningKey = new('k', 128);

        [Fact]
        public void ANewTokenCarriesTheClaimNamesAndHeaderEveryExistingSessionHas()
        {
            var token = CreateToken(NewUser());

            var parts = token.Split('.');
            Assert.Equal(3, parts.Length);

            using var header = JsonDocument.Parse(Base64UrlEncoder.Decode(parts[0]));
            Assert.Equal("HS512", header.RootElement.GetProperty("alg").GetString());
            Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());

            using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(parts[1]));
            var names = payload.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
            foreach (var expected in new[]
                     {
                         "nameid", "email", "unique_name", "role", "Role", "UserId", "FirstName", "LastName",
                         "FirstTimeOrder", "AuthProvider", "RequiresRealEmail", TokenVersionService.ClaimType,
                         "nbf", "exp", "iat"
                     })
            {
                Assert.Contains(expected, names);
            }

            // Nothing written under the long WS-Federation URIs: those would bypass the inbound map.
            Assert.DoesNotContain(names, n => n.StartsWith("http", StringComparison.Ordinal));

            Assert.Equal("7", payload.RootElement.GetProperty("nameid").GetString());
            Assert.Equal("Customer", payload.RootElement.GetProperty("role").GetString());
            Assert.Equal("Test User", payload.RootElement.GetProperty("unique_name").GetString());
        }

        [Fact]
        public void ANewTokenLivesThirtyDays()
        {
            var token = new JsonWebTokenHandler().ReadJsonWebToken(CreateToken(NewUser()));

            Assert.InRange(token.ValidTo - token.IssuedAt, TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1),
                TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));
        }

        [Fact]
        public async Task TheBearerHandlerReadsANewTokenAsTheSameUserAndRole()
        {
            var identity = await ValidateAsBearerAsync(CreateToken(NewUser()));

            AssertIsTheTestUser(identity);
        }

        [Fact]
        public async Task ATokenIssuedBeforeTheUpgradeStillAuthenticates()
        {
            var identity = await ValidateAsBearerAsync(LegacyToken(NewUser(), DateTime.UtcNow.AddDays(30)));

            AssertIsTheTestUser(identity);
        }

        [Fact]
        public async Task TheRefreshPathReadsTheUserOutOfAnExpiredTokenFromEitherHandler()
        {
            var service = NewAuthService();

            var expiredLegacy = LegacyToken(NewUser(), DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(-65));
            var current = CreateToken(NewUser());

            foreach (var token in new[] { expiredLegacy, current })
            {
                var identity = await ReadExpiredAsync(service, token);
                Assert.Equal("7", identity.FindFirst(ClaimTypes.NameIdentifier)?.Value);
                Assert.Equal("7", identity.FindFirst("UserId")?.Value);
            }
        }

        [Fact]
        public async Task TheRefreshPathRefusesATokenSignedWithAnotherAlgorithm()
        {
            var service = NewAuthService();
            var hs256 = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[] { new Claim("nameid", "7") }),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256)
            });

            await Assert.ThrowsAsync<SecurityTokenException>(() => ReadExpiredAsync(service, hs256));
        }

        [Fact]
        public async Task TheRefreshPathRefusesATokenSignedWithAnotherKey()
        {
            var service = NewAuthService();
            var forged = LegacyToken(NewUser(), DateTime.UtcNow.AddMinutes(5), signingKey: new string('x', 128));

            await Assert.ThrowsAsync<SecurityTokenException>(() => ReadExpiredAsync(service, forged));
        }

        // -- helpers ------------------------------------------------------------------------------

        private static void AssertIsTheTestUser(ClaimsIdentity identity)
        {
            Assert.Equal("7", identity.FindFirst(ClaimTypes.NameIdentifier)?.Value);
            Assert.Equal("user7@example.com", identity.FindFirst(ClaimTypes.Email)?.Value);
            Assert.Equal("Test User", identity.FindFirst(ClaimTypes.Name)?.Value);
            Assert.True(new ClaimsPrincipal(identity).IsInRole("Customer"));
            Assert.Equal("Customer", identity.FindFirst("Role")?.Value);
            Assert.Equal("3", identity.FindFirst(TokenVersionService.ClaimType)?.Value);
        }

        /// <summary>Validates exactly as the app's JwtBearer registration does (default token handler).</summary>
        private static async Task<ClaimsIdentity> ValidateAsBearerAsync(string token)
        {
            var handler = new JwtBearerOptions().TokenHandlers.Single();
            var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            });

            Assert.True(result.IsValid, result.Exception?.Message);
            return result.ClaimsIdentity;
        }

        /// <summary>A token exactly as the pre-upgrade AuthService.CreateToken wrote it.</summary>
        private static string LegacyToken(User user, DateTime expires, DateTime? notBefore = null, string? signingKey = null)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Email, user.Email),
                new(ClaimTypes.Name, $"{user.FirstName} {user.LastName}"),
                new(ClaimTypes.Role, user.Role.ToString()),
                new("Role", user.Role.ToString()),
                new("UserId", user.Id.ToString()),
                new(TokenVersionService.ClaimType, user.TokenVersion.ToString())
            };

            var handler = new JwtSecurityTokenHandler();
            var token = handler.CreateToken(new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                NotBefore = notBefore,
                IssuedAt = notBefore,
                Expires = expires,
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey ?? SigningKey)),
                    SecurityAlgorithms.HmacSha512Signature)
            });
            return handler.WriteToken(token);
        }

        private static User NewUser() => new()
        {
            Id = 7,
            Email = "user7@example.com",
            FirstName = "Test",
            LastName = "User",
            Role = UserRole.Customer,
            TokenVersion = 3
        };

        /// <summary>
        /// The token methods are private and touch only the configured signing key, so none of the
        /// service's other dependencies are reached (same arrangement as SessionRevocationTests).
        /// </summary>
        private static AuthService NewAuthService()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["AppSettings:Token"] = SigningKey })
                .Build();

            return (AuthService)Activator.CreateInstance(
                typeof(AuthService),
                null, configuration, null, null, null, null, null, null, null, null, null)!;
        }

        private static string CreateToken(User user)
        {
            var method = typeof(AuthService).GetMethod("CreateToken", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (string)method!.Invoke(NewAuthService(), new object[] { user })!;
        }

        private static async Task<ClaimsIdentity> ReadExpiredAsync(AuthService service, string token)
        {
            var method = typeof(AuthService).GetMethod("GetPrincipalFromExpiredTokenAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return await (Task<ClaimsIdentity>)method!.Invoke(service, new object[] { token })!;
        }
    }
}

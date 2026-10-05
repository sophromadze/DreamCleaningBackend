using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// A real Kestrel host on a loopback port, authenticating exactly the way production does
    /// with Authentication:UseCookieAuth = true: the JWT is read from the access_token cookie,
    /// which is what a same-origin &lt;img src&gt; carries. Only the controllers a test names are
    /// mounted, over an in-memory database and a throwaway uploads folder, so the private-file
    /// endpoints can be exercised over HTTP — status codes, headers and bytes — without MariaDB.
    /// </summary>
    public sealed class PrivateFileTestHost : IAsyncDisposable
    {
        private const string SigningKey = "private-file-tests-signing-key-0123456789-abcdefghijklmnopqrstuvwxyz";

        private readonly WebApplication _app;

        public string UploadRoot { get; }
        public IServiceProvider Services => _app.Services;
        public HttpClient Client { get; }

        private PrivateFileTestHost(WebApplication app, string uploadRoot, HttpClient client)
        {
            _app = app;
            UploadRoot = uploadRoot;
            Client = client;
        }

        public static async Task<PrivateFileTestHost> StartAsync(
            Type[] controllers,
            Action<IServiceCollection>? configureServices = null,
            Action<WebApplication, string>? configurePipeline = null,
            string url = "http://127.0.0.1:0")
        {
            var uploadRoot = Path.Combine(Path.GetTempPath(), "dc-private-files-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(uploadRoot);
            var dbName = $"private-files-{Guid.NewGuid()}";

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseUrls(url);
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileUpload:Path"] = uploadRoot,
                ["AppSettings:Token"] = SigningKey
            });

            builder.Services
                .AddControllers()
                .ConfigureApplicationPartManager(apm =>
                {
                    apm.ApplicationParts.Clear();
                    apm.ApplicationParts.Add(new AssemblyPart(typeof(ApplicationDbContext).Assembly));
                    foreach (var p in apm.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                        apm.FeatureProviders.Remove(p);
                    apm.FeatureProviders.Add(new OnlyTheseControllers(controllers));
                });

            builder.Services.AddDbContext<ApplicationDbContext>(o => o
                .UseInMemoryDatabase(dbName, b => b.EnableNullChecks(false))
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<IPermissionService, PermissionService>();
            builder.Services.AddScoped<IAuditService, AuditService>();
            builder.Services.AddScoped<IUserCleaningPhotoService, UserCleaningPhotoService>();
            configureServices?.Invoke(builder.Services);

            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.Zero
                    };
                    // Same source as Program.cs's cookie mode: the token rides in a cookie.
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = ctx =>
                        {
                            // null (no cookie) falls back to the Authorization header, which the
                            // dev SPA sends on XHRs; an <img> can only ever bring the cookie.
                            ctx.Token = ctx.Request.Cookies["access_token"];
                            return Task.CompletedTask;
                        }
                    };
                });
            builder.Services.AddAuthorization();

            var app = builder.Build();
            configurePipeline?.Invoke(app, uploadRoot);
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(address)
            };
            return new PrivateFileTestHost(app, uploadRoot, client);
        }

        public async Task SeedAsync(Action<ApplicationDbContext> seed)
        {
            using var scope = _app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            seed(db);
            await db.SaveChangesAsync();
        }

        /// <summary>Writes a file under the uploads root and returns its stored "/sub/name" path.</summary>
        public string WriteUpload(string relativePath, byte[]? bytes = null)
        {
            var full = Path.Combine(UploadRoot, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes ?? WebpBytes);
            return "/" + relativePath.TrimStart('/');
        }

        /// <summary>A minimal RIFF/WEBP header — enough for byte comparisons and type sniffing.</summary>
        public static readonly byte[] WebpBytes =
            { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' };

        public static string TokenFor(int userId, UserRole role)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role.ToString()),
                new Claim("Role", role.ToString()),
                new Claim("UserId", userId.ToString())
            };
            var creds = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha512Signature);
            var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>GET as the browser would for an &lt;img src&gt;: cookie only, no Authorization header.</summary>
        public Task<HttpResponseMessage> GetAsync(string url, int? userId = null, UserRole? role = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (userId.HasValue && role.HasValue)
                request.Headers.Add("Cookie", "access_token=" + TokenFor(userId.Value, role.Value));
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            try { Directory.Delete(UploadRoot, recursive: true); } catch { /* temp folder */ }
        }

        private sealed class OnlyTheseControllers : ControllerFeatureProvider
        {
            private readonly HashSet<Type> _allowed;
            public OnlyTheseControllers(IEnumerable<Type> allowed) => _allowed = allowed.ToHashSet();

            protected override bool IsController(System.Reflection.TypeInfo typeInfo)
                => base.IsController(typeInfo) && _allowed.Contains(typeInfo.AsType());
        }
    }

    internal static class HttpStatusAssert
    {
        public static void Is(HttpStatusCode expected, HttpResponseMessage response, string because)
            => Xunit.Assert.True(response.StatusCode == expected,
                $"{because} — expected {(int)expected}, got {(int)response.StatusCode}.");
    }
}

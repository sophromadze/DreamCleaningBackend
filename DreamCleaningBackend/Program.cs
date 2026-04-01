using System.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Repositories.Interfaces;
using DreamCleaningBackend.Repositories;
using DreamCleaningBackend.Hubs;
using DreamCleaningBackend.Services;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using MySqlConnector;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<IPermissionService, PermissionService>();

// Add memory cache for short-lived tokens (e.g. Google merge initiation)
builder.Services.AddMemoryCache();

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(o => { o.JsonSerializerOptions.PropertyNameCaseInsensitive = true; });
builder.Services.AddEndpointsApiExplorer();

// Swagger configuration
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Dream Cleaning API",
        Version = "v1",
        Description = "API for Dream Cleaning Services"
    });

    // Add JWT Authentication to Swagger
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme. \r\n\r\n " +
                      "Enter your token in the text input below.\r\n\r\n" +
                      "Example: \"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...\""
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

builder.Services.AddAuthorization();

// Add CSRF Protection for cookie authentication
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
});

// Add SignalR
builder.Services.AddSignalR()
    .AddHubOptions<LiveChatHub>(options =>
    {
        options.MaximumReceiveMessageSize = 6 * 1024 * 1024; // 6MB to support base64 images up to ~4.5MB
    });

// Database Configuration
builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    var serverVersion = new MariaDbServerVersion(new Version(10, 9, 8));
    options.UseMySql(connectionString, serverVersion);
});

// Check if we're using cookie authentication
var useCookieAuth = builder.Configuration.GetValue<bool>("Authentication:UseCookieAuth", false);

// Authentication Configuration - Updated for both cookie and JWT auth
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8
                .GetBytes(builder.Configuration.GetSection("AppSettings:Token").Value)),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };

        // Configure JWT for both SignalR and cookie auth
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var path = context.HttpContext.Request.Path;

                if (useCookieAuth)
                {
                    // For cookie auth, read token from cookie
                    context.Token = context.Request.Cookies["access_token"];
                    
                    // For SignalR with cookies, still check query string as fallback
                    if (string.IsNullOrEmpty(context.Token) && path.StartsWithSegments("/userManagementHub"))
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken))
                        {
                            context.Token = accessToken;
                        }
                    }
                }
                else
                {
                    // Original logic for query string token (SignalR)
                    var accessToken = context.Request.Query["access_token"];

                    // If the request is for our hub...
                    if (!string.IsNullOrEmpty(accessToken) &&
                        (path.StartsWithSegments("/userManagementHub")))
                    {
                        // Read the token out of the query string
                        context.Token = accessToken;
                    }
                }
                
                return Task.CompletedTask;
            }
        };
    });

// Repositories
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IApartmentRepository, ApartmentRepository>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();

// Services
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<ISmsService, SmsService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAccountMergeService, AccountMergeService>();

// Background Services
builder.Services.AddScoped<IProfileService, ProfileService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IUserManagementService, UserManagementService>();
builder.Services.AddScoped<IGiftCardService, GiftCardService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddHostedService<UnverifiedUserCleanupService>();
builder.Services.AddScoped<ISpecialOfferService, SpecialOfferService>();
builder.Services.AddScoped<ICleanerService, CleanerService>();
builder.Services.AddHostedService<CleanerNotificationService>();
builder.Services.AddHostedService<CustomerNotificationService>();
builder.Services.AddSingleton<IBookingDataService, BookingDataService>();
builder.Services.AddScoped<IStripeService, StripeService>();
builder.Services.AddScoped<IMaintenanceModeService, MaintenanceModeService>();
builder.Services.AddHostedService<AuditLogCleanupService>();
builder.Services.AddHostedService<ScheduledMailService>();
builder.Services.AddHostedService<ScheduledSmsService>();

// LiveChat services
builder.Services.AddSingleton<LiveChatSessionManager>();
builder.Services.AddSingleton<TelegramBotService>();
builder.Services.AddHostedService<LiveChatCleanupService>();

builder.Services.AddHttpClient();

// CORS Configuration - Updated for cookie auth
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngularApp",
        policy =>
        {
            policy.WithOrigins("http://localhost:4200") // Angular dev server
                   .AllowAnyHeader()
                   .AllowAnyMethod()
                   .AllowCredentials(); // Important for cookies
        });

    // Add production policy
    options.AddPolicy("ProductionPolicy",
        policy =>
        {
            policy.WithOrigins(
                    builder.Configuration["Frontend:Url"],
                    "https://dreamcleaningnearme.com",
                    "http://dreamcleaningnearme.com",
                    "https://www.dreamcleaningnearme.com",
                    "http://www.dreamcleaningnearme.com"
                )
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials(); // Important for cookies
        });
});

var app = builder.Build();

// Get logger
var logger = app.Services.GetRequiredService<ILogger<Program>>();

// Apply migrations automatically
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    try
    {
        if (await context.Database.CanConnectAsync())
        {
            var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
            if (pendingMigrations.Any())
            {
                await context.Database.MigrateAsync();
            }
        }
    }
    catch (MySqlException ex) when (ex.Message.Contains("already exists"))
    {
        Console.WriteLine("Database tables already exist. Skipping migration.");
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// REMOVE THIS FIRST HTTPS REDIRECTION - Nginx handles SSL
// app.UseHttpsRedirection();

var fileUploadPath = builder.Configuration["FileUpload:Path"];
if (!string.IsNullOrEmpty(fileUploadPath))
{
    var fullPath = Path.GetFullPath(fileUploadPath);
    Directory.CreateDirectory(fullPath); // Ensure directory exists

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(fullPath),
        RequestPath = "" // Serve from root, so /images/file.jpg works
    });
}

app.UseCors(app.Environment.IsDevelopment() ? "AllowAngularApp" : "ProductionPolicy");

// Add exception handling
app.UseExceptionHandler(appError =>
{
    appError.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";

        var contextFeature = context.Features.Get<IExceptionHandlerFeature>();
        if (contextFeature != null)
        {
            logger.LogError($"Error: {contextFeature.Error}");

            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                StatusCode = context.Response.StatusCode,
                Message = "Internal Server Error",
                Detailed = app.Environment.IsDevelopment() ? contextFeature.Error?.StackTrace : null
            }));
        }
    });
});

Console.WriteLine("Using DB: " + builder.Configuration.GetConnectionString("DefaultConnection"));

app.UseAuthentication();
app.UseAuthorization();

// Block API access for users who must verify real email (Apple relay), except verification endpoints and logout
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }
    // Allow login and verification endpoints for users with RequiresRealEmail (Apple "Hide My Email")
    var allowPaths = new[] { 
        "/api/auth/apple-login",           // Must allow so user can complete Apple login and create temp account
        "/api/auth/current-user",          // Needed so user can stay logged in and reach verify-email page
        "/api/auth/request-email-verification", 
        "/api/auth/verify-email-code", 
        "/api/auth/confirm-account-merge", // Merge temp Apple account with existing account (email code only)
        "/api/auth/resend-merge-code",     // Resend merge confirmation code
        "/api/auth/logout" 
    };
    if (allowPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
    {
        await next();
        return;
    }
    var requiresRealEmail = context.User.FindFirst("RequiresRealEmail")?.Value;
    if (string.Equals(requiresRealEmail, "True", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 403;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            error = "EMAIL_REQUIRED",
            message = "Please verify your real email to continue"
        }));
        return;
    }
    await next();
});

app.MapControllers();

// Map SignalR Hub
app.MapHub<UserManagementHub>("/userManagementHub");

// Map LiveChat SignalR Hub (anonymous access — no auth required)
app.MapHub<LiveChatHub>("/liveChatHub");

// Add logging to see if hub is registered
Console.WriteLine("SignalR Hub mapped to: /userManagementHub");
Console.WriteLine("SignalR Hub mapped to: /liveChatHub");

// REMOVE THIS ENTIRE SECTION - Not needed since Nginx handles HTTPS
/*
// Force HTTPS in production
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
    app.UseHsts();
}
*/

// Add security headers
app.Use(async (context, next) =>
{
    if (!app.Environment.IsDevelopment())
    {
        context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Add("X-Frame-Options", "SAMEORIGIN");
        context.Response.Headers.Add("X-XSS-Protection", "1; mode=block");
        context.Response.Headers.Add("Referrer-Policy", "strict-origin-when-cross-origin");
        context.Response.Headers.Add("Cross-Origin-Opener-Policy", "same-origin-allow-popups");
        
        // Add cookie security headers for production
        if (useCookieAuth)
        {
            context.Response.Headers.Add("Set-Cookie", "SameSite=Strict; Secure");
        }
    }
    await next();
});

// Register Telegram webhook on startup (production only — bot token is only in appsettings.Production.json)
if (!app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var telegramBot = scope.ServiceProvider.GetRequiredService<TelegramBotService>();
    if (telegramBot.IsConfigured)
    {
        var webhookBase = builder.Configuration["TelegramBot:WebhookBaseUrl"] ?? "https://dreamcleaningnearme.com";
        await telegramBot.SetWebhook($"{webhookBase}/api/telegram/webhook");
    }
}

app.Run();
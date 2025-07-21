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
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using MySqlConnector;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<IPermissionService, PermissionService>();

// Add services to the container.
builder.Services.AddControllers();
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
builder.Services.AddSignalR();

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
builder.Services.AddScoped<IAuthService, AuthService>();
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
        // Check if database exists and can connect
        if (await context.Database.CanConnectAsync())
        {
            // Get pending migrations
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

        // Ensure the migration history is updated
        var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
        if (!appliedMigrations.Contains("20250618161603_InitialCreate"))
        {
            // The tables exist but migration history is missing
            Console.WriteLine("Warning: Tables exist but migration history is incomplete.");
        }
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
app.MapControllers();

// Map SignalR Hub
app.MapHub<UserManagementHub>("/userManagementHub");

// Add logging to see if hub is registered
Console.WriteLine("SignalR Hub mapped to: /userManagementHub");

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

app.Run();
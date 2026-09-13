using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static DreamCleaningBackend.Tests.RecurringRefinementTests;

namespace DreamCleaningBackend.Tests;

public class BookingNotificationLoggingTests
{
    private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString(), b => b.EnableNullChecks(false))
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, true)]
    [InlineData(120, false)]
    [InlineData(125, false)]
    [InlineData(126, true)]
    public async Task DurationWarningRequiresAnActualEstimate_AndNeverChangesSavedDuration(int? estimate, bool warns)
    {
        using var db = Db();
        var serviceType = new ServiceType { Id = 1, Name = "Residential", BasePrice = 100, TimeDuration = 120 };
        db.ServiceTypes.Add(serviceType);
        db.Users.Add(new User { Id = 1 });
        await db.SaveChangesAsync();
        var logger = new RecordingLogger<BookingCreationService>();
        var loyalty = new LoyaltyDiscountService(db, new RecordingAuditService(), NullLogger<LoyaltyDiscountService>.Instance);
        var service = new BookingCreationService(db, loyalty, Stub<IGiftCardService>(), logger);
        // The recurring generator uses this same snapshot, which must not copy an old estimate.
        var dto = OrderBookingSnapshot.ToBookingDto(new Order
        {
            ServiceTypeId = 1, ServiceDate = DateTime.Today.AddDays(7),
            ServiceTime = TimeSpan.FromHours(9), TotalDuration = 999
        }, serviceType, Array.Empty<DreamCleaningBackend.Models.OrderService>(), Array.Empty<OrderExtraService>());
        Assert.Null(dto.TotalDuration);
        dto.TotalDuration = estimate;

        var order = await service.CreateOrderAsync(dto, 1, false);

        Assert.Equal(120m, order.TotalDuration);
        Assert.Equal(warns, logger.Entries.Any(e => e.Level == LogLevel.Warning));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("Backend value wins"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RejectedRecurringSms_IsSkipped_AndOnlySuccessfulDeliveryIsRecorded(bool emailSucceeds, bool unexpectedFailure)
    {
        using var db = Db();
        db.Users.Add(new User { Id = 1, IsActive = true, CanReceiveEmails = emailSucceeds, CanReceiveMessages = true });
        db.RecurringOrderSeries.Add(new RecurringOrderSeries { Id = 1, IsActive = true, AutoRequestPayment = true });
        db.Orders.Add(new Order
        {
            Id = 1, UserId = 1, RecurringSeriesId = 1, ServiceDate = NyTimeHelper.NowNy.Date.AddDays(7),
            ServiceTime = TimeSpan.FromHours(9), Status = OrderStatuses.Pending,
            PaymentMethod = PaymentMethod.Normal, Total = 100, ContactFirstName = "Test",
            ContactPhone = "1234567890", ContactEmail = "test@example.invalid"
        });
        await db.SaveChangesAsync();
        var smsAttempts = 0;
        var sms = Stub<ISmsService>((method, args) =>
        {
            Assert.Equal(nameof(ISmsService.SendPaymentReminderSmsAsync), method.Name);
            smsAttempts++;
            return Task.FromException(unexpectedFailure
                ? new InvalidOperationException("Provider unavailable")
                : new InvalidPhoneNumberException("1234567890", "Invalid destination number"));
        });
        var logger = new RecordingLogger<RecurringOrderGenerationService>();
        using var provider = new ServiceCollection().AddSingleton(db)
            .AddSingleton(Stub<IRecurringOrderSeriesService>((method, args) => Task.FromResult(0)))
            .AddSingleton(Stub<IEmailService>()).AddSingleton(sms).BuildServiceProvider();
        using var worker = new RecurringOrderGenerationService(provider, logger, new ConfigurationBuilder().Build());

        await worker.SweepAsync();

        Assert.Equal(1, smsAttempts);
        Assert.Equal(emailSucceeds ? 1 : 0, await db.NotificationLogs.CountAsync());
        Assert.Equal(unexpectedFailure, logger.Entries.Any(e => e.Level >= LogLevel.Warning));
    }

    [Fact]
    public async Task MalformedPhone_IsReportedOnce_AndDoesNotReturnSuccess()
    {
        using var db = Db();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RingCentral:EnableSmsSending"] = "true", ["RingCentral:FromNumber"] = "+12125550100",
            ["RingCentral:ClientId"] = "test", ["RingCentral:ClientSecret"] = "test",
            ["RingCentral:ServerUrl"] = "https://example.invalid", ["RingCentral:JwtToken"] = "test"
        }).Build();
        var logger = new RecordingLogger<SmsService>();
        var service = new SmsService(config, logger, db);

        var error = await Assert.ThrowsAsync<InvalidPhoneNumberException>(() => service.SendSmsAsync("bad", "Test"));

        Assert.Null(error.InnerException);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("invalid format", entry.Message);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}

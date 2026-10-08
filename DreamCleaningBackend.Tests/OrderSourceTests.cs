using DreamCleaningBackend.Controllers;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// Company → Order Sources (2026-10): <see cref="OrderSourceClassifier"/> is the only thing
    /// that buckets an order, the report arithmetic, and the raw first-touch signals the booking
    /// now stores for it.
    /// </summary>
    public class OrderSourceTests
    {
        private const string Own = "dreamcleaningnyc.com";

        private static string Classify(
            bool admin = false, string? channel = null, string? source = null, string? medium = null,
            string? clickIdType = null, string? referrer = null) =>
            OrderSourceClassifier.Classify(new OrderSourceSignals(admin, channel, source, medium, clickIdType, referrer), Own);

        // ── Admin-booked and Paid come first ───────────────────────────────────────────────

        [Fact]
        public void AnAdminBookingIsAdminBooked_WhateverElseItCarries()
        {
            Assert.Equal("admin", Classify(admin: true, channel: "Paid Search", clickIdType: "gclid"));
            Assert.Equal("admin", Classify(channel: "Phone/Unknown"));   // legacy admin stamp
        }

        [Theory]
        [InlineData("gclid")]
        [InlineData("gbraid")]   // iOS Google Ads clicks - the browser used to call these Organic
        [InlineData("wbraid")]
        [InlineData("msclkid")]
        public void AnAdClickIdIsPaid_EvenWithAGoogleReferrer(string clickId)
        {
            Assert.Equal("paid", Classify(clickIdType: clickId, referrer: "https://www.google.com/",
                source: "www.google.com", medium: "referral", channel: "Organic Search"));
        }

        [Theory]
        [InlineData("cpc")]
        [InlineData("PPC")]
        [InlineData("paid")]
        [InlineData("paid_social")]
        public void APaidUtmMediumIsPaid(string medium)
        {
            Assert.Equal("paid", Classify(source: "google", medium: medium));
        }

        [Fact]
        public void AnUnknownClickIdTypeIsIgnored()
        {
            Assert.Null(OrderSourceClassifier.NormalizeClickIdType("fbclid"));
            Assert.Equal("gclid", OrderSourceClassifier.NormalizeClickIdType(" GCLID "));
        }

        // ── Raw signals ────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("https://www.google.com/", "organic")]
        [InlineData("https://www.bing.com/search", "organic")]
        [InlineData("https://duckduckgo.com/", "organic")]
        [InlineData("https://gemini.google.com/app", "ai")]   // AI host under a search domain
        [InlineData("https://chatgpt.com/", "ai")]
        [InlineData("https://l.facebook.com/", "social")]
        [InlineData("https://www.yelp.com/biz/x", "referral")]
        [InlineData("https://www.dreamcleaningnyc.com/pricing", "direct")]   // our own site
        public void TheReferrerDecides(string referrer, string expected)
        {
            Assert.Equal(expected, Classify(referrer: referrer, source: new Uri(referrer).Host, medium: "referral"));
        }

        [Fact]
        public void UtmMediumsDecideBeforeHosts()
        {
            Assert.Equal("email", Classify(source: "newsletter", medium: "email"));
            Assert.Equal("social", Classify(source: "ig", medium: "social"));
            Assert.Equal("organic", Classify(source: "google", medium: "organic"));   // GA4 back-fill shape
        }

        [Fact]
        public void TheDirectPlaceholdersAreDirect()
        {
            Assert.Equal("direct", Classify(source: "(direct)", medium: "(none)", channel: "Direct"));
        }

        // ── Historical orders: the stored channel is the fallback ─────────────────────────

        [Theory]
        [InlineData("Paid Search", "paid")]
        [InlineData("Organic Search", "organic")]
        [InlineData("Direct", "direct")]
        [InlineData("Referral", "referral")]
        [InlineData("Social", "social")]
        [InlineData("Email", "email")]
        [InlineData("AI Assistant", "ai")]
        [InlineData("Unassigned", "unknown")]
        public void AChannelWithNoRawSignalsMapsByName(string channel, string expected)
        {
            Assert.Equal(expected, Classify(channel: channel));
        }

        [Fact]
        public void AnOrderWithNothingRecordedIsUnknown()
        {
            Assert.Equal("unknown", Classify());
        }

        [Fact]
        public void EverySourceHasALabelAndAStableOrder()
        {
            Assert.Equal(new[] { "paid", "organic", "direct", "referral", "social", "email", "ai", "admin", "unknown" },
                OrderSourceClassifier.All.Select(a => a.Key).ToArray());
            Assert.All(OrderSourceClassifier.All, a => Assert.False(string.IsNullOrWhiteSpace(a.Label)));
        }

        // ── The report ─────────────────────────────────────────────────────────────────────

        private static OrderSourcesReport.Row Row(DateTime date, decimal subTotal, string? channel = null,
            bool admin = false, string? clickIdType = null, decimal refunded = 0m) =>
            new(date, admin, channel, null, null, clickIdType, null,
                subTotal, 0m, 0m, 0m, Math.Round(subTotal * 0.08875m, 2), 20m, 0m, refunded, PaymentMethod.Normal);

        [Fact]
        public void TheReportCountsOrdersAndRevenuePerSource_AndKeepsEmptyRows()
        {
            var d = new DateTime(2026, 9, 1);
            var report = OrderSourcesReport.Build(new[]
            {
                Row(d, 200m, clickIdType: "gclid"),
                Row(d.AddDays(1), 100m, channel: "Paid Search"),
                Row(d.AddDays(2), 300m, channel: "Organic Search"),
                Row(d.AddDays(3), 150m, admin: true),
            }, d, d.AddDays(29), OrderSourcesReport.Day, Own);

            Assert.Equal(4, report.TotalOrders);
            // Revenue is Company Revenue: the subtotal, tips and remitted tax left out.
            Assert.Equal(750m, report.TotalRevenue);
            var paid = report.Sources.Single(s => s.Key == "paid");
            Assert.Equal(2, paid.Orders);
            Assert.Equal(300m, paid.Revenue);
            Assert.Equal(50m, paid.OrderShare);
            Assert.Equal(40m, paid.RevenueShare);
            Assert.Equal(150m, paid.AverageOrderValue);
            Assert.Equal(9, report.Sources.Count);
            Assert.Equal(0, report.Sources.Single(s => s.Key == "email").Orders);
            Assert.Equal(30, report.Periods.Count);   // every day, empty ones included
        }

        [Fact]
        public void ARefundComesOffTheRevenue()
        {
            var d = new DateTime(2026, 9, 1);
            var report = OrderSourcesReport.Build(new[] { Row(d, 200m, channel: "Direct", refunded: 100m) },
                d, d, OrderSourcesReport.Day, Own);

            Assert.True(report.Sources.Single(s => s.Key == "direct").Revenue < 200m);
        }

        [Fact]
        public void WeeksStartOnMonday_AndMonthsOnTheFirst()
        {
            Assert.Equal(new DateTime(2026, 9, 28), OrderSourcesReport.PeriodStart(new DateTime(2026, 10, 4), OrderSourcesReport.Week));
            Assert.Equal(new DateTime(2026, 10, 5), OrderSourcesReport.PeriodStart(new DateTime(2026, 10, 5), OrderSourcesReport.Week));
            Assert.Equal(new DateTime(2026, 10, 1), OrderSourcesReport.PeriodStart(new DateTime(2026, 10, 19), OrderSourcesReport.Month));
        }

        [Fact]
        public void TheGrainFollowsTheRangeUnlessAsked()
        {
            var d = new DateTime(2026, 1, 1);
            Assert.Equal("day", OrderSourcesReport.ResolveGranularity(null, d, d.AddDays(30)));
            Assert.Equal("week", OrderSourcesReport.ResolveGranularity("auto", d, d.AddDays(90)));
            Assert.Equal("month", OrderSourcesReport.ResolveGranularity(null, d, d.AddDays(364)));
            Assert.Equal("month", OrderSourcesReport.ResolveGranularity("Month", d, d.AddDays(3)));
            Assert.Null(OrderSourcesReport.ResolveGranularity("year", d, d));
        }

        [Fact]
        public async Task TheEndpointCountsOnlyDoneVisibleOrdersInTheServiceDateWindow()
        {
            using var db = Db();
            var inWindow = new DateTime(2026, 9, 10);
            db.Orders.AddRange(
                Order(1, inWindow, "Done", hidden: false, channel: "Paid Search"),
                Order(2, inWindow, "Done", hidden: true, channel: "Paid Search"),    // hidden
                Order(3, inWindow, "Active", hidden: false, channel: "Paid Search"), // not done
                Order(4, inWindow.AddMonths(1), "Done", hidden: false, channel: "Paid Search")); // outside
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var controller = new AdminOrderSourcesController(db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            var result = await controller.GetOrderSources(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), null);
            var report = Assert.IsType<OrderSourcesReportDto>(
                Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result).Value);

            Assert.Equal(1, report.TotalOrders);
            Assert.Equal(1, report.Sources.Single(s => s.Key == "paid").Orders);
        }

        // ── Capture at booking ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheBookingStoresTheRawSignals_WithoutQueryStrings()
        {
            using var db = Db();
            SeedBookable(db);
            var dto = Booking();
            dto.Attribution = new AttributionDto
            {
                Channel = "Organic Search", Source = "www.google.com", Medium = "referral", Campaign = "(none)",
                ClickIdType = "gbraid", ClickId = "0AAAAA-xyz",
                Referrer = "https://www.google.com/search?q=cleaning+near+me",
                LandingPage = "/deep-cleaning?gbraid=0AAAAA-xyz&utm_source=google"
            };

            var order = await new BookingCreationService(db, new NoLoyalty(), null!, NullLogger<BookingCreationService>.Instance)
                .CreateOrderAsync(dto, 10, allowCustomPricing: false);

            Assert.Equal("gbraid", order.AcquisitionClickIdType);
            Assert.Equal("0AAAAA-xyz", order.AcquisitionClickId);
            Assert.Equal("https://www.google.com/search", order.AcquisitionReferrer);
            Assert.Equal("/deep-cleaning", order.AcquisitionLandingPage);
            // An ad click is paid search whatever the browser concluded.
            Assert.Equal("Paid Search", order.AcquisitionChannel);
        }

        [Fact]
        public async Task AnUnknownClickIdTypeStoresNoClickId()
        {
            using var db = Db();
            SeedBookable(db);
            var dto = Booking();
            dto.Attribution = new AttributionDto { Channel = "Social", ClickIdType = "fbclid", ClickId = "abc" };

            var order = await new BookingCreationService(db, new NoLoyalty(), null!, NullLogger<BookingCreationService>.Instance)
                .CreateOrderAsync(dto, 10, allowCustomPricing: false);

            Assert.Null(order.AcquisitionClickIdType);
            Assert.Null(order.AcquisitionClickId);
            Assert.Equal("Social", order.AcquisitionChannel);
        }

        [Fact]
        public async Task AnAdminBookingKeepsNoneOfTheAdminsBrowserSignals()
        {
            using var db = Db();
            SeedBookable(db);
            var dto = Booking();
            dto.Attribution = new AttributionDto { Channel = "Paid Search", ClickIdType = "gclid", ClickId = "x", Referrer = "https://www.google.com/" };

            var order = await new BookingCreationService(db, new NoLoyalty(), null!, NullLogger<BookingCreationService>.Instance)
                .CreateOrderAsync(dto, 10, false, new BookingCreationOptions { BookedByAdminUserId = 2 });

            Assert.Equal(AcquisitionChannels.AdminManual, order.AcquisitionChannel);
            Assert.Null(order.AcquisitionClickIdType);
            Assert.Null(order.AcquisitionClickId);
            Assert.Null(order.AcquisitionReferrer);
            Assert.Null(order.AcquisitionLandingPage);
        }

        // ── Helpers ────────────────────────────────────────────────────────────────────────

        private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"order-sources-{Guid.NewGuid()}", b => b.EnableNullChecks(false))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

        private static Order Order(int id, DateTime date, string status, bool hidden, string channel) => new()
        {
            Id = id, UserId = 10, ServiceTypeId = 1, ServiceDate = date, Status = status, IsHidden = hidden,
            AcquisitionChannel = channel, SubTotal = 200m, Tax = 17.75m, Total = 217.75m,
            ContactFirstName = "C", ContactLastName = "L", ContactEmail = "c@example.invalid", ContactPhone = "2125550100",
            ServiceAddress = "1 St", City = "Brooklyn", State = "NY", ZipCode = "11201",
            OrderDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        };

        private static void SeedBookable(ApplicationDbContext db)
        {
            db.ServiceTypes.Add(new ServiceType
            {
                Id = 1, Name = "Residential Cleaning", BasePrice = 90m, TimeDuration = 120m, MinimumPrice = 130m, IsActive = true
            });
            db.Users.Add(new User { Id = 10, FirstName = "C", LastName = "L", Email = "c@example.invalid", Role = UserRole.Customer, IsActive = true });
            db.SaveChanges();
        }

        private static CreateBookingDto Booking() => new()
        {
            ServiceTypeId = 1,
            ServiceDate = DateTime.UtcNow.Date.AddDays(7),
            ServiceTime = "10:00",
            ContactFirstName = "Test", ContactLastName = "Booking",
            ContactEmail = "test@example.invalid", ContactPhone = "5551234567",
            ServiceAddress = "1 Test St", City = "Brooklyn", State = "New York", ZipCode = "11201",
            EntryMethod = "Someone will be home",
            Services = new List<BookingServiceDto>(),
            ExtraServices = new List<BookingExtraServiceDto>()
        };

        private sealed class NoLoyalty : ILoyaltyDiscountService
        {
            public Task<(decimal amount, decimal percentage)> CalculateForOrderAsync(int userId, decimal subTotal) => Task.FromResult((0m, 0m));
            public (decimal loyaltyAmount, decimal loyaltyPercentage, decimal subscriptionAmount, decimal promoAmount)
                ResolveStacking(decimal loyaltyCandidateAmount, decimal loyaltyCandidatePercentage, decimal subscriptionAmount, decimal promoAmount)
                => (loyaltyCandidateAmount, loyaltyCandidatePercentage, subscriptionAmount, promoAmount);
            public Task ApplyToOrderAsync(int orderId) => Task.CompletedTask;
            public Task ReverseFromOrderAsync(int orderId) => Task.CompletedTask;
            public Task<LoyaltyDiscountDto> GetForUserAsync(int userId) => throw new NotSupportedException();
            public Task<LoyaltyDiscountDto> SetManualAsync(int userId, decimal percentage, int adminUserId, bool isLifetime = false) => throw new NotSupportedException();
            public Task<LoyaltyDiscountDto> ClearAsync(int userId, int adminUserId) => throw new NotSupportedException();
        }
    }
}

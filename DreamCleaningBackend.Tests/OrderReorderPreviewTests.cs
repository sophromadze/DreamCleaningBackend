using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// "RECREATE THIS ORDER" — the preview an admin reads before recreating a past job.
    ///
    /// The flow exists because a past order is not a reliable template for a new one. Catalogue
    /// prices move, services get retired, and every discount the original order used is either
    /// spent (gift card, bubble points, special offer), gone (first-time flag), or expired (promo
    /// code). Copying any of that forward would either fail at create time or hand out a discount
    /// nobody is entitled to.
    ///
    /// So the rule is: NOTHING to do with money is carried over. The prefill arrives with every
    /// discount slot empty, and each slot the source order used is REPORTED instead, with a reason
    /// the admin can read out to the customer. The only two that may come back are loyalty and the
    /// recurring-plan discount — the two the customer might still genuinely be entitled to — and
    /// even those are an explicit opt-in, because applying loyalty also CONSUMES it.
    ///
    /// These tests run the real service against a real ApplicationDbContext and the real shared
    /// calculator: "what it costs today" has to be produced by the code that will charge it, or
    /// the preview is a second opinion rather than a preview.
    /// </summary>
    public class OrderReorderPreviewTests : IDisposable
    {
        private const int ResidentialTypeId = 1;
        private const int BedroomsServiceId = 10;
        private const int BathroomsServiceId = 11;
        private const int RetiredServiceId = 12;
        private const int WindowsExtraId = 20;
        private const int RetiredExtraId = 21;
        private const int CustomerId = 100;
        private const int WeeklyPlanId = 5;
        private const int SourceOrderId = 900;

        // What the catalogue charged when the source order was booked.
        private const decimal OriginalBedroomCost = 22.50m;
        private const decimal OriginalBathroomCost = 22.50m;
        private const decimal OriginalWindowsPrice = 12m;

        private readonly ApplicationDbContext _context;

        public OrderReorderPreviewTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"reorder-preview-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new ApplicationDbContext(options);
            Seed();
        }

        public void Dispose() => _context.Dispose();

        private void Seed()
        {
            _context.ServiceTypes.Add(new ServiceType
            {
                Id = ResidentialTypeId,
                Name = "Residential Cleaning",
                BasePrice = 90m,
                TimeDuration = 120m,
                MinimumPrice = 0m,
                IsCustom = false,
                IsActive = true
            });

            _context.Services.AddRange(
                new Service
                {
                    Id = BedroomsServiceId, ServiceTypeId = ResidentialTypeId,
                    Name = "Bedrooms", ServiceKey = "bedrooms",
                    Cost = OriginalBedroomCost, TimeDuration = 30m,
                    InputType = "dropdown", MinValue = 0, MaxValue = 10, StepValue = 1,
                    IsActive = true
                },
                new Service
                {
                    Id = BathroomsServiceId, ServiceTypeId = ResidentialTypeId,
                    Name = "Bathrooms", ServiceKey = "bathrooms",
                    Cost = OriginalBathroomCost, TimeDuration = 30m,
                    InputType = "dropdown", MinValue = 0, MaxValue = 10, StepValue = 1,
                    IsActive = true
                },
                new Service
                {
                    Id = RetiredServiceId, ServiceTypeId = ResidentialTypeId,
                    Name = "Balcony", ServiceKey = "balcony",
                    Cost = 15m, TimeDuration = 15m,
                    InputType = "dropdown", MinValue = 0, MaxValue = 3, StepValue = 1,
                    // Deactivated since the source order was booked.
                    IsActive = false
                });

            _context.ExtraServices.AddRange(
                new ExtraService
                {
                    Id = WindowsExtraId, Name = "Windows",
                    Price = OriginalWindowsPrice, Duration = 20m, PriceMultiplier = 1m,
                    HasQuantity = true, IsActive = true, IsAvailableForAll = true
                },
                new ExtraService
                {
                    Id = RetiredExtraId, Name = "Balcony Wash",
                    Price = 25m, Duration = 30m, PriceMultiplier = 1m,
                    HasQuantity = true, IsActive = false, IsAvailableForAll = true
                });

            _context.Subscriptions.Add(new Subscription
            {
                Id = WeeklyPlanId, Name = "Weekly",
                DiscountPercentage = 15m, SubscriptionDays = 7, IsActive = true
            });

            _context.Users.Add(new User
            {
                Id = CustomerId,
                Email = "customer@example.com",
                FirstName = "Cus", LastName = "Tomer",
                PasswordHash = "x",
                Phone = "5551234567",
                Role = UserRole.Customer,
                // Already used up — recreating must not offer the first-time discount again.
                FirstTimeOrder = false
            });

            _context.SaveChanges();
        }

        /// <summary>
        /// A past order for 2 bedrooms + 1 bathroom + windows, priced at the catalogue values in
        /// the seed. Costs are written the way AddOrderLinesFromQuote wrote them at the time.
        /// </summary>
        private Order SeedSourceOrder(Action<Order>? customise = null)
        {
            var order = new Order
            {
                Id = SourceOrderId,
                UserId = CustomerId,
                ServiceTypeId = ResidentialTypeId,
                ServiceDate = new DateTime(2026, 3, 14),
                ServiceTime = new TimeSpan(10, 0, 0),
                OrderDate = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
                Status = OrderStatuses.Done,
                EntryMethod = "Doorman",
                ContactFirstName = "Cus", ContactLastName = "Tomer",
                ContactEmail = "customer@example.com", ContactPhone = "5551234567",
                ServiceAddress = "1 Main St", City = "Manhattan", State = "New York", ZipCode = "10001",
                BedroomsQuantity = 2, BathroomsQuantity = 1,
                SubTotal = 90m + (2 * OriginalBedroomCost) + OriginalBathroomCost + OriginalWindowsPrice,
                Tax = 0m, Total = 0m, Tips = 0m,
                TotalDuration = 220m, MaidsCount = 1,
                OrderServices = new List<Models.OrderService>
                {
                    new Models.OrderService
                    {
                        ServiceId = BedroomsServiceId, Quantity = 2,
                        Cost = 2 * OriginalBedroomCost, Duration = 60m, PriceMultiplier = 1m
                    },
                    new Models.OrderService
                    {
                        ServiceId = BathroomsServiceId, Quantity = 1,
                        Cost = OriginalBathroomCost, Duration = 30m, PriceMultiplier = 1m
                    }
                },
                OrderExtraServices = new List<OrderExtraService>
                {
                    new OrderExtraService
                    {
                        ExtraServiceId = WindowsExtraId, Quantity = 1, Hours = 0,
                        Cost = OriginalWindowsPrice, Duration = 20m
                    }
                }
            };

            // Tax and total consistent with the lines above, so the totals comparison is honest.
            var totals = OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
            {
                SubTotal = order.SubTotal
            });
            order.Tax = totals.Tax;
            order.Total = totals.Total;

            customise?.Invoke(order);

            _context.Orders.Add(order);
            _context.SaveChanges();
            return order;
        }

        private OrderReorderPreviewService MakeService(decimal loyaltyPercentage = 0m) =>
            new OrderReorderPreviewService(_context, new StubLoyaltyDiscountService(loyaltyPercentage));

        // ── Nothing changed ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task UnchangedCatalogue_ReportsNoChangesAndTheSameTotal()
        {
            SeedSourceOrder();

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            Assert.Empty(preview.LineChanges);
            Assert.Empty(preview.Unavailable);
            Assert.Empty(preview.Discounts);
            Assert.Equal(preview.Original.Total, preview.Recreated.Total);
            // The screen renders one reassuring line instead of four empty sections.
            Assert.False(preview.HasChanges);
        }

        // ── Catalogue prices moved ─────────────────────────────────────────────────────────

        [Fact]
        public async Task RaisedServicePrice_IsReportedWithBothSidesAndMovesTheTotal()
        {
            SeedSourceOrder();

            // The shop raised bedrooms after this order was booked.
            var bedrooms = _context.Services.First(s => s.Id == BedroomsServiceId);
            bedrooms.Cost = 30m;
            _context.SaveChanges();

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            var change = Assert.Single(preview.LineChanges.Where(c => c.Id == BedroomsServiceId));
            Assert.Equal("Service", change.Kind);
            Assert.Equal(2, change.Quantity);
            // The "then" side is the persisted line total, never a re-derivation.
            Assert.Equal(2 * OriginalBedroomCost, change.OriginalCost);
            Assert.Equal(60m, change.NewCost);

            Assert.True(preview.HasChanges);
            Assert.True(preview.Recreated.SubTotal > preview.Original.SubTotal);
        }

        [Fact]
        public async Task UnchangedLines_AreNotListedAsChanges()
        {
            SeedSourceOrder();

            var bedrooms = _context.Services.First(s => s.Id == BedroomsServiceId);
            bedrooms.Cost = 30m;
            _context.SaveChanges();

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            // Bathrooms and Windows never moved, so they must not appear on a "what changed" screen.
            Assert.DoesNotContain(preview.LineChanges, c => c.Id == BathroomsServiceId);
            Assert.DoesNotContain(preview.LineChanges, c => c.Kind == "Extra");
        }

        [Fact]
        public async Task RaisedExtraPrice_IsReported()
        {
            SeedSourceOrder();

            var windows = _context.ExtraServices.First(e => e.Id == WindowsExtraId);
            windows.Price = 20m;
            _context.SaveChanges();

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            var change = Assert.Single(preview.LineChanges.Where(c => c.Kind == "Extra"));
            Assert.Equal(WindowsExtraId, change.Id);
            Assert.Equal(OriginalWindowsPrice, change.OriginalCost);
            Assert.Equal(20m, change.NewCost);
        }

        // ── Lines that no longer exist ─────────────────────────────────────────────────────

        [Fact]
        public async Task DeactivatedService_IsReportedAndDroppedFromThePrefill()
        {
            SeedSourceOrder(o => o.OrderServices.Add(new Models.OrderService
            {
                ServiceId = RetiredServiceId, Quantity = 1, Cost = 15m, Duration = 15m, PriceMultiplier = 1m
            }));

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            var gone = Assert.Single(preview.Unavailable.Where(u => u.Id == RetiredServiceId));
            Assert.Equal("Service", gone.Kind);
            Assert.Equal(15m, gone.OriginalCost);
            Assert.False(string.IsNullOrWhiteSpace(gone.Reason));

            // Dropped, not silently re-priced: the recreated order cannot contain it.
            Assert.DoesNotContain(preview.Prefill.Services, s => s.ServiceId == RetiredServiceId);
        }

        [Fact]
        public async Task DeactivatedExtra_IsReportedAndDroppedFromThePrefill()
        {
            SeedSourceOrder(o => o.OrderExtraServices.Add(new OrderExtraService
            {
                ExtraServiceId = RetiredExtraId, Quantity = 1, Hours = 0, Cost = 25m, Duration = 30m
            }));

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            var gone = Assert.Single(preview.Unavailable.Where(u => u.Id == RetiredExtraId));
            Assert.Equal("Extra", gone.Kind);
            Assert.DoesNotContain(preview.Prefill.ExtraServices, e => e.ExtraServiceId == RetiredExtraId);
        }

        // ── Discounts: none carried over, all explained ────────────────────────────────────

        [Fact]
        public async Task ExpiredPromoCode_IsReportedWithItsExpiryAndNotCarriedOver()
        {
            _context.PromoCodes.Add(new PromoCode
            {
                Id = 1, Code = "SPRING20", IsPercentage = true, DiscountValue = 20m,
                // Relative, not a fixed calendar date: a hardcoded 2026 expiry is a test that
                // silently changes meaning as the clock moves past it.
                IsActive = true, ValidTo = DateTime.UtcNow.AddMonths(-1)
            });
            _context.SaveChanges();

            SeedSourceOrder(o =>
            {
                o.PromoCode = "SPRING20";
                o.DiscountAmount = 35m;
            });

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            var slot = Assert.Single(preview.Discounts.Where(d => d.Kind == "PromoCode"));
            Assert.Equal(35m, slot.OriginalAmount);
            Assert.Equal(0m, slot.AvailableAmount);
            Assert.False(slot.CanReapply);
            Assert.Contains("expired", slot.Reason, StringComparison.OrdinalIgnoreCase);

            Assert.Null(preview.Prefill.PromoCode);
        }

        [Fact]
        public async Task StillValidPromoCode_IsStillNotCarriedOver()
        {
            _context.PromoCodes.Add(new PromoCode
            {
                Id = 2, Code = "ALWAYS10", IsPercentage = true, DiscountValue = 10m, IsActive = true
            });
            _context.SaveChanges();

            SeedSourceOrder(o => { o.PromoCode = "ALWAYS10"; o.DiscountAmount = 18m; });

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            var slot = Assert.Single(preview.Discounts.Where(d => d.Kind == "PromoCode"));
            // Valid, and still not automatic — the admin is told it is theirs to add if they want it.
            Assert.False(slot.CanReapply);
            Assert.Null(preview.Prefill.PromoCode);
            Assert.Equal(0m, preview.Recreated.DiscountAmount);
        }

        [Fact]
        public async Task SpentGiftCard_IsReportedAsEmptyAndNotCarriedOver()
        {
            _context.GiftCards.Add(new GiftCard
            {
                Id = 1, Code = "AAAA-BBBB-CCCC",
                OriginalAmount = 100m, CurrentBalance = 0m,
                RecipientName = "Cus", RecipientEmail = "customer@example.com",
                SenderName = "Friend", SenderEmail = "friend@example.com"
            });
            _context.SaveChanges();

            SeedSourceOrder(o =>
            {
                o.GiftCardCode = "AAAA-BBBB-CCCC";
                o.GiftCardAmountUsed = 100m;
            });

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            var slot = Assert.Single(preview.Discounts.Where(d => d.Kind == "GiftCard"));
            Assert.Equal(100m, slot.OriginalAmount);
            Assert.False(slot.CanReapply);
            Assert.Contains("no balance left", slot.Reason, StringComparison.OrdinalIgnoreCase);

            Assert.Null(preview.Prefill.GiftCardCode);
            Assert.Equal(0m, preview.Prefill.GiftCardAmountToUse);
        }

        [Fact]
        public async Task FirstTimeDiscount_IsReportedAsUnavailableOnceTheFlagIsGone()
        {
            SeedSourceOrder(o => { o.PromoCode = "firstUse"; o.DiscountAmount = 27m; });

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            var slot = Assert.Single(preview.Discounts.Where(d => d.Kind == "FirstTime"));
            Assert.False(slot.CanReapply);
            Assert.Contains("no longer a first-time customer", slot.Reason, StringComparison.OrdinalIgnoreCase);

            // The first-time marker rides in the PromoCode column and must not travel with it.
            Assert.Null(preview.Prefill.PromoCode);
            // It has its own slot, so it must not also be reported as a promo code.
            Assert.DoesNotContain(preview.Discounts, d => d.Kind == "PromoCode");
        }

        [Fact]
        public async Task SpentBubblePointsAndRewardBalance_AreReportedAndNotCarriedOver()
        {
            SeedSourceOrder(o =>
            {
                o.PointsRedeemed = 500;
                o.PointsRedeemedDiscount = 5m;
                o.RewardBalanceUsed = 12m;
            });

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            Assert.Single(preview.Discounts.Where(d => d.Kind == "BubblePoints"));
            Assert.Single(preview.Discounts.Where(d => d.Kind == "RewardBalance"));
            Assert.All(
                preview.Discounts.Where(d => d.Kind == "BubblePoints" || d.Kind == "RewardBalance"),
                d => Assert.False(d.CanReapply));

            Assert.Equal(0, preview.Prefill.PointsToRedeem);
            Assert.False(preview.Prefill.UseCredits);
            Assert.Equal(0m, preview.Prefill.CreditsToApply);
        }

        [Fact]
        public async Task ConsumedSpecialOffer_IsReportedAndNotCarriedOver()
        {
            SeedSourceOrder(o =>
            {
                o.UserSpecialOfferId = 77;
                o.SpecialOfferName = "Summer Treat";
                o.DiscountAmount = 20m;
            });

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            var slot = Assert.Single(preview.Discounts.Where(d => d.Kind == "SpecialOffer"));
            Assert.Contains("Summer Treat", slot.Label);
            Assert.False(slot.CanReapply);
            Assert.Null(preview.Prefill.UserSpecialOfferId);
            Assert.Null(preview.Prefill.SpecialOfferId);
        }

        // ── The two the customer may still be entitled to ──────────────────────────────────

        [Fact]
        public async Task LiveLoyaltyDiscount_IsOfferedAsAnOptInAndIsNotInTheDefaultTotal()
        {
            SeedSourceOrder();

            var preview = await MakeService(loyaltyPercentage: 10m)
                .BuildAsync(SourceOrderId, allowCustomPricing: false);

            var slot = Assert.Single(preview.Discounts.Where(d => d.Kind == "Loyalty"));
            Assert.True(slot.CanReapply);
            Assert.True(slot.AvailableAmount > 0);

            // Available is not the same as applied: the default recreated total takes nothing off.
            Assert.Equal(0m, preview.Recreated.LoyaltyDiscountAmount);
            Assert.Equal(
                OrderPricingCalculator.CalculateTotals(new OrderPricingCalculator.TotalsInput
                {
                    SubTotal = preview.Recreated.SubTotal
                }).Total,
                preview.Recreated.Total);
        }

        [Fact]
        public async Task LapsedPlan_ReportsTheDiscountAsGoneRatherThanOfferingIt()
        {
            SeedSourceOrder(o =>
            {
                o.SubscriptionId = WeeklyPlanId;
                o.SubscriptionDiscountAmount = 22.50m;
            });

            // The customer is on no plan today.
            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            var slot = Assert.Single(preview.Discounts.Where(d => d.Kind == "Subscription"));
            Assert.Equal(22.50m, slot.OriginalAmount);
            Assert.False(slot.CanReapply);
            Assert.Equal(0m, slot.AvailableAmount);

            // The PLAN itself still travels: it is what makes the recreated order count as
            // recurring in the CRM. Only its discount is withheld.
            Assert.Equal(WeeklyPlanId, preview.Prefill.SubscriptionId);
        }

        [Fact]
        public async Task ActivePlan_IsOfferedAsAnOptIn()
        {
            var customer = _context.Users.First(u => u.Id == CustomerId);
            customer.SubscriptionId = WeeklyPlanId;
            customer.SubscriptionExpiryDate = DateTime.UtcNow.AddMonths(6);
            _context.SaveChanges();

            SeedSourceOrder(o =>
            {
                o.SubscriptionId = WeeklyPlanId;
                o.SubscriptionDiscountAmount = 22.50m;
            });

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            var slot = Assert.Single(preview.Discounts.Where(d => d.Kind == "Subscription"));
            Assert.True(slot.CanReapply);
            Assert.Equal(
                OrderPricingCalculator.Round2(preview.Recreated.SubTotal * 0.15m),
                slot.AvailableAmount);
            // Still not applied by default.
            Assert.Equal(0m, preview.Recreated.SubscriptionDiscountAmount);
        }

        // ── The prefill itself ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task Prefill_ReproducesTheJobButNoneOfTheMoneySlots()
        {
            SeedSourceOrder(o =>
            {
                o.PromoCode = "SPRING20";
                o.DiscountAmount = 35m;
                o.GiftCardCode = "AAAA-BBBB-CCCC";
                o.GiftCardAmountUsed = 40m;
                o.PointsRedeemed = 200;
                o.PointsRedeemedDiscount = 2m;
                o.RewardBalanceUsed = 3m;
                o.SpecialInstructions = "Cat is friendly";
                o.Tips = 15m;
            });

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);
            var prefill = preview.Prefill;

            // The job itself comes across intact.
            Assert.Equal(ResidentialTypeId, prefill.ServiceTypeId);
            Assert.Equal(2, prefill.Services.First(s => s.ServiceId == BedroomsServiceId).Quantity);
            Assert.Equal(1, prefill.Services.First(s => s.ServiceId == BathroomsServiceId).Quantity);
            Assert.Contains(prefill.ExtraServices, e => e.ExtraServiceId == WindowsExtraId);
            Assert.Equal("Doorman", prefill.EntryMethod);
            Assert.Equal("Cat is friendly", prefill.SpecialInstructions);
            Assert.Equal("1 Main St", prefill.ServiceAddress);
            Assert.Equal("10:00", prefill.ServiceTime);
            // Tips are part of the job as booked, not a discount.
            Assert.Equal(15m, prefill.Tips);

            // Every money slot is empty, so a client that skipped the preview screen and posted
            // the prefill straight back still cannot resurrect a stale discount.
            Assert.Null(prefill.PromoCode);
            Assert.Null(prefill.GiftCardCode);
            Assert.Equal(0m, prefill.GiftCardAmountToUse);
            Assert.Null(prefill.UserSpecialOfferId);
            Assert.Null(prefill.SpecialOfferId);
            Assert.Equal(0, prefill.PointsToRedeem);
            Assert.False(prefill.UseCredits);
            Assert.Equal(0m, prefill.CreditsToApply);
            Assert.Equal(0m, prefill.DiscountAmount);
            Assert.Equal(0m, prefill.SubscriptionDiscountAmount);
            Assert.Equal(0m, prefill.LoyaltyDiscountAmount);
            Assert.Null(prefill.ReferralCode);
        }

        [Fact]
        public async Task DeletedSavedAddress_BecomesAPlainTypedAddress()
        {
            // ApartmentId points at a saved address that has since been deleted. Carrying it into
            // a new order is an FK violation at insert time, so it must not survive the prefill.
            SeedSourceOrder(o => o.ApartmentId = 4242);

            var preview = await MakeService().BuildAsync(SourceOrderId, allowCustomPricing: false);

            Assert.Null(preview.Prefill.ApartmentId);
            Assert.Equal("1 Main St", preview.Prefill.ServiceAddress);
        }

        [Fact]
        public async Task MissingOrder_Throws404Shaped()
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => MakeService().BuildAsync(sourceOrderId: 999999, allowCustomPricing: false));
        }

        /// <summary>Loyalty is a live entitlement on the USER, so the preview asks the real service
        /// for it. This stub gives a fixed percentage of whatever subtotal it is handed.</summary>
        private sealed class StubLoyaltyDiscountService : ILoyaltyDiscountService
        {
            private readonly decimal _percentage;

            public StubLoyaltyDiscountService(decimal percentage) => _percentage = percentage;

            public Task<(decimal amount, decimal percentage)> CalculateForOrderAsync(int userId, decimal subTotal)
                => Task.FromResult((
                    OrderPricingCalculator.Round2(subTotal * _percentage / 100m),
                    _percentage));

            public (decimal loyaltyAmount, decimal loyaltyPercentage, decimal subscriptionAmount, decimal promoAmount)
                ResolveStacking(decimal loyaltyCandidateAmount, decimal loyaltyCandidatePercentage,
                                decimal subscriptionAmount, decimal promoAmount)
                => (loyaltyCandidateAmount, loyaltyCandidatePercentage, subscriptionAmount, promoAmount);

            public Task ApplyToOrderAsync(int orderId) => Task.CompletedTask;
            public Task ReverseFromOrderAsync(int orderId) => Task.CompletedTask;
            public Task<LoyaltyDiscountDto> GetForUserAsync(int userId) => throw new NotSupportedException();
            public Task<LoyaltyDiscountDto> SetManualAsync(int userId, decimal percentage, int adminUserId) => throw new NotSupportedException();
            public Task<LoyaltyDiscountDto> ClearAsync(int userId, int adminUserId) => throw new NotSupportedException();
        }
    }
}

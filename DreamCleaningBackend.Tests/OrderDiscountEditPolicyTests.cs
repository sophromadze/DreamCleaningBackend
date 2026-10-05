using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// <see cref="OrderDiscountEditPolicy"/> — discounts are SuperAdmin-only in the admin order
    /// editor, but an Admin moving the SUBTOTAL still re-scales them, and that must not be refused.
    /// </summary>
    public class OrderDiscountEditPolicyTests
    {
        // A $400 order with a $100 promo (25%) and a $40 subscription discount (10%).
        private const decimal SubTotal = 400m;
        private const decimal Promo = 100m;
        private const decimal Subscription = 40m;

        private static bool Keeps(SuperAdminUpdateOrderDto dto) =>
            OrderDiscountEditPolicy.KeepsDiscounts(SubTotal, Promo, Subscription, dto);

        [Fact]
        public void OnlyASuperAdminMayEditDiscounts()
        {
            Assert.True(OrderDiscountEditPolicy.MayEditDiscounts(UserRole.SuperAdmin));
            Assert.False(OrderDiscountEditPolicy.MayEditDiscounts(UserRole.Admin));
            Assert.False(OrderDiscountEditPolicy.MayEditDiscounts(UserRole.Moderator));
        }

        [Fact]
        public void AnEditThatLeavesTheDiscountsAloneIsAccepted()
        {
            Assert.True(Keeps(new SuperAdminUpdateOrderDto { SubTotal = SubTotal, DiscountAmount = Promo, SubscriptionDiscountAmount = Subscription }));
            // Not part of the edit at all.
            Assert.True(Keeps(new SuperAdminUpdateOrderDto { ContactFirstName = "Ana" }));
        }

        [Fact]
        public void DiscountsRescaledWithANewSubtotalAreAccepted()
        {
            // Subtotal up to $480: 25% -> $120, 10% -> $48. The discount followed the price.
            Assert.True(Keeps(new SuperAdminUpdateOrderDto { SubTotal = 480m, DiscountAmount = 120m, SubscriptionDiscountAmount = 48m }));
            // ...and down, with the typed-Total path landing a cent or two off the pure ratio.
            Assert.True(Keeps(new SuperAdminUpdateOrderDto { SubTotal = 333.33m, DiscountAmount = 83.34m, SubscriptionDiscountAmount = 33.32m }));
        }

        [Fact]
        public void ATypedDiscountIsRefused()
        {
            // Same subtotal, promo raised by hand.
            Assert.False(Keeps(new SuperAdminUpdateOrderDto { SubTotal = SubTotal, DiscountAmount = 150m, SubscriptionDiscountAmount = Subscription }));
            // Subscription discount wiped.
            Assert.False(Keeps(new SuperAdminUpdateOrderDto { SubTotal = SubTotal, DiscountAmount = Promo, SubscriptionDiscountAmount = 0m }));
            // Subtotal moved AND the promo set to something other than its re-scaled figure.
            Assert.False(Keeps(new SuperAdminUpdateOrderDto { SubTotal = 480m, DiscountAmount = 100m, SubscriptionDiscountAmount = 48m }));
        }

        [Fact]
        public void ADiscountCannotBeAddedToAnOrderThatHadNone()
        {
            Assert.False(OrderDiscountEditPolicy.KeepsDiscounts(
                SubTotal, 0m, 0m, new SuperAdminUpdateOrderDto { SubTotal = SubTotal, DiscountAmount = 20m }));
        }

        [Fact]
        public void WithNoSubtotalToScaleAgainstOnlyTheStoredFigureIsAccepted()
        {
            Assert.True(OrderDiscountEditPolicy.KeepsDiscounts(
                0m, 15m, 0m, new SuperAdminUpdateOrderDto { SubTotal = 200m, DiscountAmount = 15m }));
            Assert.False(OrderDiscountEditPolicy.KeepsDiscounts(
                0m, 15m, 0m, new SuperAdminUpdateOrderDto { SubTotal = 200m, DiscountAmount = 30m }));
        }
    }
}

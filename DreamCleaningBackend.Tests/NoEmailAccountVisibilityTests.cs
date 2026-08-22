using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// "THIS CUSTOMER HAS NO EMAIL" MUST BE VISIBLE BEFORE THE SEND, NOT AFTER IT.
    ///
    /// An order's ContactEmail is FROZEN at booking time; the account's address can change (or
    /// never exist). A no-email cash account therefore routinely owns an order that displays a
    /// perfectly real-looking email — which is exactly how admins came to read a skipped send as
    /// a bug. OrderDtoMapper carries the account's real situation onto the details DTO so the
    /// admin panel can warn where the send buttons are.
    ///
    /// What must stay true:
    ///   - the warning flag follows the ACCOUNT, never the order's frozen contact email,
    ///   - a User that wasn't Included reads as "unknown" (false), never as "has no email",
    ///   - the notification target names the address the sender would really use.
    /// </summary>
    public class NoEmailAccountVisibilityTests
    {
        private static User NoEmailUser() => new User
        {
            Id = 7,
            IsNoEmailUser = true,
            Email = NoEmailHelper.GeneratePlaceholder()
        };

        private static User NormalUser(string email) => new User { Id = 8, Email = email };

        private static Order OrderFor(User? user, string contactEmail) => new Order
        {
            Id = 42,
            UserId = user?.Id ?? 8,
            User = user,
            ContactEmail = contactEmail,
            Status = "Active"
        };

        [Fact]
        public void NoEmailAccount_IsFlagged_EvenWhenTheOrderCarriesARealAddress()
        {
            var dto = OrderDtoMapper.ToOrderDto(OrderFor(NoEmailUser(), "customer@example.com"));

            Assert.True(dto.CustomerHasNoAccountEmail);
            Assert.Null(dto.CustomerAccountEmail);
            // The reminder path still uses the order's own address, and the panel says so.
            Assert.Equal("customer@example.com", dto.NotificationEmailTarget);
        }

        [Fact]
        public void NoEmailAccount_WithNoOrderAddress_HasNowhereToEmail()
        {
            var dto = OrderDtoMapper.ToOrderDto(OrderFor(NoEmailUser(), ""));

            Assert.True(dto.CustomerHasNoAccountEmail);
            Assert.Null(dto.NotificationEmailTarget);
        }

        [Fact]
        public void PlaceholderContactEmail_FallsBackToTheAccountAddress()
        {
            // An order transferred from a no-email account keeps that account's placeholder as its
            // contact email; the only routable address left is the new owner's.
            var order = OrderFor(NormalUser("new.owner@example.com"), NoEmailHelper.GeneratePlaceholder());
            var dto = OrderDtoMapper.ToOrderDto(order);

            Assert.False(dto.CustomerHasNoAccountEmail);
            Assert.Equal("new.owner@example.com", dto.NotificationEmailTarget);
        }

        [Fact]
        public void NormalAccount_IsNeverFlagged()
        {
            var dto = OrderDtoMapper.ToOrderDto(OrderFor(NormalUser("real@example.com"), "real@example.com"));

            Assert.False(dto.CustomerHasNoAccountEmail);
            Assert.Equal("real@example.com", dto.CustomerAccountEmail);
        }

        [Fact]
        public void UserNotLoaded_ReadsAsUnknown_NotAsMissingEmail()
        {
            // Customer-facing endpoints don't Include the User. "Unknown" must not render a
            // warning telling an admin the customer has no email.
            var dto = OrderDtoMapper.ToOrderDto(OrderFor(null, "customer@example.com"));

            Assert.False(dto.CustomerHasNoAccountEmail);
            Assert.Null(dto.CustomerAccountEmail);
            Assert.Equal("customer@example.com", dto.NotificationEmailTarget);
        }
    }
}

using System;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE PAYMENT-PAGE CONSENT GATE.
    ///
    /// Every self-service booking is blocked at /booking until the customer ticks SMS,
    /// cancellation-fee and terms. An admin using create-for-user ticks those same boxes on the
    /// customer's behalf during a phone call — so for admin-created orders nobody but the admin
    /// ever agreed to anything. Those orders re-ask on the payment page, and the gate is enforced
    /// where it can't be walked around: create-payment-intent refuses, so there is no client
    /// secret and no card charge is possible.
    ///
    /// What must stay true:
    ///   - customer-booked orders are NEVER re-asked (they already consented on /booking),
    ///   - an admin-created order stops asking the moment consent is recorded,
    ///   - a paid order is never gated — that would block the pending-additional-payment flow,
    ///     which is a follow-up charge on an order whose consents already exist.
    /// </summary>
    public class PaymentConsentPolicyTests
    {
        private static Order MakeOrder(
            int? bookedByAdminUserId = null,
            bool isPaid = false,
            DateTime? consentAcceptedAt = null,
            int? manualPaymentRecordedByUserId = null,
            DateTime? manualPaymentRecordedAt = null)
        {
            var orderDate = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
            return new Order
            {
                Id = 1,
                OrderDate = orderDate,
                IsPaid = isPaid,
                PaymentConsentAcceptedAt = consentAcceptedAt,
                BookedByAdminUserId = bookedByAdminUserId,
                ManualPaymentRecordedByUserId = manualPaymentRecordedByUserId,
                ManualPaymentRecordedAt = manualPaymentRecordedAt
            };
        }

        [Fact]
        public void AdminCreatedUnpaidOrder_RequiresConsent()
        {
            Assert.True(PaymentConsentPolicy.RequiresConsent(MakeOrder(bookedByAdminUserId: 7)));
        }

        [Fact]
        public void CustomerBookedOrder_NeverRequiresConsent()
        {
            // Consents were taken on /booking before this order existed.
            Assert.False(PaymentConsentPolicy.RequiresConsent(MakeOrder()));
        }

        [Fact]
        public void AdminCreatedOrder_StopsAskingOnceAccepted()
        {
            var order = MakeOrder(
                bookedByAdminUserId: 7,
                consentAcceptedAt: new DateTime(2026, 8, 2, 9, 30, 0, DateTimeKind.Utc));
            Assert.False(PaymentConsentPolicy.RequiresConsent(order));
        }

        [Fact]
        public void PaidOrder_IsNotGated_SoAdditionalPaymentsStillWork()
        {
            // A pending additional amount is charged on an already-paid order. Gating it would
            // block order edits from ever being settled.
            var order = MakeOrder(bookedByAdminUserId: 7, isPaid: true);
            Assert.False(PaymentConsentPolicy.RequiresConsent(order));
        }

        [Fact]
        public void LegacyAdminOrder_WithoutBookedByAdminColumn_StillRequiresConsent()
        {
            // Pre-2026-07 orders have no BookedByAdminUserId; IsBookedByAdmin falls back to a
            // manual payment recorded at creation time. The gate must follow that same rule.
            var orderDate = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
            var order = MakeOrder(
                manualPaymentRecordedByUserId: 3,
                manualPaymentRecordedAt: orderDate.AddMinutes(1));
            Assert.True(PaymentConsentPolicy.RequiresConsent(order));
        }

        [Fact]
        public void CustomerOrder_LaterSwitchedToCashByAnAdmin_IsNotGated()
        {
            // Admin edited an existing customer booking to cash weeks later — that must not
            // reclassify it as admin-booked (the 5-minute creation window in IsBookedByAdmin).
            var orderDate = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
            var order = MakeOrder(
                manualPaymentRecordedByUserId: 3,
                manualPaymentRecordedAt: orderDate.AddDays(14));
            Assert.False(PaymentConsentPolicy.RequiresConsent(order));
        }
    }
}

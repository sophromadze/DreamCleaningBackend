using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// AN EXISTING ORDER, EXPRESSED AS A FRESH BOOKING REQUEST.
    ///
    /// One mapping, used by both flows that re-create a job from an old one — the admin
    /// "Recreate order" preview (<c>OrderReorderPreviewService</c>) and the recurring-series
    /// generator (<c>RecurringOrderSeriesService</c>). They ask exactly the same question of an
    /// order, and two copies of the answer is how one of them quietly stops copying the floor
    /// types.
    ///
    /// <b>What it copies</b> is the reusable BOOKING CONFIGURATION: the customer's address, the
    /// service type and its priced lines, extras, bed/bath/sq.ft, property type and levels, entry
    /// method, service time, instructions, floor types, contact details and tips. The result is
    /// re-priced by the ordinary calculator, so nothing about the money travels either — only what
    /// was asked for.
    ///
    /// <b>What it deliberately does NOT copy</b> is everything transactional, and the list is the
    /// point of this class:
    ///
    ///   • the Stripe PaymentIntent, payment reference, notes and paid state
    ///   • the update/payment history and any refunds
    ///   • photos, completion timestamps, payouts, cleaner-notification state
    ///   • every discount slot — promo code, gift card, special offer, points, credits, referral —
    ///     which are left at their empty values HERE rather than in a UI, so a caller that posts
    ///     this straight to create-for-user still cannot resurrect a stale or consumed discount
    ///   • audit events
    ///
    /// The one discount-adjacent field that DOES travel is <c>SubscriptionId</c>: the recurring
    /// plan is job metadata (it is what makes the order count as recurring in the CRM), and
    /// whether it actually takes a discount is decided server-side from the customer's live
    /// subscription.
    /// </summary>
    public static class OrderBookingSnapshot
    {
        /// <summary>
        /// Builds the booking request. <paramref name="services"/> and <paramref name="extras"/>
        /// are passed in rather than read off the order so the caller can drop lines whose
        /// catalogue row has been deleted or deactivated — a decision that belongs to the caller,
        /// which has to report it.
        /// </summary>
        public static CreateBookingDto ToBookingDto(
            Order order,
            ServiceType serviceType,
            IEnumerable<Models.OrderService> services,
            IEnumerable<OrderExtraService> extras)
        {
            var dto = new CreateBookingDto
            {
                ServiceTypeId = order.ServiceTypeId,
                CustomServiceDisplayName = serviceType.IsCustom ? order.CustomServiceDisplayName : null,
                Services = services
                    .Select(s => new BookingServiceDto { ServiceId = s.ServiceId, Quantity = s.Quantity })
                    .ToList(),
                ExtraServices = extras
                    .Select(e => new BookingExtraServiceDto
                    {
                        ExtraServiceId = e.ExtraServiceId,
                        Quantity = e.Quantity,
                        Hours = e.Hours
                    })
                    .ToList(),
                // The plan the job was booked on is job metadata, not a discount: it is what makes
                // the new order count as recurring in the CRM. Whether it actually TAKES a
                // discount is decided server-side from the customer's live subscription.
                SubscriptionId = order.SubscriptionId ?? 0,
                ServiceDate = order.ServiceDate,
                ServiceTime = order.ServiceTime.ToString(@"hh\:mm"),
                EntryMethod = order.EntryMethod ?? "",
                SpecialInstructions = order.SpecialInstructions,
                ContactFirstName = order.ContactFirstName,
                ContactLastName = order.ContactLastName,
                // Empty string is not a valid [EmailAddress]; a no-email cash customer posts null.
                ContactEmail = string.IsNullOrWhiteSpace(order.ContactEmail) || NoEmailHelper.IsPlaceholder(order.ContactEmail)
                    ? null
                    : order.ContactEmail,
                ContactPhone = order.ContactPhone,
                ServiceAddress = order.ServiceAddress,
                AptSuite = order.AptSuite,
                City = order.City,
                State = order.State,
                ZipCode = order.ZipCode,
                ApartmentName = order.ApartmentName,
                Tips = order.Tips,
                BedroomsQuantity = order.BedroomsQuantity,
                BathroomsQuantity = order.BathroomsQuantity,
                PropertyType = order.PropertyType,
                LevelsQuantity = order.LevelsQuantity,
                FloorTypes = order.FloorTypes,
                FloorTypeOther = order.FloorTypeOther
            };

            if (serviceType.IsCustom)
            {
                // Custom Pricing stores the tax-INCLUSIVE amount the admin typed, split into
                // SubTotal + Tax that add back to it exactly — so recombining them recovers the
                // typed figure to the cent. Duration is stored as per-cleaner × cleaners, so the
                // form's per-cleaner field is the quotient. (A job that hit the one-hour floor
                // cannot be reversed exactly; the admin sees the number and can correct it.)
                dto.IsCustomPricing = true;
                dto.CustomAmount = order.SubTotal + order.Tax;
                dto.CustomCleaners = Math.Max(1, order.MaidsCount);
                dto.CustomDuration = order.TotalDuration / Math.Max(1, order.MaidsCount);
            }

            return dto;
        }
    }
}

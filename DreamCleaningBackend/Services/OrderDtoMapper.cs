using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for assembling the order-details DTO. Used by the user
    /// order endpoints (OrderService) and the admin order endpoints (AdminController) —
    /// a new order field added here automatically shows up in every details view.
    /// Navigation properties that weren't Included by the caller simply map to
    /// null/empty, matching the previous per-site behavior.
    /// </summary>
    public static class OrderDtoMapper
    {
        /// <param name="pointsEarned">Bubble points earned on the order — callers that
        /// show it compute the sum and pass it in; defaults to 0.</param>
        public static OrderDto ToOrderDto(Order order, int pointsEarned = 0)
        {
            return new OrderDto
            {
                Id = order.Id,
                UserId = order.UserId,
                ServiceTypeId = order.ServiceTypeId,
                // Effective name: "<label> Cleaning" for custom orders, ServiceType.Name otherwise.
                ServiceTypeName = order.GetDisplayServiceTypeName(),
                IsCustomServiceType = order.ServiceType?.IsCustom ?? false,
                CustomServiceDisplayName = order.CustomServiceDisplayName,
                OrderDate = order.OrderDate,
                ServiceDate = order.ServiceDate,
                ServiceTime = order.ServiceTime,
                Status = order.Status,
                SubTotal = order.SubTotal,
                Tax = order.Tax,
                Tips = order.Tips,
                CompanyDevelopmentTips = order.CompanyDevelopmentTips,
                Total = order.Total,
                InitialSubTotal = order.InitialSubTotal,
                InitialTax = order.InitialTax,
                InitialTips = order.InitialTips,
                InitialCompanyDevelopmentTips = order.InitialCompanyDevelopmentTips,
                InitialTotal = order.InitialTotal,
                DiscountAmount = order.DiscountAmount,
                SubscriptionDiscountAmount = order.SubscriptionDiscountAmount,
                LoyaltyDiscountAmount = order.LoyaltyDiscountAmount,
                LoyaltyDiscountPercentage = order.LoyaltyDiscountPercentage,
                PaymentMethod = order.PaymentMethod.ToString(),
                PaymentReference = order.PaymentReference,
                PaymentNotes = order.PaymentNotes,
                PromoCode = order.PromoCode,
                SpecialOfferName = GetSpecialOfferName(order.PromoCode),
                PromoCodeDetails = GetPromoCodeDetails(order.PromoCode),
                GiftCardDetails = order.GiftCardCode != null
                    ? $"{MaskGiftCardCode(order.GiftCardCode)} (${order.GiftCardAmountUsed:F2})"
                    : null,
                SubscriptionId = order.SubscriptionId,
                SubscriptionName = order.Subscription?.Name ?? "",
                GiftCardCode = order.GiftCardCode,
                GiftCardAmountUsed = order.GiftCardAmountUsed,
                PointsRedeemed = order.PointsRedeemed,
                PointsRedeemedDiscount = order.PointsRedeemedDiscount,
                RewardBalanceUsed = order.RewardBalanceUsed,
                PointsEarned = pointsEarned,
                EntryMethod = order.EntryMethod,
                SpecialInstructions = order.SpecialInstructions,
                FloorTypes = order.FloorTypes,
                FloorTypeOther = order.FloorTypeOther,
                ContactFirstName = order.ContactFirstName,
                ContactLastName = order.ContactLastName,
                ContactEmail = order.ContactEmail,
                ContactPhone = order.ContactPhone,
                ServiceAddress = order.ServiceAddress,
                AptSuite = order.AptSuite,
                City = order.City,
                State = order.State,
                ZipCode = order.ZipCode,
                TotalDuration = order.TotalDuration,
                MaidsCount = order.MaidsCount,
                BedroomsQuantity = order.BedroomsQuantity,
                BathroomsQuantity = order.BathroomsQuantity,
                IsPaid = order.IsPaid,
                PaidAt = order.PaidAt,
                CleanerHourlyRate = order.CleanerHourlyRate,
                CleanerTotalSalary = order.CleanerTotalSalary,
                HasCleanersService = order.OrderServices?.Any(os => os.Service?.ServiceRelationType == "cleaner") ?? false,
                CancellationReason = order.CancellationReason,
                IsLateCancellation = order.IsLateCancellation,
                Services = order.OrderServices?.Select(os => new OrderServiceDto
                {
                    Id = os.Id,
                    ServiceId = os.ServiceId,
                    ServiceName = os.Service?.Name ?? "",
                    Quantity = os.Quantity,
                    Cost = os.Cost,
                    Duration = os.Duration,
                    PriceMultiplier = os.PriceMultiplier
                }).ToList() ?? new List<OrderServiceDto>(),
                ExtraServices = order.OrderExtraServices?.Select(oes => new OrderExtraServiceDto
                {
                    Id = oes.Id,
                    ExtraServiceId = oes.ExtraServiceId,
                    ExtraServiceName = oes.ExtraService?.Name ?? "",
                    Quantity = oes.Quantity,
                    Hours = oes.Hours,
                    Cost = oes.Cost,
                    Duration = oes.Duration
                }).ToList() ?? new List<OrderExtraServiceDto>(),
                AssignedAdminId = order.AssignedAdminId,
                AssignedAdminFirstName = order.AssignedAdmin?.FirstName,
                AssignedAdminLastName = order.AssignedAdmin?.LastName,
                AssignedAdminDisplayName = order.AssignedAdmin != null
                    ? AdminBonusService.FormatDisplayName(order.AssignedAdmin.FirstName, order.AssignedAdmin.LastName)
                    : null
            };
        }

        public static string? GetSpecialOfferName(string? promoCode)
        {
            if (string.IsNullOrEmpty(promoCode)) return null;
            if (promoCode.StartsWith("SPECIAL_OFFER:"))
                return promoCode.Substring("SPECIAL_OFFER:".Length);
            return null;
        }

        public static string? GetPromoCodeDetails(string? promoCode)
        {
            if (string.IsNullOrEmpty(promoCode)) return null;
            // Special offers are surfaced via SpecialOfferName instead.
            if (promoCode.StartsWith("SPECIAL_OFFER:")) return null;
            // Legacy first-time discount marker.
            if (promoCode == "firstUse") return "First-Time Customer Discount";
            return promoCode;
        }

        public static string MaskGiftCardCode(string code)
        {
            if (code.Length >= 4)
                return $"****-****-{code.Substring(code.Length - 4)}";
            return "****";
        }
    }
}

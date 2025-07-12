using System;
using System.Collections.Generic;

namespace DreamCleaningBackend.DTOs
{
    public class OrderDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int ServiceTypeId { get; set; }
        public string ServiceTypeName { get; set; }
        public DateTime OrderDate { get; set; }
        public DateTime ServiceDate { get; set; }
        public TimeSpan ServiceTime { get; set; }
        public string Status { get; set; }
        public decimal SubTotal { get; set; }
        public decimal Tax { get; set; }
        public decimal Tips { get; set; }
        public decimal CompanyDevelopmentTips { get; set; }
        public decimal Total { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal SubscriptionDiscountAmount { get; set; }
        public string? PromoCode { get; set; }
        public string? SpecialOfferName { get; set; }
        public int? UserSpecialOfferId { get; set; }
        public string? PromoCodeDetails { get; set; }
        public string? GiftCardDetails { get; set; }
        public int SubscriptionId { get; set; } 
        public string SubscriptionName { get; set; }
        public string? GiftCardCode { get; set; }
        public decimal GiftCardAmountUsed { get; set; }
        public string? EntryMethod { get; set; }
        public string? SpecialInstructions { get; set; }
        public string ContactFirstName { get; set; }
        public string ContactLastName { get; set; }
        public string ContactEmail { get; set; }
        public string ContactPhone { get; set; }
        public string ServiceAddress { get; set; }
        public string? AptSuite { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string ZipCode { get; set; }
        public decimal TotalDuration { get; set; }
        public int MaidsCount { get; set; }
        public bool IsPaid { get; set; }
        public DateTime? PaidAt { get; set; }
        public List<OrderServiceDto> Services { get; set; } = new List<OrderServiceDto>();
        public List<OrderExtraServiceDto> ExtraServices { get; set; } = new List<OrderExtraServiceDto>();
    }

    public class OrderServiceDto
    {
        public int Id { get; set; }
        public int ServiceId { get; set; }
        public string ServiceName { get; set; }
        public int Quantity { get; set; }
        public decimal Cost { get; set; }
        public decimal Duration { get; set; }
        public decimal PriceMultiplier { get; set; }
    }

    public class OrderExtraServiceDto
    {
        public int Id { get; set; }
        public int ExtraServiceId { get; set; }
        public string ExtraServiceName { get; set; }
        public int Quantity { get; set; }
        public decimal Hours { get; set; }
        public decimal Cost { get; set; }
        public decimal Duration { get; set; }
    }

    public class UpdateOrderDto
    {
        public DateTime ServiceDate { get; set; }
        public string ServiceTime { get; set; }
        public int MaidsCount { get; set; }
        public decimal TotalDuration { get; set; }
        public string EntryMethod { get; set; }
        public string? SpecialInstructions { get; set; }
        public string ContactFirstName { get; set; }
        public string ContactLastName { get; set; }
        public string ContactEmail { get; set; }
        public string ContactPhone { get; set; }
        public string ServiceAddress { get; set; }
        public string? AptSuite { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string ZipCode { get; set; }
        public List<BookingServiceDto> Services { get; set; } = new List<BookingServiceDto>();
        public List<BookingExtraServiceDto> ExtraServices { get; set; } = new List<BookingExtraServiceDto>();
        public decimal Tips { get; set; }
        public decimal CompanyDevelopmentTips { get; set; }
    }

    public class OrderListDto
    {
        public int Id { get; set; }
        public int UserId { get; set; } 
        public string ContactEmail { get; set; }  
        public string ContactFirstName { get; set; }  
        public string ContactLastName { get; set; }  
        public string ServiceTypeName { get; set; }
        public bool IsCustomServiceType { get; set; }
        public DateTime ServiceDate { get; set; }
        public TimeSpan ServiceTime { get; set; }
        public string Status { get; set; }
        public decimal Total { get; set; }
        public string ServiceAddress { get; set; }
        public DateTime OrderDate { get; set; }
    }

    public class CancelOrderDto
    {
        public string Reason { get; set; }
    }
}
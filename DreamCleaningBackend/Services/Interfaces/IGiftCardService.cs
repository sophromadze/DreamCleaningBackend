using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IGiftCardService
    {
        Task<GiftCard> CreateGiftCard(int userId, CreateGiftCardDto createDto);
        Task<GiftCardValidationDto> ValidateGiftCard(string code);
        Task<decimal> ApplyGiftCardToOrder(string code, decimal orderAmount, int orderId, int userId); // ADD userId parameter
        Task<List<GiftCardDto>> GetUserGiftCards(int userId);
        Task<List<GiftCardUsageDto>> GetGiftCardUsageHistory(string code, int userId);
        Task<GiftCard> GetGiftCardByCode(string code);
        Task<bool> MarkGiftCardAsPaid(int giftCardId, string paymentIntentId);
        string GenerateUniqueGiftCardCode();
        Task<List<GiftCardAdminDto>> GetAllGiftCardsForAdmin();
        Task<bool> SimulateGiftCardPayment(int giftCardId);
    }
}

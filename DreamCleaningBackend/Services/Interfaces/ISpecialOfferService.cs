using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface ISpecialOfferService
    {
        Task<SpecialOfferAdminDto> CreateSpecialOffer(CreateSpecialOfferDto dto, int createdByUserId);
        Task<SpecialOfferAdminDto> UpdateSpecialOffer(int id, UpdateSpecialOfferDto dto);
        Task<bool> DeleteSpecialOffer(int id);
        Task<List<SpecialOfferAdminDto>> GetAllSpecialOffers();
        Task<SpecialOfferAdminDto> GetSpecialOfferById(int id);

        // Grant offers to users
        Task<int> GrantOfferToAllEligibleUsers(int offerId);
        Task<bool> GrantOfferToUser(int offerId, int userId);

        // User operations
        Task<List<UserSpecialOfferDto>> GetUserAvailableOffers(int userId);
        Task<bool> UseSpecialOffer(int userId, int offerId, int orderId);
        Task<UserSpecialOfferDto?> ValidateSpecialOffer(int userId, int offerId);

        // Check and grant first-time offer
        Task GrantFirstTimeOfferIfEligible(int userId);

        // Get first-time discount percentage
        Task<decimal> GetFirstTimeDiscountPercentage();
        Task<bool> EnableSpecialOffer(int id);
        Task<bool> DisableSpecialOffer(int id);

        Task GrantAllActiveOffersToNewUser(int userId);
    }
}

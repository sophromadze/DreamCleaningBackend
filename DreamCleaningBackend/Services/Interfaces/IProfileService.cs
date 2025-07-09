using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IProfileService
    {
        Task<ProfileDto> GetProfile(int userId);
        Task<ProfileDto> UpdateProfile(int userId, UpdateProfileDto updateProfileDto);
        Task<ApartmentDto> AddApartment(int userId, CreateApartmentDto createApartmentDto);
        Task<ApartmentDto> UpdateApartment(int userId, int apartmentId, ApartmentDto apartmentDto);
        Task<bool> DeleteApartment(int userId, int apartmentId);
        Task<List<ApartmentDto>> GetUserApartments(int userId);
    }
}
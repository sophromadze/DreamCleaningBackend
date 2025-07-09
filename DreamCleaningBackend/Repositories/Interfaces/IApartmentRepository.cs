using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Repositories.Interfaces
{
    public interface IApartmentRepository
    {
        Task<Apartment> GetByIdAsync(int id);
        Task<List<Apartment>> GetByUserIdAsync(int userId);
        Task<Apartment> CreateAsync(Apartment apartment);
        Task<Apartment> UpdateAsync(Apartment apartment);
        Task<bool> DeleteAsync(int id);
        Task<bool> BelongsToUserAsync(int apartmentId, int userId);
        Task<bool> SaveChangesAsync();
    }
}
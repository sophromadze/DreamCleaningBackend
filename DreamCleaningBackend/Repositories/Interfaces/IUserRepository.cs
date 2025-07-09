using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Repositories.Interfaces
{
    public interface IUserRepository
    {
        Task<User> GetByEmailAsync(string email);
        Task<User> GetByIdAsync(int id);
        Task<User> GetByIdWithDetailsAsync(int id);
        Task<bool> UserExistsAsync(string email);
        Task<User> CreateAsync(User user);
        Task<User> UpdateAsync(User user);
        Task<bool> SaveChangesAsync();
    }
}
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Repositories.Interfaces;

namespace DreamCleaningBackend.Repositories
{
    public class UserRepository : IUserRepository
    {
        private readonly ApplicationDbContext _context;

        public UserRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<User> GetByEmailAsync(string email)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u => u.Email == email.ToLower());
        }

        public async Task<User> GetByIdAsync(int id)
        {
            return await _context.Users.FindAsync(id);
        }

        public async Task<User> GetByIdWithDetailsAsync(int id)
        {
            return await _context.Users
                .Include(u => u.Apartments.Where(a => a.IsActive))
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Id == id);
        }

        public async Task<bool> UserExistsAsync(string email)
        {
            return await _context.Users
                .AnyAsync(u => u.Email == email.ToLower());
        }

        public async Task<User> CreateAsync(User user)
        {
            await _context.Users.AddAsync(user);
            return user;
        }

        public async Task<User> UpdateAsync(User user)
        {
            _context.Users.Update(user);
            return user;
        }

        public async Task<bool> SaveChangesAsync()
        {
            return await _context.SaveChangesAsync() > 0;
        }
    }
}
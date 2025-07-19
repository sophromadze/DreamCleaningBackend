using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Repositories.Interfaces;

namespace DreamCleaningBackend.Repositories
{
    public class ApartmentRepository : IApartmentRepository
    {
        private readonly ApplicationDbContext _context;

        public ApartmentRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<Apartment> GetByIdAsync(int id)
        {
            return await _context.Apartments
                .FirstOrDefaultAsync(a => a.Id == id && a.IsActive);
        }

        public async Task<List<Apartment>> GetByUserIdAsync(int userId)
        {
            return await _context.Apartments
                .Where(a => a.UserId == userId && a.IsActive)
                .OrderBy(a => a.Name)
                .ToListAsync();
        }

        public async Task<Apartment> CreateAsync(Apartment apartment)
        {
            await _context.Apartments.AddAsync(apartment);
            return apartment;
        }

        public async Task<Apartment> UpdateAsync(Apartment apartment)
        {
            _context.Apartments.Update(apartment);
            return apartment;
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var apartment = await _context.Apartments.FindAsync(id);
            if (apartment == null)
                return false;

            // Soft delete
            apartment.IsActive = false;
            apartment.UpdatedAt = DateTime.UtcNow;
            return true;
        }

        public async Task<bool> BelongsToUserAsync(int apartmentId, int userId)
        {
            return await _context.Apartments
                .AnyAsync(a => a.Id == apartmentId && a.UserId == userId);
        }

        public async Task<bool> SaveChangesAsync()
        {
            return await _context.SaveChangesAsync() > 0;
        }
    }
}
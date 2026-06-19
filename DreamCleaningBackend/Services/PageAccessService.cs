using System.Text.Json;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class PageAccessService : IPageAccessService
    {
        private readonly ApplicationDbContext _context;

        public PageAccessService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<HashSet<string>> GetGrantedPagesAsync(int userId)
        {
            // SuperAdmins implicitly have every page; this method only reports explicit grants,
            // which are only meaningful for the Admin role. The authorization filter handles the
            // SuperAdmin short-circuit, so here we simply return the stored set.
            var raw = await _context.Users
                .Where(u => u.Id == userId)
                .Select(u => u.ViewablePages)
                .FirstOrDefaultAsync();

            return ParsePages(raw).ToHashSet();
        }

        public List<string> ParsePages(string? raw) => AdminViewablePages.Parse(raw);

        public async Task<List<string>> SetGrantedPagesAsync(int userId, IEnumerable<string> pages)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                throw new InvalidOperationException("User not found.");

            if (user.Role != UserRole.Admin)
                throw new InvalidOperationException("Page-view access can only be granted to users with the Admin role.");

            var normalized = (pages ?? Enumerable.Empty<string>())
                .Where(AdminViewablePages.IsValid)
                .Distinct()
                .ToList();

            user.ViewablePages = normalized.Count == 0 ? null : JsonSerializer.Serialize(normalized);
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return normalized;
        }
    }
}

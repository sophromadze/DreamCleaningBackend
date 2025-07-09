using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Repositories.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DreamCleaningBackend.Repositories
{
    public class OrderRepository : IOrderRepository
    {
        private readonly ApplicationDbContext _context;

        public OrderRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<Order> GetByIdAsync(int id)
        {
            return await _context.Orders.FindAsync(id);
        }

        public async Task<Order> GetByIdWithDetailsAsync(int id)
        {
            return await _context.Orders
                .Include(o => o.ServiceType)
                .Include(o => o.Subscription)
                .Include(o => o.OrderServices)
                    .ThenInclude(os => os.Service)
                .Include(o => o.OrderExtraServices)
                    .ThenInclude(oes => oes.ExtraService)
                .FirstOrDefaultAsync(o => o.Id == id);
        }

        public async Task<List<Order>> GetUserOrdersAsync(int userId)
        {
            return await _context.Orders
                .Include(o => o.ServiceType)
                .Where(o => o.UserId == userId)
                .OrderByDescending(o => o.OrderDate)
                .ToListAsync();
        }

        public async Task<Order> CreateAsync(Order order)
        {
            await _context.Orders.AddAsync(order);
            return order;
        }

        public async Task<Order> UpdateAsync(Order order)
        {
            _context.Orders.Update(order);
            return order;
        }

        public async Task<bool> SaveChangesAsync()
        {
            return await _context.SaveChangesAsync() > 0;
        }
    }
}
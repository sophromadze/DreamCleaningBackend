using DreamCleaningBackend.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DreamCleaningBackend.Repositories.Interfaces
{
    public interface IOrderRepository
    {
        Task<Order> GetByIdAsync(int id);
        Task<Order> GetByIdWithDetailsAsync(int id);
        Task<List<Order>> GetUserOrdersAsync(int userId);
        Task<Order> CreateAsync(Order order);
        Task<Order> UpdateAsync(Order order);
        Task<bool> SaveChangesAsync();
    }
}
using DreamCleaningBackend.DTOs;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface IOrderService
    {
        Task<List<OrderListDto>> GetUserOrders(int userId);
        Task<List<OrderListDto>> GetAllOrdersForAdmin();
        Task<OrderDto> GetOrderById(int orderId, int userId);
        Task<OrderDto> UpdateOrder(int orderId, int userId, UpdateOrderDto updateOrderDto);
        Task<bool> CancelOrder(int orderId, int userId, CancelOrderDto cancelOrderDto);
        Task<decimal> CalculateAdditionalAmount(int orderId, UpdateOrderDto updateOrderDto);
        Task<bool> MarkOrderAsDone(int orderId);
        Task<List<OrderListDto>> GetUserOrdersForAdmin(int userId);
        Task<OrderDto> GetOrderByIdForAdmin(int orderId);
    }
}
using System.Collections.Concurrent;
using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services
{
    public interface IBookingDataService
    {
        void StoreBookingData(string sessionId, CreateBookingDto bookingData);
        CreateBookingDto GetBookingData(string sessionId);
        void RemoveBookingData(string sessionId);
    }

    public class BookingDataService : IBookingDataService
    {
        private readonly ConcurrentDictionary<string, CreateBookingDto> _bookingData = new();
        private readonly ILogger<BookingDataService> _logger;

        public BookingDataService(ILogger<BookingDataService> logger)
        {
            _logger = logger;
        }

        public void StoreBookingData(string sessionId, CreateBookingDto bookingData)
        {
            _bookingData[sessionId] = bookingData;
            _logger.LogInformation($"Stored booking data for session {sessionId}");
        }

        public CreateBookingDto GetBookingData(string sessionId)
        {
            _bookingData.TryGetValue(sessionId, out var data);
            return data;
        }

        public void RemoveBookingData(string sessionId)
        {
            _bookingData.TryRemove(sessionId, out _);
            _logger.LogInformation($"Removed booking data for session {sessionId}");
        }
    }
}
namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>
    /// Service for sending SMS via RingCentral. Used for admin-to-user and transactional messages.
    /// </summary>
    public interface ISmsService
    {
        /// <summary>
        /// Send an SMS to a phone number. Number should be E.164 (e.g. +19295551234).
        /// </summary>
        Task SendSmsAsync(string toNumber, string message);

        /// <summary>
        /// Booking confirmation SMS template. Optional: use from OrderService when SMS is enabled.
        /// </summary>
        Task SendBookingConfirmationSmsAsync(string phoneNumber, string customerName, DateTime serviceDate, string serviceTime);

        /// <summary>
        /// Cleaner assignment SMS template. Optional: use when assigning cleaners.
        /// </summary>
        Task SendCleanerAssignmentSmsAsync(string phoneNumber, string cleanerName, DateTime serviceDate, string address);

        /// <summary>
        /// Returns true if SMS is configured and enabled (RingCentral credentials and EnableSmsSending).
        /// </summary>
        bool IsSmsEnabled();

        /// <summary>
        /// Payment reminder SMS template for admin-created orders. Sends reminder to pay for the order.
        /// </summary>
        Task SendPaymentReminderSmsAsync(string phoneNumber, string customerName, decimal amount, int orderId, string orderLink);

        /// <summary>
        /// Notify customer that their order was updated and an additional payment is required. Includes payment link.
        /// </summary>
        Task SendAdditionalPaymentRequiredSmsAsync(string phoneNumber, string customerName, decimal additionalAmount, int orderId, string paymentLink);
    }
}

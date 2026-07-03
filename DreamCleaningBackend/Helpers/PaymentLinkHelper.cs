using System.Security.Cryptography;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Builds customer payment links (/order/{id}/pay?t=&lt;token&gt;). The token grants
    /// logged-out access to the order's payment page while something is unpaid, so every link
    /// we send out must carry it — and legacy orders get one backfilled the first time a link
    /// is (re)sent for them.
    /// </summary>
    public static class PaymentLinkHelper
    {
        public static string GenerateToken()
        {
            // 24 random bytes → 48 hex chars; fits the 64-char column with headroom.
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        }

        /// <summary>Ensures the (tracked) order has a token, persisting a new one when missing,
        /// and returns the full tokenized payment link.</summary>
        public static async Task<string> BuildPaymentLinkAsync(ApplicationDbContext context, Order order, string frontendUrl)
        {
            if (string.IsNullOrEmpty(order.PaymentAccessToken))
            {
                order.PaymentAccessToken = GenerateToken();
                await context.SaveChangesAsync();
            }
            return $"{frontendUrl.TrimEnd('/')}/order/{order.Id}/pay?t={order.PaymentAccessToken}";
        }

        /// <summary>Constant-time-ish token check; false for null/empty on either side.</summary>
        public static bool TokenMatches(Order order, string? token)
        {
            if (string.IsNullOrEmpty(order.PaymentAccessToken) || string.IsNullOrEmpty(token))
                return false;
            var expected = System.Text.Encoding.UTF8.GetBytes(order.PaymentAccessToken);
            var actual = System.Text.Encoding.UTF8.GetBytes(token);
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
    }
}

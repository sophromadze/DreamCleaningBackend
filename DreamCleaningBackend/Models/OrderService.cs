using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class OrderService
    {
        public int Id { get; set; }

        // Order relationship
        public int OrderId { get; set; }
        public virtual Order Order { get; set; }

        // Service relationship
        public int ServiceId { get; set; }
        public virtual Service Service { get; set; }

        // Quantity/Value for this service in this order
        public int Quantity { get; set; } // e.g., 3 bedrooms, 2 bathrooms, 1500 sqft

        // Calculated cost for this service
        [Column(TypeName = "decimal(18,2)")]
        public decimal Cost { get; set; }

        // Duration in minutes for this service
        public int Duration { get; set; }

        // For tracking deep cleaning price modifications
        [Column(TypeName = "decimal(18,2)")]
        public decimal PriceMultiplier { get; set; } = 1.0m;

        public DateTime CreatedAt { get; set; }
    }
}
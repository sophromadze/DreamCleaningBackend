using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    public class OrderExtraService
    {
        public int Id { get; set; }

        // Order relationship
        public int OrderId { get; set; }
        public virtual Order Order { get; set; }

        // Extra Service relationship
        public int ExtraServiceId { get; set; }
        public virtual ExtraService ExtraService { get; set; }

        // Quantity (for services with quantity)
        public int Quantity { get; set; } = 1;

        // Hours (for services with hours, stored in 30-minute increments)
        public decimal Hours { get; set; } = 0;

        // Calculated cost
        [Column(TypeName = "decimal(18,2)")]
        public decimal Cost { get; set; }

        // Duration in minutes
        public int Duration { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
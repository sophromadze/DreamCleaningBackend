using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    public class AdminShiftDto
    {
        public int Id { get; set; }
        public DateTime ShiftDate { get; set; }
        public int AdminId { get; set; }
        public string AdminName { get; set; } = string.Empty;
        public string AdminRole { get; set; } = string.Empty;
        public string? AdminColor { get; set; }
        public string? Notes { get; set; }
        public int CreatedByUserId { get; set; }
        public string CreatedByUserName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CreateAdminShiftDto
    {
        [Required]
        public DateTime ShiftDate { get; set; }

        [Required]
        public int AdminId { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }
    }

    public class UpdateAdminShiftDto
    {
        public int? AdminId { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }
    }

    public class BulkSetShiftsDto
    {
        [Required]
        public DateTime ShiftDate { get; set; }

        [Required]
        public List<int> AdminIds { get; set; } = new();

        [StringLength(500)]
        public string? Notes { get; set; }
    }

    public class SetAdminColorDto
    {
        [StringLength(10)]
        public string? Color { get; set; }
    }

    public class ShiftAdminDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? ShiftColor { get; set; }
    }
}

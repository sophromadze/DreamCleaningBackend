namespace DreamCleaningBackend.DTOs
{
    public class UserDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string? Phone { get; set; }
        public bool FirstTimeOrder { get; set; }
        public int? SubscriptionId { get; set; }
        public string? AuthProvider { get; set; }
        public string Role { get; set; } 
    }
}

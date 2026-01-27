using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.DTOs
{
    public class AppleLoginDto
    {
        public string IdentityToken { get; set; } = string.Empty;
        public string AuthorizationCode { get; set; } = string.Empty;
        public AppleUserDto? User { get; set; }
    }

    public class AppleUserDto
    {
        public AppleNameDto? Name { get; set; }
        public string? Email { get; set; }
    }

    public class AppleNameDto
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
    }

    // DTO for receiving Apple's form_post callback
    // Apple sends form data with these exact field names
    public class AppleCallbackFormDto
    {
        [FromForm(Name = "code")]
        public string? Code { get; set; }
        
        [FromForm(Name = "id_token")]
        public string? IdToken { get; set; }
        
        [FromForm(Name = "state")]
        public string? State { get; set; }
        
        [FromForm(Name = "user")]
        public string? User { get; set; }
        
        [FromForm(Name = "error")]
        public string? Error { get; set; }
        
        [FromForm(Name = "error_description")]
        public string? ErrorDescription { get; set; }
    }
}

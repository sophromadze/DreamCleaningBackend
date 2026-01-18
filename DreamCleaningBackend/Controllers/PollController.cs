using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.Authorization;
using DreamCleaningBackend.Services.Interfaces;

namespace DreamCleaningBackend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PollController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<PollController> _logger;

        public PollController(
            ApplicationDbContext context,
            IEmailService emailService,
            IConfiguration configuration,
            ILogger<PollController> logger)
        {
            _context = context;
            _emailService = emailService;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet("questions/{serviceTypeId}")]
        public async Task<ActionResult<List<PollQuestionDto>>> GetPollQuestions(int serviceTypeId)
        {
            var questions = await _context.PollQuestions
                .Where(pq => pq.ServiceTypeId == serviceTypeId && pq.IsActive)
                .OrderBy(pq => pq.DisplayOrder)
                .Select(pq => new PollQuestionDto
                {
                    Id = pq.Id,
                    Question = pq.Question,
                    QuestionType = pq.QuestionType,
                    Options = pq.Options,
                    IsRequired = pq.IsRequired,
                    DisplayOrder = pq.DisplayOrder,
                    IsActive = pq.IsActive,
                    ServiceTypeId = pq.ServiceTypeId
                })
                .ToListAsync();

            return Ok(questions);
        }

        [HttpPost("submit")]
        public async Task<ActionResult> SubmitPoll(CreatePollSubmissionDto dto)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // For anonymous users, set UserId to null instead of trying to get from User claims
                int? userId = null;
                if (User.Identity?.IsAuthenticated == true)
                {
                    // If user is logged in, get their ID
                    var userIdClaim = User.FindFirst("userId")?.Value;
                    if (!string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out var parsedUserId))
                    {
                        userId = parsedUserId;
                    }
                }

                var submission = new PollSubmission
                {
                    UserId = userId, // This can be null for anonymous submissions
                    ServiceTypeId = dto.ServiceTypeId,
                    ContactFirstName = dto.ContactFirstName,
                    ContactLastName = dto.ContactLastName,
                    ContactEmail = dto.ContactEmail,
                    ContactPhone = dto.ContactPhone,
                    ServiceAddress = dto.ServiceAddress,
                    AptSuite = dto.AptSuite,
                    City = dto.City,
                    State = dto.State,
                    PostalCode = dto.PostalCode,
                    Status = "Pending",
                    CreatedAt = DateTime.UtcNow
                };

                _context.PollSubmissions.Add(submission);
                await _context.SaveChangesAsync();

                // Add answers
                foreach (var answerDto in dto.Answers)
                {
                    var answer = new PollAnswer
                    {
                        PollSubmissionId = submission.Id,
                        PollQuestionId = answerDto.PollQuestionId,
                        Answer = answerDto.Answer,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.PollAnswers.Add(answer);
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                // Send email notifications
                await SendPollSubmissionEmails(submission, dto.UploadedPhotos);

                return Ok(new { message = "Poll submitted successfully! We will review your request and contact you soon." });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Error submitting poll");
                return StatusCode(500, new { message = "An error occurred while submitting the poll." });
            }
        }

        private async Task SendPollSubmissionEmails(PollSubmission submission, List<PhotoUploadDto> uploadedPhotos = null)
        {
            try
            {
                // Load full submission data
                var fullSubmission = await _context.PollSubmissions
                    .Include(ps => ps.ServiceType)
                    .Include(ps => ps.PollAnswers)
                        .ThenInclude(pa => pa.PollQuestion)
                    .FirstOrDefaultAsync(ps => ps.Id == submission.Id);

                if (fullSubmission == null) return;

                var companyEmail = _configuration["Email:CompanyEmail"] ?? _configuration["Email:FromAddress"];
                
                // Validate company email before attempting to send
                if (string.IsNullOrWhiteSpace(companyEmail))
                {
                    _logger.LogError("Company email is not configured. Cannot send poll submission notification email.");
                    return;
                }

                var subject = $"New Poll Submission: {fullSubmission.ServiceType.Name} - {fullSubmission.ContactFirstName} {fullSubmission.ContactLastName}";

                var answersHtml = string.Join("", fullSubmission.PollAnswers.Select(pa => $@"
                    <tr>
                        <td style='padding: 10px; border: 1px solid #ddd; background-color: #f8f9fa; font-weight: bold; vertical-align: top; width: 30%;'>{pa.PollQuestion.Question}:</td>
                        <td style='padding: 10px; border: 1px solid #ddd; white-space: pre-wrap;'>{pa.Answer}</td>
                    </tr>"));

                var photoInfo = "";
                if (uploadedPhotos != null && uploadedPhotos.Any())
                {
                    photoInfo = $@"
                    <div style='background: #e8f5e9; padding: 15px; border-radius: 8px; margin: 20px 0;'>
                        <p><strong>📷 Customer Photos:</strong></p>
                        <p>The customer has uploaded {uploadedPhotos.Count} photo(s) to help illustrate their cleaning needs. Please see the attached files.</p>
                    </div>";
                }

                // Handle nullable fields safely
                var contactLastName = !string.IsNullOrWhiteSpace(fullSubmission.ContactLastName) ? fullSubmission.ContactLastName : "";
                var contactName = !string.IsNullOrWhiteSpace(contactLastName) 
                    ? $"{fullSubmission.ContactFirstName} {contactLastName}" 
                    : fullSubmission.ContactFirstName;
                var contactEmail = !string.IsNullOrWhiteSpace(fullSubmission.ContactEmail) 
                    ? $"<a href='mailto:{fullSubmission.ContactEmail}'>{fullSubmission.ContactEmail}</a>" 
                    : "Not provided";
                var serviceAddressDisplay = !string.IsNullOrWhiteSpace(fullSubmission.ServiceAddress)
                    ? $"{fullSubmission.ServiceAddress}{(!string.IsNullOrEmpty(fullSubmission.AptSuite) ? $", {fullSubmission.AptSuite}" : "")}<br>{fullSubmission.City}, {fullSubmission.State} {fullSubmission.PostalCode}"
                    : "Not provided";

                var body = $@"
                    <h2>New Poll Submission</h2>
                    <p>A customer has submitted a poll for <strong>{fullSubmission.ServiceType.Name}</strong> service:</p>
                    
                    <h3>Contact Information</h3>
                    <table style='width: 100%; border-collapse: collapse; margin: 20px 0;'>
                        <tr>
                            <td style='padding: 10px; border: 1px solid #ddd; background-color: #f8f9fa; font-weight: bold; width: 30%;'>Name:</td>
                            <td style='padding: 10px; border: 1px solid #ddd;'>{contactName}</td>
                        </tr>
                        <tr>
                            <td style='padding: 10px; border: 1px solid #ddd; background-color: #f8f9fa; font-weight: bold;'>Email:</td>
                            <td style='padding: 10px; border: 1px solid #ddd;'>{contactEmail}</td>
                        </tr>
                        <tr>
                            <td style='padding: 10px; border: 1px solid #ddd; background-color: #f8f9fa; font-weight: bold;'>Phone:</td>
                            <td style='padding: 10px; border: 1px solid #ddd;'>{fullSubmission.ContactPhone ?? "Not provided"}</td>
                        </tr>
                        <tr>
                            <td style='padding: 10px; border: 1px solid #ddd; background-color: #f8f9fa; font-weight: bold;'>Service Address:</td>
                            <td style='padding: 10px; border: 1px solid #ddd;'>{serviceAddressDisplay}</td>
                        </tr>
                    </table>
                    
                    <h3>Poll Answers</h3>
                    <table style='width: 100%; border-collapse: collapse; margin: 20px 0;'>
                        {answersHtml}
                    </table>
                    
                    <p style='margin-top: 30px; color: #666; font-size: 14px;'>
                        <strong>Submitted on:</strong> {fullSubmission.CreatedAt:MMMM dd, yyyy at h:mm tt}
                    </p>
                    
                    {photoInfo}
                ";

                // Use the new method to send email with photos
                await _emailService.SendPollSubmissionEmailWithPhotosAsync(
                    companyEmail,
                    subject,
                    body,
                    uploadedPhotos
                );

                _logger.LogInformation($"Poll submission email with {uploadedPhotos?.Count ?? 0} photos sent for submission ID {submission.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send poll submission email for submission ID {submission.Id}");
            }
        }
    }
}
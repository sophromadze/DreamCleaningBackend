using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using DreamCleaningBackend.Attributes;
using DreamCleaningBackend.Hubs;
using System.Linq;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using DreamCleaningBackend.Services;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>Poll questions and submissions.
    /// Split out of the monolithic AdminController; same api/admin route prefix, so URLs are unchanged.</summary>
    [Route("api/admin")]
    [ApiController]
    [Authorize(Roles = "Admin,SuperAdmin,Moderator")]
    public class AdminPollsController : AdminControllerBase
    {
        private readonly ApplicationDbContext _context;

        public AdminPollsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet("poll-questions")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<PollQuestionDto>>> GetAllPollQuestions()
        {
            var questions = await _context.PollQuestions
                .Include(pq => pq.ServiceType)
                .OrderBy(pq => pq.ServiceTypeId)
                .ThenBy(pq => pq.DisplayOrder)
                .ThenBy(pq => pq.Id)
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

        /// <summary>
        /// Get all poll questions for a service type (including inactive). Used by admin so questions are visible even when service type or question is inactive.
        /// </summary>
        [HttpGet("poll-questions/by-service-type/{serviceTypeId}")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<PollQuestionDto>>> GetPollQuestionsByServiceType(int serviceTypeId)
        {
            var questions = await _context.PollQuestions
                .Where(pq => pq.ServiceTypeId == serviceTypeId)
                .OrderBy(pq => pq.DisplayOrder)
                .ThenBy(pq => pq.Id)
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

        [HttpPost("poll-questions")]
        [RequirePermission(Permission.Create)]
        public async Task<ActionResult<PollQuestionDto>> CreatePollQuestion(CreatePollQuestionDto dto)
        {
            var question = new PollQuestion
            {
                Question = dto.Question,
                QuestionType = dto.QuestionType,
                Options = dto.Options,
                IsRequired = dto.IsRequired,
                DisplayOrder = dto.DisplayOrder,
                ServiceTypeId = dto.ServiceTypeId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _context.PollQuestions.Add(question);
            await _context.SaveChangesAsync();

            var result = new PollQuestionDto
            {
                Id = question.Id,
                Question = question.Question,
                QuestionType = question.QuestionType,
                Options = question.Options,
                IsRequired = question.IsRequired,
                DisplayOrder = question.DisplayOrder,
                IsActive = question.IsActive,
                ServiceTypeId = question.ServiceTypeId
            };

            return Ok(result);
        }

        [HttpPut("poll-questions/{id}")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> UpdatePollQuestion(int id, PollQuestionDto dto)
        {
            var question = await _context.PollQuestions.FindAsync(id);
            if (question == null)
            {
                return NotFound();
            }

            question.Question = dto.Question;
            question.QuestionType = dto.QuestionType;
            question.Options = dto.Options;
            question.IsRequired = dto.IsRequired;
            question.DisplayOrder = dto.DisplayOrder;
            question.IsActive = dto.IsActive;
            question.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return Ok();
        }

        [HttpDelete("poll-questions/{id}")]
        [RequirePermission(Permission.Delete)]
        public async Task<ActionResult> DeletePollQuestion(int id)
        {
            var question = await _context.PollQuestions.FindAsync(id);
            if (question == null)
            {
                return NotFound();
            }

            _context.PollQuestions.Remove(question);
            await _context.SaveChangesAsync();
            return Ok();
        }

        [HttpGet("poll-submissions")]
        [RequirePermission(Permission.View)]
        public async Task<ActionResult<List<PollSubmissionDto>>> GetPollSubmissions()
        {
            var submissions = await _context.PollSubmissions
                .Include(ps => ps.ServiceType)
                .Include(ps => ps.PollAnswers)
                    .ThenInclude(pa => pa.PollQuestion)
                .OrderByDescending(ps => ps.CreatedAt)
                .Select(ps => new PollSubmissionDto
                {
                    Id = ps.Id,
                    UserId = ps.UserId,
                    ServiceTypeId = ps.ServiceTypeId,
                    ServiceTypeName = ps.ServiceType.Name,
                    ContactFirstName = ps.ContactFirstName,
                    ContactLastName = ps.ContactLastName,
                    ContactEmail = ps.ContactEmail,
                    ContactPhone = ps.ContactPhone,
                    ServiceAddress = ps.ServiceAddress,
                    AptSuite = ps.AptSuite,
                    City = ps.City,
                    State = ps.State,
                    PostalCode = ps.PostalCode,
                    Status = ps.Status,
                    AdminNotes = ps.AdminNotes,
                    CreatedAt = ps.CreatedAt,
                    Answers = ps.PollAnswers.Select(pa => new PollAnswerDto
                    {
                        Id = pa.Id,
                        PollQuestionId = pa.PollQuestionId,
                        Question = pa.PollQuestion.Question,
                        Answer = pa.Answer
                    }).ToList()
                })
                .ToListAsync();

            return Ok(submissions);
        }

        [HttpPut("poll-submissions/{id}/status")]
        [RequirePermission(Permission.Update)]
        public async Task<ActionResult> UpdatePollSubmissionStatus(int id, [FromBody] UpdatePollSubmissionStatusDto dto)
        {
            var submission = await _context.PollSubmissions.FindAsync(id);
            if (submission == null)
            {
                return NotFound();
            }

            submission.Status = dto.Status;
            submission.AdminNotes = dto.AdminNotes;
            submission.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return Ok();
        }

    }
}

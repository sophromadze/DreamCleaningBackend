namespace DreamCleaningBackend.DTOs
{
    public class PollQuestionDto
    {
        public int Id { get; set; }
        public string Question { get; set; }
        public string QuestionType { get; set; }
        public string? Options { get; set; }
        public bool IsRequired { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; }
        public int ServiceTypeId { get; set; }
    }

    public class CreatePollQuestionDto
    {
        public string Question { get; set; }
        public string QuestionType { get; set; } = "text";
        public string? Options { get; set; }
        public bool IsRequired { get; set; } = false;
        public int DisplayOrder { get; set; }
        public int ServiceTypeId { get; set; }
    }

    public class PollSubmissionDto
    {
        public int Id { get; set; }
        public int? UserId { get; set; }
        public int ServiceTypeId { get; set; }
        public string ServiceTypeName { get; set; }
        public string ContactFirstName { get; set; }
        public string ContactLastName { get; set; }
        public string ContactEmail { get; set; }
        public string? ContactPhone { get; set; }
        public string ServiceAddress { get; set; }
        public string? AptSuite { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string PostalCode { get; set; }
        public string Status { get; set; }
        public string? AdminNotes { get; set; }
        public List<PollAnswerDto> Answers { get; set; } = new List<PollAnswerDto>();
        public DateTime CreatedAt { get; set; }
        public List<PhotoUploadDto> UploadedPhotos { get; set; } = new List<PhotoUploadDto>();
    }

    public class CreatePollSubmissionDto
    {
        public int ServiceTypeId { get; set; }
        public string ContactFirstName { get; set; }
        public string ContactLastName { get; set; }
        public string ContactEmail { get; set; }
        public string? ContactPhone { get; set; }
        public string ServiceAddress { get; set; }
        public string? AptSuite { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public string PostalCode { get; set; }
        public List<CreatePollAnswerDto> Answers { get; set; } = new List<CreatePollAnswerDto>();
        public List<PhotoUploadDto> UploadedPhotos { get; set; } = new List<PhotoUploadDto>();
    }

    public class PollAnswerDto
    {
        public int Id { get; set; }
        public int PollQuestionId { get; set; }
        public string Question { get; set; }
        public string Answer { get; set; }
    }

    public class CreatePollAnswerDto
    {
        public int PollQuestionId { get; set; }
        public string Answer { get; set; }
    }

    public class UpdatePollSubmissionStatusDto
    {
        public string Status { get; set; }
        public string? AdminNotes { get; set; }
    }
}

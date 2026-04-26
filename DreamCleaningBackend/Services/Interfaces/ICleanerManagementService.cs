using DreamCleaningBackend.DTOs;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface ICleanerManagementService
    {
        Task<List<CleanerListItemDto>> GetAllAsync(bool includeInactive = false, string? search = null);
        Task<CleanerDetailDto?> GetByIdAsync(int id);
        Task<CleanerDetailDto> CreateAsync(CreateCleanerDto dto, int adminId);
        Task<CleanerDetailDto?> UpdateAsync(int id, UpdateCleanerDto dto);
        Task<bool> DeleteAsync(int id);

        Task<CleanerNoteDto> AddNoteAsync(int cleanerId, CreateCleanerNoteDto dto, int adminId, string adminDisplayName);
        Task<bool> DeleteNoteAsync(int noteId);
        Task<CleanerNoteDto?> UpsertOrderPerformanceAsync(int cleanerId, UpsertOrderPerformanceDto dto, int adminId, string adminDisplayName);

        Task<CleanerImageUploadResultDto?> UploadPhotoAsync(int cleanerId, IFormFile file);
        Task<CleanerImageUploadResultDto?> UploadDocumentAsync(int cleanerId, IFormFile file);
    }
}

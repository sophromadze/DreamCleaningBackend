using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services.Interfaces
{
    public interface ICleanerManagementService
    {
        Task<List<CleanerListItemDto>> GetAllAsync(bool includeInactive = false, string? search = null);
        // includePay: fill CleanerDetailDto.WorkStats' money fields. Pass CallerCanSeePayAsync's
        // answer — the default (false) never leaks wages to a caller nobody checked.
        Task<CleanerDetailDto?> GetByIdAsync(int id, bool includePay = false);
        Task<CleanerDetailDto> CreateAsync(CreateCleanerDto dto, int adminId, bool includePay = false);
        Task<CleanerDetailDto?> UpdateAsync(int id, UpdateCleanerDto dto, int adminId, bool includePay = false);
        Task<bool> CallerCanSeePayAsync(int userId, UserRole role);
        Task<bool> DeleteAsync(int id);

        Task<CleanerNoteDto> AddNoteAsync(int cleanerId, CreateCleanerNoteDto dto, int adminId, string adminDisplayName);
        Task<CleanerNoteDto?> UpdateNoteAsync(int noteId, UpdateCleanerNoteDto dto);
        Task<bool> DeleteNoteAsync(int noteId);
        Task<CleanerNoteDto?> UpsertOrderPerformanceAsync(int cleanerId, UpsertOrderPerformanceDto dto, int adminId, string adminDisplayName);

        Task<CleanerImageUploadResultDto?> UploadPhotoAsync(int cleanerId, IFormFile file);
        Task<CleanerImageUploadResultDto?> UploadDocumentAsync(int cleanerId, IFormFile file);
    }
}

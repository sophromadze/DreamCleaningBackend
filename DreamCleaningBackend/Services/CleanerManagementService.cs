using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace DreamCleaningBackend.Services
{
    public class CleanerManagementService : ICleanerManagementService
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;

        private const long MaxUploadSizeBytes = 10 * 1024 * 1024;
        private static readonly string[] AllowedImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };

        public CleanerManagementService(ApplicationDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        public async Task<List<CleanerListItemDto>> GetAllAsync(bool includeInactive = false, string? search = null)
        {
            var query = _context.Cleaners.AsQueryable();

            if (!includeInactive)
                query = query.Where(c => c.IsActive);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(c =>
                    c.FirstName.Contains(term) ||
                    c.LastName.Contains(term) ||
                    (c.Phone != null && c.Phone.Contains(term)) ||
                    (c.Email != null && c.Email.Contains(term)) ||
                    (c.Location != null && c.Location.Contains(term)));
            }

            return await query
                .OrderByDescending(c => c.Ranking == CleanerRanking.Top)
                .ThenBy(c => c.FirstName)
                .ThenBy(c => c.LastName)
                .Select(c => new CleanerListItemDto
                {
                    Id = c.Id,
                    FirstName = c.FirstName,
                    LastName = c.LastName,
                    Age = c.Age,
                    Experience = c.Experience,
                    IsExperienced = c.IsExperienced,
                    Phone = c.Phone,
                    Email = c.Email,
                    Location = c.Location,
                    Availability = c.Availability,
                    AlreadyWorkedWithUs = c.AlreadyWorkedWithUs,
                    Nationality = c.Nationality,
                    Ranking = c.Ranking,
                    PhotoUrl = c.PhotoUrl,
                    IsActive = c.IsActive
                })
                .ToListAsync();
        }

        public async Task<CleanerDetailDto?> GetByIdAsync(int id)
        {
            var cleaner = await _context.Cleaners
                .Include(c => c.CreatedByAdmin)
                .Include(c => c.Notes.OrderByDescending(n => n.CreatedAt))
                    .ThenInclude(n => n.Admin)
                .AsSplitQuery()
                .FirstOrDefaultAsync(c => c.Id == id);

            if (cleaner == null)
                return null;

            var assignments = await _context.OrderCleaners
                .Where(oc => oc.CleanerId == id)
                .Include(oc => oc.Order)
                    .ThenInclude(o => o.ServiceType)
                .OrderByDescending(oc => oc.Order.ServiceDate)
                .Select(oc => new CleanerAssignedOrderDto
                {
                    OrderId = oc.OrderId,
                    ServiceDate = oc.Order.ServiceDate,
                    ServiceTime = oc.Order.ServiceTime.ToString(),
                    ServiceAddress = oc.Order.ServiceAddress,
                    ServiceCity = oc.Order.City,
                    ServiceTypeName = oc.Order.ServiceType != null ? oc.Order.ServiceType.Name : null,
                    Status = oc.Order.Status,
                    AssignedAt = oc.AssignedAt,
                    AssignmentNotificationSentAt = oc.AssignmentNotificationSentAt
                })
                .ToListAsync();

            return MapToDetail(cleaner, assignments);
        }

        public async Task<CleanerDetailDto> CreateAsync(CreateCleanerDto dto, int adminId)
        {
            var cleaner = new Cleaner
            {
                FirstName = dto.FirstName,
                LastName = dto.LastName,
                Age = dto.Age,
                Experience = dto.Experience,
                IsExperienced = dto.IsExperienced,
                Phone = dto.Phone,
                Email = dto.Email,
                Location = dto.Location,
                Availability = dto.Availability,
                AlreadyWorkedWithUs = dto.AlreadyWorkedWithUs,
                Nationality = dto.Nationality,
                Ranking = dto.Ranking,
                RestrictedReason = dto.RestrictedReason,
                Allergies = dto.Allergies,
                Restrictions = dto.Restrictions,
                MainNote = dto.MainNote,
                DocumentType = dto.DocumentType,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                CreatedByAdminId = adminId
            };

            _context.Cleaners.Add(cleaner);
            await _context.SaveChangesAsync();

            return (await GetByIdAsync(cleaner.Id))!;
        }

        public async Task<CleanerDetailDto?> UpdateAsync(int id, UpdateCleanerDto dto)
        {
            var cleaner = await _context.Cleaners.FirstOrDefaultAsync(c => c.Id == id);
            if (cleaner == null)
                return null;

            cleaner.FirstName = dto.FirstName;
            cleaner.LastName = dto.LastName;
            cleaner.Age = dto.Age;
            cleaner.Experience = dto.Experience;
            cleaner.IsExperienced = dto.IsExperienced;
            cleaner.Phone = dto.Phone;
            cleaner.Email = dto.Email;
            cleaner.Location = dto.Location;
            cleaner.Availability = dto.Availability;
            cleaner.AlreadyWorkedWithUs = dto.AlreadyWorkedWithUs;
            cleaner.Nationality = dto.Nationality;
            cleaner.Ranking = dto.Ranking;
            cleaner.RestrictedReason = dto.RestrictedReason;
            cleaner.Allergies = dto.Allergies;
            cleaner.Restrictions = dto.Restrictions;
            cleaner.MainNote = dto.MainNote;
            cleaner.DocumentType = dto.DocumentType;
            cleaner.IsActive = dto.IsActive;
            cleaner.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return await GetByIdAsync(id);
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var cleaner = await _context.Cleaners.FirstOrDefaultAsync(c => c.Id == id);
            if (cleaner == null)
                return false;

            var hasAssignments = await _context.OrderCleaners.AnyAsync(oc => oc.CleanerId == id);
            if (hasAssignments)
            {
                cleaner.IsActive = false;
                cleaner.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return true;
            }

            DeleteFileIfExists(cleaner.PhotoUrl);
            DeleteFileIfExists(cleaner.DocumentUrl);

            _context.Cleaners.Remove(cleaner);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<CleanerNoteDto> AddNoteAsync(int cleanerId, CreateCleanerNoteDto dto, int adminId, string adminDisplayName)
        {
            var note = new CleanerNote
            {
                CleanerId = cleanerId,
                AdminId = adminId,
                AdminDisplayName = adminDisplayName,
                Text = dto.Text,
                OrderId = dto.OrderId,
                OrderPerformance = dto.OrderPerformance,
                CreatedAt = DateTime.UtcNow
            };

            _context.CleanerNotes.Add(note);
            await _context.SaveChangesAsync();

            return new CleanerNoteDto
            {
                Id = note.Id,
                CleanerId = note.CleanerId,
                AdminId = note.AdminId,
                AdminDisplayName = note.AdminDisplayName,
                Text = note.Text,
                OrderId = note.OrderId,
                OrderPerformance = note.OrderPerformance,
                CreatedAt = note.CreatedAt
            };
        }

        public async Task<CleanerNoteDto?> UpdateNoteAsync(int noteId, UpdateCleanerNoteDto dto)
        {
            var note = await _context.CleanerNotes.FirstOrDefaultAsync(n => n.Id == noteId);
            if (note == null)
                return null;

            note.Text = dto.Text;
            await _context.SaveChangesAsync();

            return new CleanerNoteDto
            {
                Id = note.Id,
                CleanerId = note.CleanerId,
                AdminId = note.AdminId,
                AdminDisplayName = note.AdminDisplayName,
                Text = note.Text,
                OrderId = note.OrderId,
                OrderPerformance = note.OrderPerformance,
                CreatedAt = note.CreatedAt
            };
        }

        public async Task<bool> DeleteNoteAsync(int noteId)
        {
            var note = await _context.CleanerNotes.FirstOrDefaultAsync(n => n.Id == noteId);
            if (note == null)
                return false;

            _context.CleanerNotes.Remove(note);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<CleanerNoteDto?> UpsertOrderPerformanceAsync(int cleanerId, UpsertOrderPerformanceDto dto, int adminId, string adminDisplayName)
        {
            var cleanerExists = await _context.Cleaners.AnyAsync(c => c.Id == cleanerId);
            if (!cleanerExists)
                return null;

            var existing = await _context.CleanerNotes
                .FirstOrDefaultAsync(n => n.CleanerId == cleanerId && n.OrderId == dto.OrderId);

            var hasPerformance = !string.IsNullOrWhiteSpace(dto.Performance);
            var hasText = !string.IsNullOrWhiteSpace(dto.Text);

            if (!hasPerformance && !hasText)
            {
                if (existing != null)
                {
                    _context.CleanerNotes.Remove(existing);
                    await _context.SaveChangesAsync();
                }
                return null;
            }

            if (existing != null)
            {
                existing.OrderPerformance = hasPerformance ? dto.Performance : null;
                existing.Text = hasText ? dto.Text : existing.Text;
                existing.AdminId = adminId;
                existing.AdminDisplayName = adminDisplayName;
                await _context.SaveChangesAsync();
                return MapNote(existing);
            }

            var note = new CleanerNote
            {
                CleanerId = cleanerId,
                AdminId = adminId,
                AdminDisplayName = adminDisplayName,
                Text = hasText ? dto.Text! : (dto.Performance ?? string.Empty),
                OrderId = dto.OrderId,
                OrderPerformance = hasPerformance ? dto.Performance : null,
                CreatedAt = DateTime.UtcNow
            };

            _context.CleanerNotes.Add(note);
            await _context.SaveChangesAsync();
            return MapNote(note);
        }

        private static CleanerNoteDto MapNote(CleanerNote note)
        {
            return new CleanerNoteDto
            {
                Id = note.Id,
                CleanerId = note.CleanerId,
                AdminId = note.AdminId,
                AdminDisplayName = note.AdminDisplayName,
                Text = note.Text,
                OrderId = note.OrderId,
                OrderPerformance = note.OrderPerformance,
                CreatedAt = note.CreatedAt
            };
        }

        public async Task<CleanerImageUploadResultDto?> UploadPhotoAsync(int cleanerId, IFormFile file)
        {
            var cleaner = await _context.Cleaners.FirstOrDefaultAsync(c => c.Id == cleanerId);
            if (cleaner == null)
                return null;

            var result = await SaveWebpImageAsync(file, "cleaners/photos", $"cleaner-{cleanerId}", maxWidth: 800, maxHeight: 800, quality: 82);
            if (result == null)
                return null;

            DeleteFileIfExists(cleaner.PhotoUrl);
            cleaner.PhotoUrl = result.Url;
            cleaner.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return result;
        }

        public async Task<CleanerImageUploadResultDto?> UploadDocumentAsync(int cleanerId, IFormFile file)
        {
            var cleaner = await _context.Cleaners.FirstOrDefaultAsync(c => c.Id == cleanerId);
            if (cleaner == null)
                return null;

            var result = await SaveWebpImageAsync(file, "cleaners/documents", $"cleaner-{cleanerId}-doc", maxWidth: 2000, maxHeight: 2000, quality: 88);
            if (result == null)
                return null;

            DeleteFileIfExists(cleaner.DocumentUrl);
            cleaner.DocumentUrl = result.Url;
            cleaner.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return result;
        }

        private async Task<CleanerImageUploadResultDto?> SaveWebpImageAsync(IFormFile file, string subfolder, string baseFileName, int maxWidth, int maxHeight, int quality)
        {
            if (file == null || file.Length == 0)
                throw new InvalidOperationException("No file uploaded.");

            if (file.Length > MaxUploadSizeBytes)
                throw new InvalidOperationException("File size must be less than 10MB.");

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedImageExtensions.Contains(extension))
                throw new InvalidOperationException("Invalid file type. Only image files are allowed.");

            var basePath = _configuration["FileUpload:Path"];
            if (string.IsNullOrWhiteSpace(basePath))
                throw new InvalidOperationException("FileUpload:Path is not configured.");

            var uploadDir = Path.Combine(basePath, subfolder);
            Directory.CreateDirectory(uploadDir);

            var fileName = $"{baseFileName}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.webp";
            var fullPath = Path.Combine(uploadDir, fileName);

            using (var inputStream = file.OpenReadStream())
            using (var image = await Image.LoadAsync(inputStream))
            {
                if (image.Width > maxWidth || image.Height > maxHeight)
                {
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(maxWidth, maxHeight),
                        Mode = ResizeMode.Max
                    }));
                }

                var encoder = new WebpEncoder
                {
                    Quality = quality,
                    Method = WebpEncodingMethod.BestQuality
                };

                await image.SaveAsync(fullPath, encoder);
            }

            var info = new FileInfo(fullPath);
            var publicUrl = "/" + Path.Combine(subfolder, fileName).Replace("\\", "/");

            return new CleanerImageUploadResultDto
            {
                Url = publicUrl,
                SizeBytes = info.Length
            };
        }

        private void DeleteFileIfExists(string? publicUrl)
        {
            if (string.IsNullOrWhiteSpace(publicUrl))
                return;

            var basePath = _configuration["FileUpload:Path"];
            if (string.IsNullOrWhiteSpace(basePath))
                return;

            var relative = publicUrl.TrimStart('/');
            var fullPath = Path.Combine(basePath, relative);

            try
            {
                if (System.IO.File.Exists(fullPath))
                    System.IO.File.Delete(fullPath);
            }
            catch
            {
                // ignore file delete errors
            }
        }

        private static CleanerDetailDto MapToDetail(Cleaner cleaner, List<CleanerAssignedOrderDto> assignedOrders)
        {
            return new CleanerDetailDto
            {
                Id = cleaner.Id,
                FirstName = cleaner.FirstName,
                LastName = cleaner.LastName,
                Age = cleaner.Age,
                Experience = cleaner.Experience,
                IsExperienced = cleaner.IsExperienced,
                Phone = cleaner.Phone,
                Email = cleaner.Email,
                Location = cleaner.Location,
                Availability = cleaner.Availability,
                AlreadyWorkedWithUs = cleaner.AlreadyWorkedWithUs,
                Nationality = cleaner.Nationality,
                Ranking = cleaner.Ranking,
                PhotoUrl = cleaner.PhotoUrl,
                IsActive = cleaner.IsActive,
                RestrictedReason = cleaner.RestrictedReason,
                Allergies = cleaner.Allergies,
                Restrictions = cleaner.Restrictions,
                MainNote = cleaner.MainNote,
                DocumentUrl = cleaner.DocumentUrl,
                DocumentType = cleaner.DocumentType,
                CreatedAt = cleaner.CreatedAt,
                UpdatedAt = cleaner.UpdatedAt,
                CreatedByAdminId = cleaner.CreatedByAdminId,
                CreatedByAdminName = cleaner.CreatedByAdmin != null
                    ? $"{cleaner.CreatedByAdmin.FirstName} {cleaner.CreatedByAdmin.LastName}".Trim()
                    : null,
                Notes = cleaner.Notes.Select(n => new CleanerNoteDto
                {
                    Id = n.Id,
                    CleanerId = n.CleanerId,
                    AdminId = n.AdminId,
                    AdminDisplayName = !string.IsNullOrWhiteSpace(n.AdminDisplayName)
                        ? n.AdminDisplayName
                        : (n.Admin != null ? $"{n.Admin.FirstName} {n.Admin.LastName}".Trim() : null),
                    Text = n.Text,
                    OrderId = n.OrderId,
                    OrderPerformance = n.OrderPerformance,
                    CreatedAt = n.CreatedAt
                }).ToList(),
                AssignedOrders = assignedOrders
            };
        }
    }
}

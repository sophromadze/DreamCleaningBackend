using System.Security.Claims;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Helpers.Contracts;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Contracts
{
    /// <summary>Thrown when the matrix refuses an action; surfaced as a 403.</summary>
    public class ContractForbiddenException : Exception
    {
        public ContractForbiddenException(string message) : base(message) { }
    }

    /// <summary>
    /// Enforces <see cref="ContractPermissionMatrix"/> at the API layer.
    ///
    /// The officer title is read from the DATABASE, not from the JWT, on every check. Putting it
    /// in the token would mean an existing session kept its old authority until the user logged
    /// out and back in — so revoking a CTO would not actually revoke anything until they felt like
    /// re-authenticating. Contract endpoints are low-traffic; a keyed lookup per request is the
    /// right trade.
    ///
    /// Hiding a button is never the enforcement. Every gated controller action calls
    /// <see cref="EnsureCanAsync"/>, so a hand-rolled request from an unauthorized account gets
    /// the same 403 the UI implies.
    /// </summary>
    public class ContractAuthorizationService
    {
        private readonly ApplicationDbContext _context;
        private readonly IHttpContextAccessor _httpContext;

        public ContractAuthorizationService(ApplicationDbContext context, IHttpContextAccessor httpContext)
        {
            _context = context;
            _httpContext = httpContext;
        }

        public int CurrentUserId
        {
            get
            {
                var raw = _httpContext.HttpContext?.User?.FindFirst("UserId")?.Value;
                return int.TryParse(raw, out var id) ? id : 0;
            }
        }

        /// <summary>Role and title straight from the row, so a demotion takes effect immediately.</summary>
        public async Task<(UserRole Role, OrgTitle Title)> GetCurrentIdentityAsync()
        {
            var userId = CurrentUserId;
            if (userId == 0) return (UserRole.Customer, OrgTitle.None);

            var row = await _context.Users
                .Where(u => u.Id == userId)
                .Select(u => new { u.Role, u.OrgTitle, u.IsActive })
                .FirstOrDefaultAsync();

            // A deactivated account keeps no authority here regardless of what it used to hold.
            if (row == null || !row.IsActive) return (UserRole.Customer, OrgTitle.None);
            return (row.Role, row.OrgTitle);
        }

        public async Task<ContractAuthority?> GetAuthorityAsync()
        {
            var (role, title) = await GetCurrentIdentityAsync();
            return ContractPermissionMatrix.Resolve(role, title);
        }

        public async Task<bool> CanAsync(ContractAction action)
        {
            var authority = await GetAuthorityAsync();
            return authority.HasValue && ContractPermissionMatrix.Can(authority.Value, action);
        }

        /// <summary>Throws <see cref="ContractForbiddenException"/> when the matrix says no.</summary>
        public async Task EnsureCanAsync(ContractAction action)
        {
            if (await CanAsync(action)) return;

            var authority = await GetAuthorityAsync();
            throw new ContractForbiddenException(authority switch
            {
                null => "Your account does not have access to the Contracts module.",
                ContractAuthority.Manager =>
                    $"{Describe(action)} requires the CEO or CTO title.",
                ContractAuthority.CEO =>
                    $"{Describe(action)} is restricted to the CTO.",
                _ => $"{Describe(action)} is not permitted for your account."
            });
        }

        /// <summary>The id of the account holding CTO right now, or null in bootstrap mode.</summary>
        public async Task<int?> GetCurrentCtoUserIdAsync()
        {
            return await _context.Users
                .Where(u => u.OrgTitle == OrgTitle.CTO && u.IsActive)
                .Select(u => (int?)u.Id)
                .FirstOrDefaultAsync();
        }

        /// <summary>
        /// The full capability set for the signed-in account, so the Angular panel can render
        /// exactly the buttons the API would honour rather than maintaining its own copy of the
        /// matrix.
        /// </summary>
        public async Task<Dictionary<string, bool>> DescribeCapabilitiesAsync()
        {
            var authority = await GetAuthorityAsync();
            var map = new Dictionary<string, bool>();
            foreach (ContractAction action in Enum.GetValues<ContractAction>())
            {
                map[ToCamelCase(action.ToString())] =
                    authority.HasValue && ContractPermissionMatrix.Can(authority.Value, action);
            }
            return map;
        }

        private static string Describe(ContractAction action) => action switch
        {
            ContractAction.BackToEdit => "Editing a generated contract",
            ContractAction.CreateRevision => "Creating a revision",
            ContractAction.CreateAmendment => "Creating an amendment",
            ContractAction.DeleteContract => "Deleting a contract",
            ContractAction.RestoreContract => "Restoring a contract",
            ContractAction.AssignOrgTitle => "Assigning an officer title",
            ContractAction.ToggleBusinessFlag => "Changing the business flag",
            ContractAction.SignAsContractor => "Signing as the contractor",
            _ => "That action"
        };

        private static string ToCamelCase(string value) =>
            string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value.Substring(1);
    }
}

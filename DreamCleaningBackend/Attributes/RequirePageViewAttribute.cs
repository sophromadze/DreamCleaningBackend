using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DreamCleaningBackend.Attributes
{
    /// <summary>
    /// Gates an action to a restricted admin page. SuperAdmin always passes (any method). A regular
    /// Admin passes only for safe read requests (GET/HEAD) AND only when the SuperAdmin has granted
    /// them view access to this page key. Everyone else is forbidden. Mutations stay restricted to
    /// SuperAdmin via the usual <c>[Authorize(Roles = "SuperAdmin")]</c> on those actions.
    ///
    /// The grant is checked live against the database on every request, so revoking access takes
    /// effect immediately regardless of the caller's (possibly stale) JWT.
    /// </summary>
    public class RequirePageViewAttribute : AuthorizeAttribute, IAsyncAuthorizationFilter
    {
        // An endpoint may back more than one page (e.g. /statistics feeds both the statistics
        // page and the finances page); a grant to ANY of the listed keys unlocks reads.
        private readonly string[] _pageKeys;

        public RequirePageViewAttribute(params string[] pageKeys)
        {
            _pageKeys = pageKeys;
        }

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var user = context.HttpContext.User;

            if (user.Identity?.IsAuthenticated != true)
            {
                context.Result = new UnauthorizedResult();
                return;
            }

            var roleClaim = user.FindFirst("Role")?.Value;
            if (!Enum.TryParse<UserRole>(roleClaim, out var userRole))
            {
                context.Result = new ForbidResult();
                return;
            }

            // SuperAdmin has full access to every page and every method.
            if (userRole == UserRole.SuperAdmin)
                return;

            // Granted Admins get read-only access: GET/HEAD only.
            if (userRole == UserRole.Admin)
            {
                var method = context.HttpContext.Request.Method;
                var isReadOnly = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
                if (isReadOnly && int.TryParse(user.FindFirst("UserId")?.Value, out var userId))
                {
                    var pageAccessService = context.HttpContext.RequestServices
                        .GetService(typeof(IPageAccessService)) as IPageAccessService;

                    if (pageAccessService != null)
                    {
                        var granted = await pageAccessService.GetGrantedPagesAsync(userId);
                        if (_pageKeys.Any(granted.Contains))
                            return;
                    }
                }
            }

            context.Result = new ForbidResult();
        }
    }
}

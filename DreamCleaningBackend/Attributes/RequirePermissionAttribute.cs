using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;

namespace DreamCleaningBackend.Attributes
{
    public class RequirePermissionAttribute : AuthorizeAttribute, IAuthorizationFilter
    {
        private readonly Permission _permission;

        public RequirePermissionAttribute(Permission permission)
        {
            _permission = permission;
        }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var user = context.HttpContext.User;

            if (!user.Identity.IsAuthenticated)
            {
                context.Result = new UnauthorizedResult();
                return;
            }

            // Get user role from claims
            var roleClaim = user.FindFirst("Role")?.Value;
            if (!Enum.TryParse<UserRole>(roleClaim, out var userRole))
            {
                context.Result = new ForbidResult();
                return;
            }

            var permissionService = context.HttpContext.RequestServices
                .GetService(typeof(IPermissionService)) as IPermissionService;

            if (!permissionService.HasPermission(userRole, _permission))
            {
                context.Result = new ForbidResult();
            }
        }
    }
}

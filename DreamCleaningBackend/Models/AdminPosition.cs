namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Sub-type of an <see cref="UserRole.Admin"/> staff member. Deliberately NOT a UserRole value:
    /// making Manager a role would mean auditing every [Authorize(Roles = "Admin,SuperAdmin")]
    /// attribute, PermissionService entry and frontend role string in the codebase, and a missed one
    /// silently locks a manager out. As a separate column the position changes nothing about what a
    /// staff member may DO — it only decides which side of the per-order bonus they earn (see
    /// <see cref="DreamCleaningBackend.Helpers.AdminBonusAttribution"/>) and whether administrators
    /// can be attached to them.
    ///
    /// Meaningless for every other role; a Customer/Moderator/SuperAdmin row simply carries the
    /// default. Non-nullable on purpose — a null "unset" state would need a fallback rule at every
    /// read site, and Administrator is the right answer for every admin that existed before this
    /// distinction did.
    /// </summary>
    public enum AdminPosition
    {
        Administrator = 0,
        Manager = 1
    }
}

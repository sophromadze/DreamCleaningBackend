namespace DreamCleaningBackend.Models
{
    [Flags]
    public enum Permission
    {
        View = 1,
        Create = 2,
        Update = 4,
        Delete = 8,
        Activate = 16,
        Deactivate = 32,
        ManageRoles = 64,
        ManageSuperAdminRoles = 128
    }
}
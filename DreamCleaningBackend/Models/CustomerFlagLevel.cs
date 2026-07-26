namespace DreamCleaningBackend.Models
{
    // Internal, admin-only "problem customer" signal. The flag lives on the User and is the
    // single source of truth — flagging an order is just a shortcut that flags the order's
    // owning user, and every one of that user's orders derives its tint from this value.
    // None (0) is the default so existing rows carry no flag after migration.
    public enum CustomerFlagLevel
    {
        None = 0,
        Yellow = 1,  // caution
        Red = 2      // serious
    }
}

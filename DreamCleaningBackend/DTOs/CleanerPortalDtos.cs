using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.DTOs
{
    /// <summary>
    /// Who is looking at the portal, and in which of its two modes. Answered in one call so the
    /// page never has to guess from the JWT: the cleaner link lives in the database (an admin can
    /// create or remove it at any time) and a token minted before the link existed would be stale.
    /// </summary>
    public class CleanerPortalContextDto
    {
        /// <summary>True for a Cleaner-role account: their own jobs, minimal detail.</summary>
        public bool IsCleanerView { get; set; }

        /// <summary>
        /// True for an Admin or SuperAdmin: every cleaning in the system, full detail on open.
        /// Named for the VIEW rather than a role - it grants the same thing to two of them, and a
        /// flag called IsSuperAdminView would have quietly told the next reader otherwise.
        /// </summary>
        public bool IsSystemWideView { get; set; }

        /// <summary>The Cleaner record behind this account, when one is linked. Null for a
        /// SuperAdmin, and null for a Cleaner-role account nobody has linked yet - which is the
        /// case the page has to explain rather than render as "no jobs".</summary>
        public int? CleanerId { get; set; }

        public string? CleanerName { get; set; }

        /// <summary>
        /// The language to RENDER IN, already resolved server-side (CleanerLanguage.Resolve) from
        /// the cleaner's own choice falling back to their nationality. The client is told the
        /// answer rather than the ingredients, so the page and the assignment email a cleaner gets
        /// cannot end up in different languages.
        /// </summary>
        public string Language { get; set; } = Helpers.CleanerLanguage.English;

        /// <summary>
        /// The cleaner's EXPLICIT choice, or null when they are following their nationality. The
        /// language picker needs the difference: a null shows as "Automatic", and re-saving it
        /// would otherwise pin them to today's default forever.
        /// </summary>
        public string? PreferredLanguage { get; set; }
    }

    /// <summary>The cleaner's language choice. Null/blank clears it back to "follow nationality".</summary>
    public class SetCleanerLanguageDto
    {
        [StringLength(5)]
        public string? Language { get; set; }
    }

    /// <summary>
    /// A job as a CLEANER may see it. Deliberately the same field set the cleaner assignment email
    /// already sends them and nothing more - no pricing, no invoicing, no free-text customer notes,
    /// no other customer data. Adding a field here widens what every cleaner can see, so it should
    /// be matched against Services/EmailService.BuildCleanerAssignmentEmail first.
    /// </summary>
    public class CleanerPortalJobDto
    {
        public int OrderId { get; set; }

        /// <summary>NY wall-clock service date (the same value the whole app schedules on).</summary>
        public DateTime ServiceDate { get; set; }

        /// <summary>"HH:mm" - formatted for display by the client, as everywhere else.</summary>
        public string ServiceTime { get; set; } = string.Empty;

        /// <summary>Effective service-type name, custom label included (GetDisplayServiceTypeName).</summary>
        public string ServiceTypeName { get; set; } = string.Empty;

        /// <summary>Priced service lines by name and quantity. No costs and no durations - what
        /// the work IS, not what it was sold for.</summary>
        public List<CleanerPortalServiceLineDto> Services { get; set; } = new();

        /// <summary>Extras the cleaner is expected to perform, named and quantified exactly as the
        /// assignment email lists them (Cleaning Supplies and Extra Cleaners excluded there too).</summary>
        public List<string> ExtraServices { get; set; } = new();

        /// <summary>Customer FIRST name only, matching the assignment email.</summary>
        public string CustomerName { get; set; } = string.Empty;

        /// <summary>Single-line service address, assembled the way the assignment email does.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// Whether the cleaner has to bring cleaning solutions/supplies. There is no dedicated
        /// column for this anywhere: the assignment email derives it from the customer having
        /// bought the "Cleaning Supplies" extra, and this reads the same source
        /// (CustomerSupplyChecklist.HasCleaningSuppliesExtra) so the portal and the email can
        /// never disagree about what to load into the car.
        /// </summary>
        public bool BringCleaningSupplies { get; set; }

        /// <summary>
        /// Whether the cleaner has to bring the essentials - paper towels, garbage bags, a toilet
        /// brush and a broom. Same shape and same source as <see cref="BringCleaningSupplies"/>
        /// (CleanerJobView.RequiresCleanerToBringEssentials): the customer bought the "Cleaning
        /// Essentials" extra, so we bring those four items and their own checklist no longer
        /// lists them. A VACUUM is still the separate Vacuum Cleaner extra.
        /// </summary>
        public bool BringCleaningEssentials { get; set; }

        /// <summary>
        /// The hours this cleaner is actually expected to work: THEIR payroll line, straight from
        /// CleanerPayrollCalculator, so the figure on the page is the figure they are paid for and
        /// the one their assignment email already quoted. Never Order.TotalDuration, which is total
        /// cleaner-minutes across everybody on the job.
        /// </summary>
        public int ServiceDurationMinutes { get; set; }

        /// <summary>"Apartment" / "House", or null on a legacy order that never recorded it.</summary>
        public string? PropertyType { get; set; }

        /// <summary>Levels to clean, for a house. Null for an apartment - stairs are the point.</summary>
        public int? LevelsQuantity { get; set; }

        /// <summary>
        /// Floor types on the job, already flattened to display text (the column stores a CSV that
        /// may carry an "other:free text" entry, and Order.FloorTypeOther beside it). What is
        /// underfoot decides which products come out of the bag, so the crew reads it before they
        /// arrive.
        /// </summary>
        public List<string> FloorTypes { get; set; } = new();

        /// <summary>
        /// HOW TO GET IN - "doorman", "I will be home", a lockbox code. The assignment email has
        /// always carried it; without it a cleaner is standing outside a locked door with the right
        /// address, which is the one failure the address alone cannot prevent.
        /// </summary>
        public string? EntryMethod { get; set; }

        /// <summary>The customer's own note on the order (Order.SpecialInstructions).</summary>
        public string? CustomerInstructions { get; set; }

        /// <summary>
        /// What the admin typed for THIS cleaner when staffing the job (OrderCleaner.TipsForCleaner
        /// - "visible to cleaners" since it was added). Per-assignment, so it is null on the
        /// SuperAdmin's list, where there is no single cleaner to be addressing.
        /// </summary>
        public string? CleanerInstructions { get; set; }

        /// <summary>
        /// Whether the cleaning has been performed. Drives the calendar's dot - a finished job is a
        /// quiet green mark, work still ahead is the one that has to catch the eye - and whether
        /// there is anything to open: a past job is a record, not a briefing.
        /// </summary>
        public bool IsCompleted { get; set; }
    }

    public class CleanerPortalServiceLineDto
    {
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; }

        /// <summary>
        /// The catalogue key ("bedrooms", "bathrooms", "sqft", "cleaners", "hours", "levels"), so
        /// the client can render "2 Bedrooms" / "Studio" in the reader's own language instead of
        /// printing a stored English name with a multiplication sign after it. Matched on the KEY
        /// and never on the name or the Id, both of which differ between dev and production.
        /// </summary>
        public string? ServiceKey { get; set; }
    }

    /// <summary>
    /// What a Cleaner-role account gets in one call: their current work, and their history.
    ///
    /// HISTORY IS NOW FULL JOBS, not the bare dates it started as (owner's call, 2026-09): a
    /// finished job takes its place in the month calendar beside the upcoming ones, so a cleaner
    /// can see the shape of the month they have worked. The narrowing is in the UI, where a
    /// completed job renders as the card alone with nothing to open - the briefing is for work
    /// still to be done.
    /// </summary>
    public class CleanerPortalMyJobsDto
    {
        public List<CleanerPortalJobDto> Current { get; set; } = new();
        public List<CleanerPortalJobDto> Past { get; set; } = new();
    }

    /// <summary>
    /// One row of the SuperAdmin's system-wide list: every cleaning, past, current and future, for
    /// every cleaner. Carries the cleaner-visible minimum plus the two things a system-wide list is
    /// useless without - who is on the job, and what state it is in.
    /// </summary>
    public class CleanerPortalAdminJobDto : CleanerPortalJobDto
    {
        public string Status { get; set; } = string.Empty;

        /// <summary>Names of the cleaners assigned. Empty means nobody is staffed yet, which on
        /// this page is a fact worth seeing rather than a missing value.</summary>
        public List<string> AssignedCleaners { get; set; } = new();

        /// <summary>Staffing slots the order was priced for. Compared against AssignedCleaners it
        /// is how an under-staffed job is spotted from the list.</summary>
        public int MaidsCount { get; set; }

        public bool IsPaid { get; set; }
    }

    /// <summary>
    /// The SuperAdmin's read-only detail for one order inside the portal: everything a cleaner sees
    /// plus the rest of the order. It reuses <see cref="OrderDto"/> rather than re-listing 80 fields,
    /// so the detail panel here shows the same order the admin panel does; the cleaner-facing
    /// summary rides alongside so the panel can show both halves without re-deriving either.
    ///
    /// READ ONLY on purpose - there is no write endpoint in this controller at all. Editing an order
    /// already exists in the admin orders panel, and a second editor is a second place for the
    /// pricing rules to be got wrong.
    /// </summary>
    public class CleanerPortalOrderDetailDto
    {
        public CleanerPortalJobDto CleanerView { get; set; } = new();

        /// <summary>The full order, assembled by the shared OrderDtoMapper.</summary>
        public OrderDto Order { get; set; } = new();

        public List<CleanerPortalAssignedCleanerDto> AssignedCleaners { get; set; } = new();

        /// <summary>Internal admin note on the order, when one exists. SuperAdmin-only data, which
        /// is why it is on this DTO and not on the cleaner-facing one.</summary>
        public string? AdminNotes { get; set; }
    }

    public class CleanerPortalAssignedCleanerDto
    {
        public int CleanerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public DateTime AssignedAt { get; set; }
        public DateTime? AssignmentNotificationSentAt { get; set; }
    }

    // ─────────────────────────── Admin Cleaners tab ───────────────────────────

    /// <summary>
    /// One Cleaner-role login account, with the cleaner record it drives (if any). The tab exists
    /// to make the link visible and fixable - an account with no link is the failure case it is
    /// there to catch, since that person signs in and correctly sees nothing.
    /// </summary>
    public class CleanerAccountDto
    {
        public int UserId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;

        /// <summary>The account's real address, or null for a no-email account (NoEmailHelper).</summary>
        public string? Email { get; set; }

        /// <summary>The ACCOUNT's own phone, which is routinely null: it is optional at
        /// registration and social sign-in never supplies one. See CleanerPhone.</summary>
        public string? Phone { get; set; }

        public string? ProfilePictureUrl { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>The linked Cleaner record, or null when nobody has linked this account yet.</summary>
        public int? CleanerId { get; set; }
        public string? CleanerName { get; set; }

        /// <summary>The cleaner record's email - after a link it always equals Email, because
        /// linking overwrites it with the address the account actually signs in with.</summary>
        public string? CleanerEmail { get; set; }

        /// <summary>
        /// The linked cleaner RECORD's phone. Sent alongside Phone rather than merged into it
        /// because they are two different facts about one person: Phone is what the account was
        /// registered with (often nothing at all), this is the number the office dials, entered on
        /// the Cleaners Dashboard. The tab renders whichever it has - a column reading "-" beside a
        /// record that plainly shows a mobile number reads as a bug in the panel.
        /// </summary>
        public string? CleanerPhone { get; set; }

        public bool CleanerIsActive { get; set; }

        /// <summary>Assignments on the linked cleaner record, so the tab shows at a glance whether
        /// the link points at somebody who actually works.</summary>
        public int AssignedOrdersCount { get; set; }
    }

    /// <summary>A cleaner record offered for linking, with the reason it may not be available.</summary>
    public class LinkableCleanerDto
    {
        public int CleanerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public bool IsActive { get; set; }

        /// <summary>Set when this cleaner is already linked to a DIFFERENT account. Such rows are
        /// still returned, and named, rather than silently absent: "why is X not in the list" is
        /// the question the tab has to be able to answer.</summary>
        public int? LinkedUserId { get; set; }
        public string? LinkedUserEmail { get; set; }
    }

    /// <summary>A non-cleaner account offered for promotion into the Cleaner role.</summary>
    public class PromotableUserDto
    {
        public int UserId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string Role { get; set; } = string.Empty;
    }

    public class LinkCleanerAccountDto
    {
        [Required]
        public int CleanerId { get; set; }
    }
}

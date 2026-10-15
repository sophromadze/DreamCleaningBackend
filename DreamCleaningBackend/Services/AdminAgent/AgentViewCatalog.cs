using System.Text;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Commercial;

namespace DreamCleaningBackend.Services.AdminAgent
{
    /// <summary>One column of an <c>agent_*</c> view: its name, the SQL that produces it, a type
    /// word for the prompt, and what it means.</summary>
    public sealed record AgentViewColumn(string Name, string Sql, string Type, string Meaning);

    /// <summary>One read-only view the analytics agent's run_sql tool may query.</summary>
    public sealed record AgentView(
        string Name, string Meaning, string From, IReadOnlyList<AgentViewColumn> Columns, string? Where = null);

    /// <summary>
    /// THE definition of the <c>agent_*</c> views (2026-10) — the only database surface the
    /// analytics agent's ad-hoc SQL can reach. Two things are generated from this one list so they
    /// cannot drift apart:
    /// <list type="bullet">
    /// <item>the <c>CREATE VIEW</c> statements (<see cref="CreateViewSql"/>), pasted into a migration
    /// — <c>AgentViewCatalogTests</c> fails if a view here differs from what a migration creates,
    /// so changing a view means a new migration;</item>
    /// <item>the schema section of the agent's system prompt (<see cref="PromptSchema"/>).</item>
    /// </list>
    ///
    /// What is left OUT is the point of the views. No password hashes, salts, security stamps,
    /// refresh / reset / verification / payment-access tokens, 2FA material, Stripe ids, card
    /// brand/last-4, cleaners' payout details, customer e-mail, phone or street address (ZIP, city
    /// and state stay for geography), door entry instructions, and no free-text notes, messages or
    /// reasons — those are customer- or staff-written and would be a prompt-injection channel into
    /// the agent. Customer first/last name is kept so "top customers" questions can be answered.
    ///
    /// Enum columns are exposed as their NAMES, generated from the C# enums, so a new enum value
    /// shows up as its name instead of as a bare number nobody can read.
    /// </summary>
    public static class AgentViewCatalog
    {
        public const string Prefix = "agent_";

        public static IReadOnlyList<AgentView> Views { get; } = Build();

        /// <summary>Every view name, in creation order.</summary>
        public static IEnumerable<string> ViewNames => Views.Select(v => v.Name);

        /// <summary>
        /// <c>CREATE OR REPLACE SQL SECURITY DEFINER VIEW</c> for one view. DEFINER security is what
        /// lets a user holding SELECT on the view alone read it without any right on the tables.
        /// Identifiers are back-quoted with the EF table names, so the statement is the same on a
        /// case-insensitive Windows dev server and a case-sensitive Linux one.
        /// </summary>
        public static string CreateViewSql(AgentView view)
        {
            var sb = new StringBuilder();
            sb.Append("CREATE OR REPLACE SQL SECURITY DEFINER VIEW `").Append(view.Name).Append("` AS SELECT ");
            sb.Append(string.Join(", ", view.Columns.Select(c => $"{c.Sql} AS `{c.Name}`")));
            sb.Append(" FROM ").Append(view.From);
            if (!string.IsNullOrWhiteSpace(view.Where))
                sb.Append(" WHERE ").Append(view.Where);
            return sb.ToString();
        }

        public static string DropViewSql(AgentView view) => $"DROP VIEW IF EXISTS `{view.Name}`";

        /// <summary>The view schemas as the system prompt shows them.</summary>
        public static string PromptSchema()
        {
            var sb = new StringBuilder();
            foreach (var v in Views)
            {
                sb.Append("### ").Append(v.Name).Append('\n').Append(v.Meaning).Append('\n');
                foreach (var c in v.Columns)
                    sb.Append("- ").Append(c.Name).Append(" (").Append(c.Type).Append("): ").Append(c.Meaning).Append('\n');
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The GRANT statements for the read-only database user: SELECT on each view, nothing else.
        /// MariaDB has no wildcard over table names, so every view is named.
        /// </summary>
        public static IEnumerable<string> GrantSql(string database, string user, string host) =>
            Views.Select(v => $"GRANT SELECT ON `{database}`.`{v.Name}` TO '{user}'@'{host}';");

        // ───────────────────────────────────────────────────────────────────────────────────────

        private static AgentViewColumn C(string name, string sql, string type, string meaning) => new(name, sql, type, meaning);

        /// <summary>SQL CASE that turns a stored enum int into the enum member's name.</summary>
        internal static string EnumName<TEnum>(string column) where TEnum : struct, Enum
        {
            var sb = new StringBuilder("CASE ").Append(column);
            foreach (var value in Enum.GetValues<TEnum>())
                sb.Append(" WHEN ").Append(Convert.ToInt64(value)).Append(" THEN '").Append(value).Append('\'');
            sb.Append(" ELSE CONCAT('Unknown(', ").Append(column).Append(", ')') END");
            return sb.ToString();
        }

        internal static string EnumNames<TEnum>() where TEnum : struct, Enum =>
            string.Join(", ", Enum.GetValues<TEnum>().Select(v => v.ToString()));

        private static List<AgentView> Build()
        {
            // Mirrors Helpers/OrderPaymentFilter (and the hand-written copy in the Statistics
            // report): an order counts as paid for reporting when Stripe settled it, or it was
            // paid outside Stripe — except Invoice, which settles only once its invoice is paid.
            const string settled =
                "(o.`IsPaid` = 1 OR (o.`PaymentMethod` <> 0 AND (o.`PaymentMethod` <> 5 OR o.`InvoicePaidAt` IS NOT NULL)))";

            return new List<AgentView>
            {
                new("agent_orders",
                    "One row per order (booking). Money columns are USD as stored on the order. For revenue, tax, net or payroll FIGURES use the canonical tools: they apply the refund allocation, the tax-kept-outside-Stripe rule and the invoice rules that plain sums here do not.",
                    "`Orders` o LEFT JOIN `ServiceTypes` st ON st.`Id` = o.`ServiceTypeId` LEFT JOIN `Subscriptions` sub ON sub.`Id` = o.`SubscriptionId`",
                    new[]
                    {
                        C("order_id", "o.`Id`", "int", "Order id."),
                        C("customer_id", "o.`UserId`", "int", "The customer account (agent_customers.customer_id)."),
                        C("service_date", "CAST(o.`ServiceDate` AS DATE)", "date", "Day of the cleaning (New York calendar day). Reports bucket by THIS date."),
                        C("service_time", "o.`ServiceTime`", "time", "Start time of the cleaning."),
                        C("booked_at_utc", "o.`CreatedAt`", "datetime", "When the booking was created, UTC."),
                        C("status", "o.`Status`", "text", "Pending, Active, Done, Cancelled or Refunded. Reports treat Done (and Refunded after Done) as performed."),
                        C("status_before_refund", "o.`StatusBeforeRefund`", "text", "Status the order had when it was fully refunded (Done = the cleaning happened)."),
                        C("is_settled", settled, "bool", "Paid for reporting purposes (same rule as Statistics): paid by card, or recorded as paid outside Stripe; an Invoice order only once its invoice is paid."),
                        C("is_paid_stripe", "o.`IsPaid`", "bool", "Settled through Stripe. Cash/Zelle/Check orders are 0 here by design - use is_settled."),
                        C("payment_method", EnumName<PaymentMethod>("o.`PaymentMethod`"), "text", $"How the order is paid: {EnumNames<PaymentMethod>()}. Normal = card through Stripe."),
                        C("invoice_paid_at", "o.`InvoicePaidAt`", "datetime", "When the commercial invoice covering this order was paid in full."),
                        C("service_type_id", "o.`ServiceTypeId`", "int", "Service type id (agent_service_types)."),
                        C("service_type_key", "st.`ServiceKey`", "text", "Stable service-type KEY. Always match service types by this key, never by id or name. May be NULL on types nobody has keyed yet."),
                        C("service_type_name", "st.`Name`", "text", "Service type display name (for labels only)."),
                        C("is_custom_service_type", "st.`IsCustom`", "bool", "Pre-Arranged / custom-priced type (admin-entered total)."),
                        C("subscription_id", "o.`SubscriptionId`", "int", "Plan tier the order was booked on (One Time is a tier too)."),
                        C("subscription_name", "sub.`Name`", "text", "Plan tier name: One Time, Weekly, Bi-Weekly, Monthly ..."),
                        C("subscription_days", "sub.`SubscriptionDays`", "int", "Days between visits of the tier; 0 = one-time."),
                        C("is_recurring_plan", "(o.`SubscriptionId` IS NOT NULL AND sub.`SubscriptionDays` > 0)", "bool", "Booked on a recurring plan (Helpers/RecurringPlanRule: tier days > 0). Never infer this from a subscription discount."),
                        C("is_generated_by_recurring_series", "o.`IsGeneratedByRecurringSeries`", "bool", "Created automatically by a recurring series (agent_recurring_series)."),
                        C("recurring_series_id", "o.`RecurringSeriesId`", "int", "The recurring series that generated it."),
                        C("is_commercial", "o.`IsCommercial`", "bool", "Commercial (business) order vs regular residential."),
                        C("business_client_id", "o.`ContractClientId`", "int", "Linked business client (agent_business_clients)."),
                        C("subtotal", "o.`SubTotal`", "decimal", "Price before discounts and tax."),
                        C("promo_discount", "o.`DiscountAmount`", "decimal", "Promo code / first-time / special-offer discount."),
                        C("subscription_discount", "o.`SubscriptionDiscountAmount`", "decimal", "Recurring-plan discount."),
                        C("loyalty_discount", "o.`LoyaltyDiscountAmount`", "decimal", "Loyalty discount."),
                        C("gift_card_used", "o.`GiftCardAmountUsed`", "decimal", "Paid with a gift card (a payment, not a discount)."),
                        C("points_discount", "o.`PointsRedeemedDiscount`", "decimal", "Bubble Points redeemed."),
                        C("reward_balance_used", "o.`RewardBalanceUsed`", "decimal", "Reward credit used."),
                        C("sales_tax", "o.`Tax`", "decimal", "Sales tax charged on the DISCOUNTED subtotal."),
                        C("tips", "o.`Tips`", "decimal", "Customer tip for the cleaners (pass-through, not revenue)."),
                        C("legacy_company_tips", "o.`CompanyDevelopmentTips`", "decimal", "Retired company-development tip; only on old orders."),
                        C("total", "o.`Total`", "decimal", "What the customer pays: discounted subtotal + tax + tips."),
                        C("total_refunded", "o.`TotalRefundedAmount`", "decimal", "Total refunded so far."),
                        C("amount_paid_in_parts", "o.`AmountPaid`", "decimal", "Sum of settled part-payments (0 on an ordinary fully-paid order)."),
                        C("promo_code", "o.`PromoCode`", "text", "Promo code entered, if any."),
                        C("special_offer_name", "o.`SpecialOfferName`", "text", "Special offer applied, if any."),
                        C("cleaner_hourly_rate", "o.`CleanerHourlyRate`", "decimal", "Default hourly wage on the order."),
                        C("cleaner_total_salary", "o.`CleanerTotalSalary`", "decimal", "Total cleaner wages for the order as kept up to date by the payroll calculator (incl. unfilled cleaner slots). This is the figure every report uses."),
                        C("total_duration_minutes", "o.`TotalDuration`", "decimal", "Total job duration in minutes (all cleaners together unless the type is priced per cleaner-hour)."),
                        C("maids_count", "o.`MaidsCount`", "int", "Number of cleaners the order is priced/staffed for."),
                        C("booking_admin_id", "o.`BonusBookerId`", "int", "BOOKING ADMIN: the staff member credited with booking it (agent_staff.user_id). This is who 'admin X booked' means, and what Outgoing Payments -> Admins counts."),
                        C("created_by_admin_id", "o.`BookedByAdminUserId`", "int", "Staff account that created the order in the admin booking flow (NULL = customer booked it; NULL on orders before July 2026)."),
                        C("assigned_admin_id", "o.`AssignedAdminId`", "int", "Admin currently responsible for the order (can change)."),
                        C("bonus_manager_id", "o.`BonusManagerId`", "int", "Manager earning a team share of the booking."),
                        C("is_new_customer_order", "o.`IsNewCustomerOrder`", "bool", "The customer's first-ever real booking (decided once at creation)."),
                        C("city", "o.`City`", "text", "Service city / borough."),
                        C("state", "o.`State`", "text", "Service state."),
                        C("zip_code", "o.`ZipCode`", "text", "Service ZIP code."),
                        C("property_type", "o.`PropertyType`", "text", "Apartment/condo or house/townhouse."),
                        C("bedrooms", "o.`BedroomsQuantity`", "int", "Bedrooms (display copy)."),
                        C("bathrooms", "o.`BathroomsQuantity`", "int", "Bathrooms (display copy)."),
                        C("levels", "o.`LevelsQuantity`", "int", "House levels cleaned."),
                        C("acquisition_channel", "o.`AcquisitionChannel`", "text", "First-touch marketing channel stored at booking (raw; Order Sources tool classifies properly)."),
                        C("acquisition_source", "o.`AcquisitionSource`", "text", "First-touch source."),
                        C("acquisition_medium", "o.`AcquisitionMedium`", "text", "First-touch medium."),
                        C("acquisition_campaign", "o.`AcquisitionCampaign`", "text", "First-touch campaign."),
                        C("is_late_cancellation", "o.`IsLateCancellation`", "bool", "Cancelled inside the fee window."),
                        C("is_hidden_in_admin_list", "o.`IsHidden`", "bool", "Hidden from the admin Orders list only. Reports do NOT exclude hidden orders.")
                    }),

                new("agent_order_services",
                    "Priced service lines of each order (bedrooms, bathrooms, square feet, hours, cleaners ...).",
                    "`OrderServices` os JOIN `Services` s ON s.`Id` = os.`ServiceId`",
                    new[]
                    {
                        C("order_id", "os.`OrderId`", "int", "Order id."),
                        C("service_id", "os.`ServiceId`", "int", "Service id."),
                        C("service_key", "s.`ServiceKey`", "text", "Stable service key (bedrooms, bathrooms, sqft, levels, hours, cleaners ...)."),
                        C("service_name", "s.`Name`", "text", "Display name."),
                        C("relation_type", "s.`ServiceRelationType`", "text", "'cleaner' / 'hours' etc. - how the line drives duration."),
                        C("quantity", "os.`Quantity`", "int", "Quantity selected."),
                        C("cost", "os.`Cost`", "decimal", "Line price."),
                        C("duration_minutes", "os.`Duration`", "decimal", "Line duration in minutes.")
                    }),

                new("agent_order_extras",
                    "Extra services on each order (oven, fridge, windows, deep cleaning, same day ...).",
                    "`OrderExtraServices` oe JOIN `ExtraServices` es ON es.`Id` = oe.`ExtraServiceId`",
                    new[]
                    {
                        C("order_id", "oe.`OrderId`", "int", "Order id."),
                        C("extra_service_id", "oe.`ExtraServiceId`", "int", "Extra service id."),
                        C("extra_key", "es.`ExtraServiceKey`", "text", "Stable extra key."),
                        C("extra_name", "es.`Name`", "text", "Display name."),
                        C("is_deep_cleaning", "es.`IsDeepCleaning`", "bool", "The Deep Cleaning upgrade."),
                        C("is_super_deep_cleaning", "es.`IsSuperDeepCleaning`", "bool", "The Super Deep Cleaning upgrade."),
                        C("is_same_day", "es.`IsSameDayService`", "bool", "Same-day service surcharge."),
                        C("quantity", "oe.`Quantity`", "int", "Quantity."),
                        C("hours", "oe.`Hours`", "decimal", "Hours (hour-based extras)."),
                        C("cost", "oe.`Cost`", "decimal", "Line price (0 on custom-priced orders, where extras are informational)."),
                        C("duration_minutes", "oe.`Duration`", "decimal", "Line duration in minutes.")
                    }),

                new("agent_service_types",
                    "The service catalogue's service types.",
                    "`ServiceTypes` st",
                    new[]
                    {
                        C("service_type_id", "st.`Id`", "int", "Id."),
                        C("service_type_key", "st.`ServiceKey`", "text", "Stable key - match by this."),
                        C("name", "st.`Name`", "text", "Display name."),
                        C("is_active", "st.`IsActive`", "bool", "Offered for booking."),
                        C("is_custom", "st.`IsCustom`", "bool", "Custom / Pre-Arranged pricing."),
                        C("base_price", "st.`BasePrice`", "decimal", "Base price.")
                    }),

                new("agent_services",
                    "Catalogue services (the priced inputs of a service type).",
                    "`Services` s",
                    new[]
                    {
                        C("service_id", "s.`Id`", "int", "Id."),
                        C("service_type_id", "s.`ServiceTypeId`", "int", "Owning service type."),
                        C("service_key", "s.`ServiceKey`", "text", "Stable key."),
                        C("name", "s.`Name`", "text", "Display name."),
                        C("relation_type", "s.`ServiceRelationType`", "text", "How it drives duration."),
                        C("cost", "s.`Cost`", "decimal", "Unit cost."),
                        C("is_active", "s.`IsActive`", "bool", "Active.")
                    }),

                new("agent_extra_services",
                    "Catalogue extra services.",
                    "`ExtraServices` es",
                    new[]
                    {
                        C("extra_service_id", "es.`Id`", "int", "Id."),
                        C("service_type_id", "es.`ServiceTypeId`", "int", "Owning service type (NULL/any when available for all)."),
                        C("extra_key", "es.`ExtraServiceKey`", "text", "Stable key."),
                        C("name", "es.`Name`", "text", "Display name."),
                        C("price", "es.`Price`", "decimal", "Price."),
                        C("is_available_for_all", "es.`IsAvailableForAll`", "bool", "Offered on every service type."),
                        C("is_active", "es.`IsActive`", "bool", "Active.")
                    }),

                new("agent_subscriptions",
                    "Plan tiers (One Time, Weekly, Bi-Weekly, Monthly).",
                    "`Subscriptions` sub",
                    new[]
                    {
                        C("subscription_id", "sub.`Id`", "int", "Id."),
                        C("name", "sub.`Name`", "text", "Tier name."),
                        C("subscription_days", "sub.`SubscriptionDays`", "int", "Days between visits; 0 = one-time."),
                        C("is_recurring", "(sub.`SubscriptionDays` > 0)", "bool", "Recurring tier."),
                        C("discount_percentage", "sub.`DiscountPercentage`", "decimal", "Plan discount %."),
                        C("is_active", "sub.`IsActive`", "bool", "Offered.")
                    }),

                new("agent_customers",
                    "Customer accounts (role Customer). No e-mail, phone or address by design.",
                    "`Users` u",
                    new[]
                    {
                        C("customer_id", "u.`Id`", "int", "Account id (agent_orders.customer_id)."),
                        C("first_name", "u.`FirstName`", "text", "First name."),
                        C("last_name", "u.`LastName`", "text", "Last name."),
                        C("created_at_utc", "u.`CreatedAt`", "datetime", "Account created, UTC."),
                        C("is_active", "u.`IsActive`", "bool", "Not blocked."),
                        C("is_deleted", "u.`IsDeleted`", "bool", "Soft-deleted."),
                        C("is_business", "u.`IsBusiness`", "bool", "Business customer flag."),
                        C("current_subscription_id", "u.`SubscriptionId`", "int", "Plan tier the account holds TODAY (not what past orders were booked on)."),
                        C("subscription_expires_at", "u.`SubscriptionExpiryDate`", "datetime", "When that plan lapses."),
                        C("loyalty_discount_percentage", "u.`LoyaltyDiscountPercentage`", "decimal", "Standing loyalty discount %."),
                        C("referred_by_customer_id", "u.`ReferredByUserId`", "int", "Referrer account.")
                    },
                    "u.`Role` = 0"),

                new("agent_staff",
                    "Staff accounts: SuperAdmin, Admin, Moderator. Names only.",
                    "`Users` u",
                    new[]
                    {
                        C("user_id", "u.`Id`", "int", "Account id (agent_orders.booking_admin_id etc.)."),
                        C("first_name", "u.`FirstName`", "text", "First name."),
                        C("last_name", "u.`LastName`", "text", "Last name."),
                        C("role", EnumName<UserRole>("u.`Role`"), "text", "Role."),
                        C("position", EnumName<AdminPosition>("u.`AdminPosition`"), "text", $"Position: {EnumNames<AdminPosition>()}."),
                        C("manager_id", "u.`ManagerId`", "int", "Their manager (agent_staff.user_id)."),
                        C("is_active", "u.`IsActive`", "bool", "Not blocked."),
                        C("is_deleted", "u.`IsDeleted`", "bool", "Soft-deleted.")
                    },
                    "u.`Role` IN (1, 2, 3)"),

                new("agent_cleaners",
                    "Cleaners (the people orders are assigned to). No contact, address, documents or payout details.",
                    "`Cleaners` c",
                    new[]
                    {
                        C("cleaner_id", "c.`Id`", "int", "Id."),
                        C("first_name", "c.`FirstName`", "text", "First name."),
                        C("last_name", "c.`LastName`", "text", "Last name."),
                        C("is_active", "c.`IsActive`", "bool", "Active."),
                        C("ranking", "c.`Ranking`", "int", "Internal ranking."),
                        C("borough", "c.`Borough`", "text", "Home borough."),
                        C("created_at_utc", "c.`CreatedAt`", "datetime", "Added, UTC.")
                    }),

                new("agent_order_cleaners",
                    "Cleaner assignments. Per-cleaner hours and pay come from the get_cleaner_payroll tool (the payroll calculator), never from these columns alone.",
                    "`OrderCleaners` oc",
                    new[]
                    {
                        C("order_id", "oc.`OrderId`", "int", "Order id."),
                        C("cleaner_id", "oc.`CleanerId`", "int", "Cleaner id."),
                        C("assigned_at_utc", "oc.`AssignedAt`", "datetime", "Assigned, UTC."),
                        C("is_paid", "oc.`IsPaid`", "bool", "Payout recorded."),
                        C("paid_amount", "oc.`PaidAmount`", "decimal", "Amount recorded as paid out."),
                        C("paid_at_utc", "oc.`PaidAt`", "datetime", "When paid."),
                        C("paid_via", EnumName<CleanerPaymentMethod>("oc.`PaidVia`"), "text", $"Payout method: {EnumNames<CleanerPaymentMethod>()}."),
                        C("hours_override_minutes", "oc.`SalaryBillableMinutes`", "decimal", "Per-cleaner billable minutes override (NULL = automatic split)."),
                        C("hourly_rate_override", "oc.`SalaryHourlyRate`", "decimal", "Per-cleaner hourly rate override (NULL = order rate)."),
                        C("tips_withheld", "oc.`TipsWithheldFromPayout`", "bool", "Tips withheld from this payout.")
                    }),

                new("agent_order_refunds",
                    "Refunds issued on orders.",
                    "`OrderRefunds` r",
                    new[]
                    {
                        C("refund_id", "r.`Id`", "int", "Id."),
                        C("order_id", "r.`OrderId`", "int", "Order id."),
                        C("amount", "r.`Amount`", "decimal", "Amount."),
                        C("status", "r.`Status`", "text", "Refund status."),
                        C("source", EnumName<RefundSource>("r.`Source`"), "text", "Where the refund came from."),
                        C("created_at_utc", "r.`CreatedAt`", "datetime", "When, UTC.")
                    }),

                new("agent_expenses",
                    "Expense DEFINITIONS (a recurring row repeats every frequency_months). For expense totals in a period use get_company_statistics, which expands recurring rows the way the Finances page does.",
                    "`Expenses` e LEFT JOIN `ExpenseCategories` ec ON ec.`Id` = e.`Category`",
                    new[]
                    {
                        C("expense_id", "e.`Id`", "int", "Id."),
                        C("name", "e.`Name`", "text", "Name."),
                        C("category", "ec.`Name`", "text", "Category."),
                        C("amount", "e.`Amount`", "decimal", "Amount per occurrence, in currency."),
                        C("currency", "e.`Currency`", "text", "USD or GEL."),
                        C("start_date", "e.`StartDate`", "datetime", "First occurrence."),
                        C("end_date", "e.`EndDate`", "datetime", "Last occurrence (NULL = open)."),
                        C("is_recurring", "e.`IsRecurring`", "bool", "Repeats."),
                        C("frequency_months", "e.`FrequencyMonths`", "int", "Months between occurrences."),
                        C("staff_user_id", "e.`StaffUserId`", "int", "Staff member a salary is for.")
                    }),

                new("agent_fx_rates",
                    "Monthly GEL->USD rates used to convert staff bonuses and GEL expenses.",
                    "`MonthlyFinancialSnapshots` f",
                    new[]
                    {
                        C("year", "f.`Year`", "int", "Year."),
                        C("month", "f.`Month`", "int", "Month."),
                        C("usd_per_gel", "f.`UsdPerGel`", "decimal", "USD per 1 GEL."),
                        C("is_finalized", "f.`IsFinalized`", "bool", "Locked.")
                    }),

                new("agent_recurring_series",
                    "Recurring cleaning schedules.",
                    "`RecurringOrderSeries` rs",
                    new[]
                    {
                        C("series_id", "rs.`Id`", "int", "Id."),
                        C("customer_id", "rs.`UserId`", "int", "Customer."),
                        C("template_order_id", "rs.`TemplateOrderId`", "int", "Order it was created from."),
                        C("interval_value", "rs.`IntervalValue`", "int", "Every N units."),
                        C("interval_unit", EnumName<RecurrenceIntervalUnit>("rs.`IntervalUnit`"), "text", "Days, Weeks or Months."),
                        C("anchor_date", "rs.`AnchorDate`", "datetime", "First visit."),
                        C("is_active", "rs.`IsActive`", "bool", "Running."),
                        C("end_date", "rs.`EndDate`", "datetime", "Planned end."),
                        C("stopped_at_utc", "rs.`StoppedAt`", "datetime", "When it was stopped."),
                        C("created_at_utc", "rs.`CreatedAt`", "datetime", "Created, UTC.")
                    }),

                new("agent_leads",
                    "CRM leads (quote requests, calls). No contact details or messages.",
                    "`Leads` l",
                    new[]
                    {
                        C("lead_id", "l.`Id`", "int", "Id."),
                        C("created_at_utc", "l.`CreatedAt`", "datetime", "Created, UTC."),
                        C("stage", "l.`Stage`", "text", "Pipeline stage."),
                        C("source", "l.`Source`", "text", "Where it came from."),
                        C("type", "l.`Type`", "text", "Lead type."),
                        C("cleaning_type", "l.`CleaningType`", "text", "Requested cleaning."),
                        C("estimated_value", "l.`EstimatedValue`", "decimal", "Estimated value."),
                        C("assigned_to_admin_id", "l.`AssignedToAdminId`", "int", "Owner (agent_staff.user_id)."),
                        C("converted_order_id", "l.`ConvertedOrderId`", "int", "Order it became."),
                        C("is_archived", "l.`IsArchived`", "bool", "Archived.")
                    }),

                new("agent_business_clients",
                    "Commercial (business) clients.",
                    "`ContractClients` cc",
                    new[]
                    {
                        C("business_client_id", "cc.`Id`", "int", "Id."),
                        C("legal_entity_name", "cc.`LegalEntityName`", "text", "Company name."),
                        C("city", "cc.`City`", "text", "City."),
                        C("state", "cc.`State`", "text", "State."),
                        C("zip_code", "cc.`Zip`", "text", "ZIP."),
                        C("is_active", "cc.`IsActive`", "bool", "Active."),
                        C("created_at_utc", "cc.`CreatedAt`", "datetime", "Created, UTC.")
                    }),

                new("agent_commercial_invoices",
                    "Commercial invoices. Statistics counts their money by PAYMENT date, not by the cleanings' dates.",
                    "`CommercialInvoices` ci",
                    new[]
                    {
                        C("invoice_id", "ci.`Id`", "int", "Id."),
                        C("invoice_number", "ci.`InvoiceNumber`", "text", "DCI-... number."),
                        C("business_client_id", "ci.`ContractClientId`", "int", "Client."),
                        C("status", EnumName<InvoiceStatus>("ci.`Status`"), "text", $"Status: {EnumNames<InvoiceStatus>()}."),
                        C("invoice_date", "ci.`InvoiceDate`", "datetime", "Invoice date."),
                        C("due_date", "ci.`DueDate`", "datetime", "Due date."),
                        C("subtotal", "ci.`SubTotal`", "decimal", "Subtotal."),
                        C("discount_amount", "ci.`DiscountAmount`", "decimal", "Discount."),
                        C("tax_amount", "ci.`TaxAmount`", "decimal", "Tax."),
                        C("total", "ci.`Total`", "decimal", "Total."),
                        C("amount_paid", "ci.`AmountPaid`", "decimal", "Paid so far."),
                        C("balance_due", "ci.`BalanceDue`", "decimal", "Still owed."),
                        C("first_sent_at_utc", "ci.`FirstSentAt`", "datetime", "First sent."),
                        C("paid_at_utc", "ci.`PaidAt`", "datetime", "Paid in full."),
                        C("voided_at_utc", "ci.`VoidedAt`", "datetime", "Voided.")
                    }),

                new("agent_google_reviews",
                    "Google reviews (rating and date only).",
                    "`GoogleReviews` g",
                    new[]
                    {
                        C("rating", "g.`Rating`", "int", "Stars 1-5."),
                        C("created_at_utc", "g.`CreateTime`", "datetime", "Posted, UTC."),
                        C("is_hidden", "g.`IsHidden`", "bool", "Hidden on the website.")
                    })
            };
        }
    }
}

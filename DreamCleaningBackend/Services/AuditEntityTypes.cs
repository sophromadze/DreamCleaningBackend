namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// The audit vocabulary: every EntityType string the audit trail uses, and the single
    /// definition of which of them can never be replayed by Undo/Redo.
    ///
    /// WHY THIS FILE EXISTS. Two problems it solves, both found in the 2026-08-31 sweep:
    ///
    /// 1. <b>The block list was duplicated.</b> <c>AuditService</c> held a private HashSet and
    ///    <c>audit-history.component.ts</c> held a literal array, with nothing keeping them in
    ///    step - so the Audits tab happily rendered an enabled Undo button on rows the server was
    ///    always going to refuse. The list now lives here once and is SHIPPED to the frontend by
    ///    <c>GET api/admin/audit-logs/metadata</c>; the component has no copy of its own.
    /// 2. <b>Pseudo-entity names were invented at each call site.</b> An action that does not map
    ///    to a single persisted row ("this cleaner was paid", "this refund was issued") still
    ///    needs an EntityType, and a typo produced a stream nobody could filter on. Every such
    ///    name is a constant here.
    ///
    /// THE RULE FOR A NEW PSEUDO-ENTITY: name it here, and add it to
    /// <see cref="UndoBlockedReasons"/> in the same edit. A pseudo-entity is by definition not a
    /// row the generic reflection undo can write back, so leaving it out means an admin gets an
    /// Undo button that either fails loudly or - far worse - resolves to a same-named Models class
    /// and edits the wrong record.
    /// </summary>
    public static class AuditEntityTypes
    {
        // --- Pre-existing pseudo-entities (names are load-bearing: rows already in the DB carry
        //     them, and the Audits tab has dedicated renderers keyed on three of them) ----------
        public const string OrderServicesUpdate = "OrderServicesUpdate";
        public const string CleanerAssignment = "CleanerAssignment";
        public const string BubblePointsAdjustment = "BubblePointsAdjustment";
        public const string OrderNotification = "OrderNotification";
        public const string UserLoyaltyDiscount = "UserLoyaltyDiscount";

        // --- Outgoing Payments. The screen where money leaves the business; every one of these
        //     records a payout decision, so none of them is undoable. ---------------------------
        /// <summary>Per-cleaner rate/hours override set or reset on one order. EntityId = order id.</summary>
        public const string CleanerPayrollOverride = "CleanerPayrollOverride";
        /// <summary>The ORDER's cleaner hourly rate. EntityId = order id.</summary>
        public const string OrderCleanerHourlyRate = "OrderCleanerHourlyRate";
        /// <summary>A payout marked paid or un-paid (named cleaner or unassigned slot). EntityId = order id.</summary>
        public const string CleanerPayout = "CleanerPayout";

        // --- Orders ------------------------------------------------------------------------
        /// <summary>Refund issued or synced. EntityId = order id.</summary>
        public const string OrderRefundAction = "OrderRefundAction";
        /// <summary>Internal admin note on an order. EntityId = order id.</summary>
        public const string OrderAdminNote = "OrderAdminNote";
        /// <summary>Order hidden from / restored to the admin list. EntityId = order id.</summary>
        public const string OrderVisibility = "OrderVisibility";
        /// <summary>Order moved to another customer, or that move undone. EntityId = order id.</summary>
        public const string OrderTransferAction = "OrderTransferAction";
        /// <summary>Change request submitted / approved / rejected. EntityId = order id.</summary>
        public const string OrderEditRequest = "OrderEditRequest";
        /// <summary>Manual payment recorded, or a saved card charged. EntityId = order id.</summary>
        public const string OrderPaymentAction = "OrderPaymentAction";
        /// <summary>Assigned admin set or cleared on an order. EntityId = order id.</summary>
        public const string OrderAssignedAdmin = "OrderAssignedAdmin";

        // --- Users, rewards, referrals -----------------------------------------------------
        /// <summary>Free-text admin note on a customer. EntityId = user id.</summary>
        public const string UserAdminNote = "UserAdminNote";
        /// <summary>Email/SMS opt-in flipped by an admin. EntityId = user id.</summary>
        public const string UserCommunicationPreference = "UserCommunicationPreference";
        /// <summary>Store credit granted, review bonus granted, points reset. EntityId = user id.</summary>
        public const string RewardAdjustment = "RewardAdjustment";
        /// <summary>Referral link added or removed by an admin. EntityId = user id.</summary>
        public const string ReferralAdjustment = "ReferralAdjustment";
        /// <summary>A rewards/loyalty configuration key. EntityId = 0 (settings are global).</summary>
        public const string RewardSetting = "RewardSetting";

        /// <summary>
        /// The pre-reset snapshot AdminRewardsController writes before clearing bubble points.
        /// Named here because it predates this file and its rows are already in the database — it
        /// is not written through LogActionAsync, but it still has to be undo-blocked: the reset
        /// has its OWN restore endpoint (<c>POST reset/undo</c>) which replays the snapshot across
        /// many users, and the generic single-row undo would simply delete the snapshot and strand
        /// that restore.
        /// </summary>
        public const string BubblePointsResetSnapshot = "BubblePointsResetSnapshot";

        // --- Cleaners ----------------------------------------------------------------------
        /// <summary>Photo or document uploaded against a cleaner profile. EntityId = cleaner id.</summary>
        public const string CleanerDocument = "CleanerDocument";
        /// <summary>Per-order cleaner performance rating. EntityId = cleaner id.</summary>
        public const string CleanerPerformance = "CleanerPerformance";

        // --- Catalogue / pricing -----------------------------------------------------------
        /// <summary>Bulk pricing import applied from the Services tab. EntityId = 0.</summary>
        public const string PricingConfiguration = "PricingConfiguration";
        /// <summary>Service or extra copied onto another service type. EntityId = the new row id.</summary>
        public const string CatalogueCopy = "CatalogueCopy";

        // --- Marketing / comms -------------------------------------------------------------
        /// <summary>Scheduled mail or SMS campaign sent, enabled or disabled. EntityId = campaign id.</summary>
        public const string CampaignAction = "CampaignAction";
        /// <summary>Special offer granted to one user or to everybody. EntityId = offer id.</summary>
        public const string SpecialOfferGrant = "SpecialOfferGrant";

        // --- Site-wide toggles and integrations ---------------------------------------------
        /// <summary>Maintenance mode, live chat, chat-agent visibility, etc. EntityId = 0.</summary>
        public const string SiteSetting = "SiteSetting";
        /// <summary>An admin-triggered external data sync/backfill (Ads, GA4, Search Console, calls).
        /// EntityId = 0. Recorded because a backfill rewrites reported figures.</summary>
        public const string DataSync = "DataSync";

        /// <summary>
        /// Entity types Undo/Redo must refuse, and the reason each is refused, in ONE place.
        ///
        /// The reason text is not decoration: it is shipped to the Audits tab and rendered in the
        /// disabled button's tooltip, because a bare dash in the Undo column told an admin nothing
        /// and left them wondering whether the page was broken.
        ///
        /// Membership rules - a type belongs here when reverting the DATABASE row would not revert
        /// what actually happened:
        ///  (a) it is an audit/event log (undoing it would lie about history);
        ///  (b) it has an external side effect already emitted (money moved, mail sent);
        ///  (c) it is a pseudo-entity with no single DbSet row behind it.
        ///
        /// NOTE: <see cref="UserLoyaltyDiscount"/> is deliberately ABSENT. It is a pseudo-entity,
        /// but AuditService.UndoAsync has an explicit hand-written path for it that writes the
        /// four loyalty columns back onto the User row.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> UndoBlockedReasons =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // (a) audit / event logs
                ["AuditLog"] = "Audit rows are the record itself - reverting one would falsify history.",
                [BubblePointsAdjustment] = "Points adjustments are a running ledger; reverse one by making the opposite adjustment.",
                [CleanerAssignment] = "Assignment notices have already been sent. Change the assignment on the order instead.",
                [OrderServicesUpdate] = "Order lines are re-priced on save; re-enter the correct lines on the order instead.",
                [OrderNotification] = "The message has already left. Nothing can un-send it.",

                // (b) money and other external side effects
                ["PaymentHistory"] = "Payment records mirror Stripe; changing one here would not move any money.",
                ["WebhookEvent"] = "Webhook records make retries idempotent; removing one would let a payment be processed twice.",
                ["OrderRefund"] = "The refund has already reached the customer. Deleting the record would only hide it.",
                [OrderRefundAction] = "The refund has already reached the customer. Deleting the record would only hide it.",
                [OrderPaymentAction] = "The charge has already been taken. Refund it on the order rather than reverting the record.",
                [CleanerPayrollOverride] = "Payroll figures feed reported labour cost. Set the rate or hours back by hand so the change is recorded.",
                [OrderCleanerHourlyRate] = "Payroll figures feed reported labour cost. Set the rate back by hand so the change is recorded.",
                [CleanerPayout] = "This records money handed to a cleaner. Use Undo payment on the Outgoing Payments page.",

                // notification + scheduling logs
                ["NotificationLog"] = "Notification records track what was sent; reverting one would not recall it.",
                ["ScheduledMail"] = "Scheduled mail may already have gone out. Edit the campaign instead.",
                ["ScheduledSms"] = "Scheduled SMS may already have gone out. Edit the campaign instead.",
                [CampaignAction] = "The campaign has already run. Edit or disable it instead.",

                ["OrderUpdateHistory"] = "Update history tracks money owed; reverting a row would break payment accounting.",
                ["OrderTransfer"] = "Transfers move points, spend and photos between accounts. Use Undo transfer on the order.",
                [OrderTransferAction] = "Transfers move points, spend and photos between accounts. Use Undo transfer on the order.",

                // (c) pseudo-entities with no single row behind them
                [OrderAdminNote] = "Notes are free text on the order - edit the note instead.",
                [OrderVisibility] = "Show or hide the order again from the orders panel.",
                [OrderEditRequest] = "Change requests are a decision record. Submit a new request instead.",
                [OrderAssignedAdmin] = "Reassign the order from the orders panel instead.",
                [UserAdminNote] = "Notes are free text on the customer - edit the note instead.",
                [UserCommunicationPreference] = "Set the preference back from the customer's record.",
                [RewardAdjustment] = "Credits and points are a running ledger; make the opposite adjustment instead.",
                [ReferralAdjustment] = "Re-link or unlink the referral from the Rewards tab instead.",
                [RewardSetting] = "Settings are global; set the value back from the Rewards tab.",
                [BubblePointsResetSnapshot] = "Use \"Undo last reset\" on the Rewards tab — it replays this snapshot across every affected customer.",
                [CleanerDocument] = "Files are stored on disk; re-upload or delete the file itself.",
                [CleanerPerformance] = "Re-rate the cleaner on the order instead.",
                [PricingConfiguration] = "A pricing import rewrites many rows at once. Re-import the previous configuration instead.",
                [CatalogueCopy] = "Delete the copied row from the catalogue instead.",
                [SpecialOfferGrant] = "Offers may already have been used. Withdraw the offer from the Special Offers tab.",
                [SiteSetting] = "Toggle the setting back from its own page.",
                [DataSync] = "A sync rewrites imported figures; run the sync again for the corrected range.",
            };

        /// <summary>The block-list keys alone - what AuditService checks membership against.</summary>
        public static readonly IReadOnlySet<string> UndoBlockedEntityTypes =
            UndoBlockedReasons.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Why Undo/Redo is unavailable for this row, or null when it is available. This is the
        /// server-side authority for the button's enabled state - the Audits tab renders exactly
        /// what this returns rather than deciding for itself.
        /// </summary>
        public static string? ResolveUndoBlockedReason(Models.AuditLog log)
        {
            if (log == null) return "This row could not be read.";

            if (UndoBlockedReasons.TryGetValue(log.EntityType ?? string.Empty, out var reason))
                return reason;

            // The Phase 1 refusal: replaying a partial before-image would write zeroes over live
            // data. It used to be server-side only, so the button looked enabled and failed on
            // click.
            if (AuditSnapshot.HasFabricatedBeforeImage(log))
                return AuditSnapshot.FabricatedBeforeImageMessage;

            var action = log.Action ?? string.Empty;
            var isStandard = action.Equals("Create", StringComparison.OrdinalIgnoreCase)
                          || action.Equals("Update", StringComparison.OrdinalIgnoreCase)
                          || action.Equals("Delete", StringComparison.OrdinalIgnoreCase);
            var isLoyalty = string.Equals(log.EntityType, UserLoyaltyDiscount, StringComparison.OrdinalIgnoreCase);

            if (!isStandard && !isLoyalty)
                return $"'{action}' is an event, not a field change, so there is nothing to write back.";

            return null;
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminAnalyticsAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminAgentSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    OwnerUserId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheWriteTokens = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedCostUsd = table.Column<decimal>(type: "decimal(18,6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAgentSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminAgentSessions_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AdminAgentSqlLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    MessageId = table.Column<long>(type: "bigint", nullable: true),
                    Sql = table.Column<string>(type: "LONGTEXT", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Succeeded = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Rejected = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Error = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    Truncated = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAgentSqlLogs", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AdminAgentMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Content = table.Column<string>(type: "LONGTEXT", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InputMode = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProgressLabel = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErrorMessage = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PartsJson = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ToolCallsJson = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ApiMessagesJson = table.Column<string>(type: "LONGTEXT", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Model = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    CacheReadTokens = table.Column<int>(type: "int", nullable: false),
                    CacheWriteTokens = table.Column<int>(type: "int", nullable: false),
                    CostUsd = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAgentMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminAgentMessages_AdminAgentSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "AdminAgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAgentMessages_Session_Id",
                table: "AdminAgentMessages",
                columns: new[] { "SessionId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAgentSessions_Owner_UpdatedAt",
                table: "AdminAgentSessions",
                columns: new[] { "OwnerUserId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAgentSqlLogs_CreatedAt",
                table: "AdminAgentSqlLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAgentSqlLogs_User_CreatedAt",
                table: "AdminAgentSqlLogs",
                columns: new[] { "UserId", "CreatedAt" });

            // Read-only views for the SuperAdmin analytics agent's run_sql tool, generated from
            // Services/AdminAgent/AgentViewCatalog.cs and pasted here verbatim so this migration
            // stays frozen (AgentViewCatalogTests fails if the catalog and the migrations disagree).
            // The AgentReadOnly database user is granted SELECT on these views and nothing else;
            // SQL SECURITY DEFINER is what lets it read them without any right on the tables.
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_orders` AS SELECT o.`Id` AS `order_id`, o.`UserId` AS `customer_id`, CAST(o.`ServiceDate` AS DATE) AS `service_date`, o.`ServiceTime` AS `service_time`, o.`CreatedAt` AS `booked_at_utc`, o.`Status` AS `status`, o.`StatusBeforeRefund` AS `status_before_refund`, (o.`IsPaid` = 1 OR (o.`PaymentMethod` <> 0 AND (o.`PaymentMethod` <> 5 OR o.`InvoicePaidAt` IS NOT NULL))) AS `is_settled`, o.`IsPaid` AS `is_paid_stripe`, CASE o.`PaymentMethod` WHEN 0 THEN 'Normal' WHEN 1 THEN 'Cash' WHEN 2 THEN 'Zelle' WHEN 3 THEN 'Check' WHEN 4 THEN 'Other' WHEN 5 THEN 'Invoice' WHEN 6 THEN 'BankTransfer' ELSE CONCAT('Unknown(', o.`PaymentMethod`, ')') END AS `payment_method`, o.`InvoicePaidAt` AS `invoice_paid_at`, o.`ServiceTypeId` AS `service_type_id`, st.`ServiceKey` AS `service_type_key`, st.`Name` AS `service_type_name`, st.`IsCustom` AS `is_custom_service_type`, o.`SubscriptionId` AS `subscription_id`, sub.`Name` AS `subscription_name`, sub.`SubscriptionDays` AS `subscription_days`, (o.`SubscriptionId` IS NOT NULL AND sub.`SubscriptionDays` > 0) AS `is_recurring_plan`, o.`IsGeneratedByRecurringSeries` AS `is_generated_by_recurring_series`, o.`RecurringSeriesId` AS `recurring_series_id`, o.`IsCommercial` AS `is_commercial`, o.`ContractClientId` AS `business_client_id`, o.`SubTotal` AS `subtotal`, o.`DiscountAmount` AS `promo_discount`, o.`SubscriptionDiscountAmount` AS `subscription_discount`, o.`LoyaltyDiscountAmount` AS `loyalty_discount`, o.`GiftCardAmountUsed` AS `gift_card_used`, o.`PointsRedeemedDiscount` AS `points_discount`, o.`RewardBalanceUsed` AS `reward_balance_used`, o.`Tax` AS `sales_tax`, o.`Tips` AS `tips`, o.`CompanyDevelopmentTips` AS `legacy_company_tips`, o.`Total` AS `total`, o.`TotalRefundedAmount` AS `total_refunded`, o.`AmountPaid` AS `amount_paid_in_parts`, o.`PromoCode` AS `promo_code`, o.`SpecialOfferName` AS `special_offer_name`, o.`CleanerHourlyRate` AS `cleaner_hourly_rate`, o.`CleanerTotalSalary` AS `cleaner_total_salary`, o.`TotalDuration` AS `total_duration_minutes`, o.`MaidsCount` AS `maids_count`, o.`BonusBookerId` AS `booking_admin_id`, o.`BookedByAdminUserId` AS `created_by_admin_id`, o.`AssignedAdminId` AS `assigned_admin_id`, o.`BonusManagerId` AS `bonus_manager_id`, o.`IsNewCustomerOrder` AS `is_new_customer_order`, o.`City` AS `city`, o.`State` AS `state`, o.`ZipCode` AS `zip_code`, o.`PropertyType` AS `property_type`, o.`BedroomsQuantity` AS `bedrooms`, o.`BathroomsQuantity` AS `bathrooms`, o.`LevelsQuantity` AS `levels`, o.`AcquisitionChannel` AS `acquisition_channel`, o.`AcquisitionSource` AS `acquisition_source`, o.`AcquisitionMedium` AS `acquisition_medium`, o.`AcquisitionCampaign` AS `acquisition_campaign`, o.`IsLateCancellation` AS `is_late_cancellation`, o.`IsHidden` AS `is_hidden_in_admin_list` FROM `Orders` o LEFT JOIN `ServiceTypes` st ON st.`Id` = o.`ServiceTypeId` LEFT JOIN `Subscriptions` sub ON sub.`Id` = o.`SubscriptionId`
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_order_services` AS SELECT os.`OrderId` AS `order_id`, os.`ServiceId` AS `service_id`, s.`ServiceKey` AS `service_key`, s.`Name` AS `service_name`, s.`ServiceRelationType` AS `relation_type`, os.`Quantity` AS `quantity`, os.`Cost` AS `cost`, os.`Duration` AS `duration_minutes` FROM `OrderServices` os JOIN `Services` s ON s.`Id` = os.`ServiceId`
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_order_extras` AS SELECT oe.`OrderId` AS `order_id`, oe.`ExtraServiceId` AS `extra_service_id`, es.`ExtraServiceKey` AS `extra_key`, es.`Name` AS `extra_name`, es.`IsDeepCleaning` AS `is_deep_cleaning`, es.`IsSuperDeepCleaning` AS `is_super_deep_cleaning`, es.`IsSameDayService` AS `is_same_day`, oe.`Quantity` AS `quantity`, oe.`Hours` AS `hours`, oe.`Cost` AS `cost`, oe.`Duration` AS `duration_minutes` FROM `OrderExtraServices` oe JOIN `ExtraServices` es ON es.`Id` = oe.`ExtraServiceId`
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_service_types` AS SELECT st.`Id` AS `service_type_id`, st.`ServiceKey` AS `service_type_key`, st.`Name` AS `name`, st.`IsActive` AS `is_active`, st.`IsCustom` AS `is_custom`, st.`BasePrice` AS `base_price` FROM `ServiceTypes` st
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_services` AS SELECT s.`Id` AS `service_id`, s.`ServiceTypeId` AS `service_type_id`, s.`ServiceKey` AS `service_key`, s.`Name` AS `name`, s.`ServiceRelationType` AS `relation_type`, s.`Cost` AS `cost`, s.`IsActive` AS `is_active` FROM `Services` s
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_extra_services` AS SELECT es.`Id` AS `extra_service_id`, es.`ServiceTypeId` AS `service_type_id`, es.`ExtraServiceKey` AS `extra_key`, es.`Name` AS `name`, es.`Price` AS `price`, es.`IsAvailableForAll` AS `is_available_for_all`, es.`IsActive` AS `is_active` FROM `ExtraServices` es
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_subscriptions` AS SELECT sub.`Id` AS `subscription_id`, sub.`Name` AS `name`, sub.`SubscriptionDays` AS `subscription_days`, (sub.`SubscriptionDays` > 0) AS `is_recurring`, sub.`DiscountPercentage` AS `discount_percentage`, sub.`IsActive` AS `is_active` FROM `Subscriptions` sub
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_customers` AS SELECT u.`Id` AS `customer_id`, u.`FirstName` AS `first_name`, u.`LastName` AS `last_name`, u.`CreatedAt` AS `created_at_utc`, u.`IsActive` AS `is_active`, u.`IsDeleted` AS `is_deleted`, u.`IsBusiness` AS `is_business`, u.`SubscriptionId` AS `current_subscription_id`, u.`SubscriptionExpiryDate` AS `subscription_expires_at`, u.`LoyaltyDiscountPercentage` AS `loyalty_discount_percentage`, u.`ReferredByUserId` AS `referred_by_customer_id` FROM `Users` u WHERE u.`Role` = 0
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_staff` AS SELECT u.`Id` AS `user_id`, u.`FirstName` AS `first_name`, u.`LastName` AS `last_name`, CASE u.`Role` WHEN 0 THEN 'Customer' WHEN 1 THEN 'SuperAdmin' WHEN 2 THEN 'Admin' WHEN 3 THEN 'Moderator' WHEN 4 THEN 'Cleaner' ELSE CONCAT('Unknown(', u.`Role`, ')') END AS `role`, CASE u.`AdminPosition` WHEN 0 THEN 'Administrator' WHEN 1 THEN 'Manager' ELSE CONCAT('Unknown(', u.`AdminPosition`, ')') END AS `position`, u.`ManagerId` AS `manager_id`, u.`IsActive` AS `is_active`, u.`IsDeleted` AS `is_deleted` FROM `Users` u WHERE u.`Role` IN (1, 2, 3)
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_cleaners` AS SELECT c.`Id` AS `cleaner_id`, c.`FirstName` AS `first_name`, c.`LastName` AS `last_name`, c.`IsActive` AS `is_active`, c.`Ranking` AS `ranking`, c.`Borough` AS `borough`, c.`CreatedAt` AS `created_at_utc` FROM `Cleaners` c
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_order_cleaners` AS SELECT oc.`OrderId` AS `order_id`, oc.`CleanerId` AS `cleaner_id`, oc.`AssignedAt` AS `assigned_at_utc`, oc.`IsPaid` AS `is_paid`, oc.`PaidAmount` AS `paid_amount`, oc.`PaidAt` AS `paid_at_utc`, CASE oc.`PaidVia` WHEN 1 THEN 'Zelle' WHEN 2 THEN 'Cash' WHEN 3 THEN 'Check' WHEN 4 THEN 'Other' ELSE CONCAT('Unknown(', oc.`PaidVia`, ')') END AS `paid_via`, oc.`SalaryBillableMinutes` AS `hours_override_minutes`, oc.`SalaryHourlyRate` AS `hourly_rate_override`, oc.`TipsWithheldFromPayout` AS `tips_withheld` FROM `OrderCleaners` oc
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_order_refunds` AS SELECT r.`Id` AS `refund_id`, r.`OrderId` AS `order_id`, r.`Amount` AS `amount`, r.`Status` AS `status`, CASE r.`Source` WHEN 0 THEN 'Crm' WHEN 1 THEN 'Stripe' ELSE CONCAT('Unknown(', r.`Source`, ')') END AS `source`, r.`CreatedAt` AS `created_at_utc` FROM `OrderRefunds` r
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_expenses` AS SELECT e.`Id` AS `expense_id`, e.`Name` AS `name`, ec.`Name` AS `category`, e.`Amount` AS `amount`, e.`Currency` AS `currency`, e.`StartDate` AS `start_date`, e.`EndDate` AS `end_date`, e.`IsRecurring` AS `is_recurring`, e.`FrequencyMonths` AS `frequency_months`, e.`StaffUserId` AS `staff_user_id` FROM `Expenses` e LEFT JOIN `ExpenseCategories` ec ON ec.`Id` = e.`Category`
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_fx_rates` AS SELECT f.`Year` AS `year`, f.`Month` AS `month`, f.`UsdPerGel` AS `usd_per_gel`, f.`IsFinalized` AS `is_finalized` FROM `MonthlyFinancialSnapshots` f
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_recurring_series` AS SELECT rs.`Id` AS `series_id`, rs.`UserId` AS `customer_id`, rs.`TemplateOrderId` AS `template_order_id`, rs.`IntervalValue` AS `interval_value`, CASE rs.`IntervalUnit` WHEN 0 THEN 'Days' WHEN 1 THEN 'Weeks' WHEN 2 THEN 'Months' ELSE CONCAT('Unknown(', rs.`IntervalUnit`, ')') END AS `interval_unit`, rs.`AnchorDate` AS `anchor_date`, rs.`IsActive` AS `is_active`, rs.`EndDate` AS `end_date`, rs.`StoppedAt` AS `stopped_at_utc`, rs.`CreatedAt` AS `created_at_utc` FROM `RecurringOrderSeries` rs
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_leads` AS SELECT l.`Id` AS `lead_id`, l.`CreatedAt` AS `created_at_utc`, l.`Stage` AS `stage`, l.`Source` AS `source`, l.`Type` AS `type`, l.`CleaningType` AS `cleaning_type`, l.`EstimatedValue` AS `estimated_value`, l.`AssignedToAdminId` AS `assigned_to_admin_id`, l.`ConvertedOrderId` AS `converted_order_id`, l.`IsArchived` AS `is_archived` FROM `Leads` l
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_business_clients` AS SELECT cc.`Id` AS `business_client_id`, cc.`LegalEntityName` AS `legal_entity_name`, cc.`City` AS `city`, cc.`State` AS `state`, cc.`Zip` AS `zip_code`, cc.`IsActive` AS `is_active`, cc.`CreatedAt` AS `created_at_utc` FROM `ContractClients` cc
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_commercial_invoices` AS SELECT ci.`Id` AS `invoice_id`, ci.`InvoiceNumber` AS `invoice_number`, ci.`ContractClientId` AS `business_client_id`, CASE ci.`Status` WHEN 0 THEN 'Draft' WHEN 1 THEN 'Sent' WHEN 2 THEN 'Viewed' WHEN 3 THEN 'PartiallyPaid' WHEN 4 THEN 'Paid' WHEN 5 THEN 'Overdue' WHEN 6 THEN 'Void' ELSE CONCAT('Unknown(', ci.`Status`, ')') END AS `status`, ci.`InvoiceDate` AS `invoice_date`, ci.`DueDate` AS `due_date`, ci.`SubTotal` AS `subtotal`, ci.`DiscountAmount` AS `discount_amount`, ci.`TaxAmount` AS `tax_amount`, ci.`Total` AS `total`, ci.`AmountPaid` AS `amount_paid`, ci.`BalanceDue` AS `balance_due`, ci.`FirstSentAt` AS `first_sent_at_utc`, ci.`PaidAt` AS `paid_at_utc`, ci.`VoidedAt` AS `voided_at_utc` FROM `CommercialInvoices` ci
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE SQL SECURITY DEFINER VIEW `agent_google_reviews` AS SELECT g.`Rating` AS `rating`, g.`CreateTime` AS `created_at_utc`, g.`IsHidden` AS `is_hidden` FROM `GoogleReviews` g
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The views do not depend on the agent tables below; they are dropped first only so
            // Down undoes Up in reverse order.
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_google_reviews`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_commercial_invoices`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_business_clients`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_leads`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_recurring_series`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_fx_rates`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_expenses`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_order_refunds`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_order_cleaners`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_cleaners`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_staff`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_customers`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_subscriptions`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_extra_services`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_services`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_service_types`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_order_extras`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_order_services`
                """);
            migrationBuilder.Sql("""
                DROP VIEW IF EXISTS `agent_orders`
                """);
            migrationBuilder.DropTable(
                name: "AdminAgentMessages");

            migrationBuilder.DropTable(
                name: "AdminAgentSqlLogs");

            migrationBuilder.DropTable(
                name: "AdminAgentSessions");
        }
    }
}

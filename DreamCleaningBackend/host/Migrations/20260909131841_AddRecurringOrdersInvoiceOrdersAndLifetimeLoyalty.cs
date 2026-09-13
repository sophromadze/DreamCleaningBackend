using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurringOrdersInvoiceOrdersAndLifetimeLoyalty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LoyaltyDiscountIsLifetime",
                table: "Users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ContractClientId",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InvoicePaidAt",
                table: "Orders",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsGeneratedByRecurringSeries",
                table: "Orders",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "PreInvoiceAllocationTotal",
                table: "Orders",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecurrenceOccurrenceDate",
                table: "Orders",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecurringSeriesId",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AutoAssignedFromSeriesId",
                table: "OrderCleaners",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftWarningsJson",
                table: "CommercialInvoices",
                type: "LONGTEXT",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "NegotiatedOrderGroupTotal",
                table: "CommercialInvoices",
                type: "decimal(10,2)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CommercialInvoiceOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    CommercialInvoiceId = table.Column<int>(type: "int", nullable: false),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    AllocatedAmount = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    OriginalOrderTotal = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    CommittedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CommittedByUserId = table.Column<int>(type: "int", nullable: true),
                    ActivatedOrderAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LineDescription = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommercialInvoiceOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommercialInvoiceOrders_CommercialInvoices_CommercialInvoice~",
                        column: x => x.CommercialInvoiceId,
                        principalTable: "CommercialInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CommercialInvoiceOrders_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommercialInvoiceOrders_Users_CommittedByUserId",
                        column: x => x.CommittedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecurringOrderSeries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    TemplateOrderId = table.Column<int>(type: "int", nullable: false),
                    IntervalValue = table.Column<int>(type: "int", nullable: false),
                    IntervalUnit = table.Column<int>(type: "int", nullable: false),
                    AnchorDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ServiceTime = table.Column<TimeSpan>(type: "time(6)", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    EndDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CopyCleanerAssignments = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    AutoRequestPayment = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    GeneratedThroughDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Notes = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringOrderSeries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringOrderSeries_Orders_TemplateOrderId",
                        column: x => x.TemplateOrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringOrderSeries_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringOrderSeries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "OrderPaymentBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    RecurringSeriesId = table.Column<int>(type: "int", nullable: true),
                    PaymentIntentId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FailureReason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SettlementWarning = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderPaymentBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderPaymentBatches_RecurringOrderSeries_RecurringSeriesId",
                        column: x => x.RecurringSeriesId,
                        principalTable: "RecurringOrderSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OrderPaymentBatches_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "OrderPaymentBatchItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    OrderPaymentBatchId = table.Column<int>(type: "int", nullable: false),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AppliedToOrder = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderPaymentBatchItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderPaymentBatchItems_OrderPaymentBatches_OrderPaymentBatch~",
                        column: x => x.OrderPaymentBatchId,
                        principalTable: "OrderPaymentBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrderPaymentBatchItems_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ContractClientId",
                table: "Orders",
                column: "ContractClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Series_Occurrence",
                table: "Orders",
                columns: new[] { "RecurringSeriesId", "RecurrenceOccurrenceDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommercialInvoiceOrders_CommittedByUserId",
                table: "CommercialInvoiceOrders",
                column: "CommittedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CommercialInvoiceOrders_Invoice_Order",
                table: "CommercialInvoiceOrders",
                columns: new[] { "CommercialInvoiceId", "OrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommercialInvoiceOrders_OrderId",
                table: "CommercialInvoiceOrders",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderPaymentBatches_PaymentIntent",
                table: "OrderPaymentBatches",
                column: "PaymentIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderPaymentBatches_RecurringSeriesId",
                table: "OrderPaymentBatches",
                column: "RecurringSeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderPaymentBatches_User_Status",
                table: "OrderPaymentBatches",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderPaymentBatchItems_Batch_Order",
                table: "OrderPaymentBatchItems",
                columns: new[] { "OrderPaymentBatchId", "OrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderPaymentBatchItems_OrderId",
                table: "OrderPaymentBatchItems",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringOrderSeries_Active_Anchor",
                table: "RecurringOrderSeries",
                columns: new[] { "IsActive", "AnchorDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringOrderSeries_CreatedByUserId",
                table: "RecurringOrderSeries",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringOrderSeries_TemplateOrderId",
                table: "RecurringOrderSeries",
                column: "TemplateOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringOrderSeries_UserId",
                table: "RecurringOrderSeries",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_ContractClients_ContractClientId",
                table: "Orders",
                column: "ContractClientId",
                principalTable: "ContractClients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_RecurringOrderSeries_RecurringSeriesId",
                table: "Orders",
                column: "RecurringSeriesId",
                principalTable: "RecurringOrderSeries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_ContractClients_ContractClientId",
                table: "Orders");

            migrationBuilder.DropForeignKey(
                name: "FK_Orders_RecurringOrderSeries_RecurringSeriesId",
                table: "Orders");

            migrationBuilder.DropTable(
                name: "CommercialInvoiceOrders");

            migrationBuilder.DropTable(
                name: "OrderPaymentBatchItems");

            migrationBuilder.DropTable(
                name: "OrderPaymentBatches");

            migrationBuilder.DropTable(
                name: "RecurringOrderSeries");

            migrationBuilder.DropIndex(
                name: "IX_Orders_ContractClientId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_Series_Occurrence",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "LoyaltyDiscountIsLifetime",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ContractClientId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "InvoicePaidAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "IsGeneratedByRecurringSeries",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PreInvoiceAllocationTotal",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RecurrenceOccurrenceDate",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RecurringSeriesId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AutoAssignedFromSeriesId",
                table: "OrderCleaners");

            migrationBuilder.DropColumn(
                name: "DraftWarningsJson",
                table: "CommercialInvoices");

            migrationBuilder.DropColumn(
                name: "NegotiatedOrderGroupTotal",
                table: "CommercialInvoices");
        }
    }
}

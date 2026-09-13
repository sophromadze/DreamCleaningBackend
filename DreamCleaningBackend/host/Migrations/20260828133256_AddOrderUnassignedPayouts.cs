using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderUnassignedPayouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderUnassignedPayouts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    SlotIndex = table.Column<int>(type: "int", nullable: false),
                    IsPaid = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    PaidVia = table.Column<int>(type: "int", nullable: true),
                    PaidAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    PaidByUserId = table.Column<int>(type: "int", nullable: true),
                    PaymentNote = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderUnassignedPayouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderUnassignedPayouts_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrderUnassignedPayouts_Users_PaidByUserId",
                        column: x => x.PaidByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_OrderUnassignedPayouts_OrderId_SlotIndex",
                table: "OrderUnassignedPayouts",
                columns: new[] { "OrderId", "SlotIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderUnassignedPayouts_PaidByUserId",
                table: "OrderUnassignedPayouts",
                column: "PaidByUserId");

            BackfillHistoricSlots(migrationBuilder);
        }

        /// <summary>
        /// The same reasoning as BackfillHistoricCleanerPayouts: everything already finished when
        /// this shipped had been paid the old way, including the people who are not in the system.
        /// Without this, every historic under-staffed order would open showing money still owed.
        ///
        /// A slot exists for each staffing position past the assigned cleaners — indexes
        /// <c>assignedCount .. MaidsCount-1</c> — which is what the join over the small integer
        /// list generates. Ten covers every real cleaner count by a wide margin (production tops
        /// out at 3); a job somehow staffed for more than ten would leave its extra slots unpaid
        /// and visible, which is the safe direction to fail in.
        ///
        /// <c>PaidAmount</c>/<c>PaidAt</c>/<c>PaidVia</c>/<c>PaidByUserId</c> stay NULL — we do not
        /// know what was handed over, when, how or by whom, and inventing it would put fabricated
        /// history in the audit trail. The note carries the reason instead.
        /// </summary>
        private static void BackfillHistoricSlots(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
                INSERT INTO `OrderUnassignedPayouts` (`OrderId`, `SlotIndex`, `IsPaid`, `PaymentNote`)
                SELECT o.`Id`, n.`i`, 1, '{BackfillNote}'
                FROM `Orders` o
                CROSS JOIN (
                    SELECT 0 AS i UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3
                    UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7
                    UNION ALL SELECT 8 UNION ALL SELECT 9
                ) n
                LEFT JOIN (
                    SELECT `OrderId`, COUNT(*) AS c FROM `OrderCleaners` GROUP BY `OrderId`
                ) a ON a.`OrderId` = o.`Id`
                WHERE (o.`Status` = 'Done'
                       OR (o.`Status` = 'Refunded' AND o.`StatusBeforeRefund` = 'Done'))
                  AND n.`i` >= COALESCE(a.`c`, 0)
                  AND n.`i` < o.`MaidsCount`;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the table takes the backfilled rows with it.
            migrationBuilder.DropTable(
                name: "OrderUnassignedPayouts");
        }

        private const string BackfillNote = "Paid before payout tracking existed";
    }
}

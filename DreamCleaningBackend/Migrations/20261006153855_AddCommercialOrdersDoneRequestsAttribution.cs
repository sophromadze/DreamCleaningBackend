using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddCommercialOrdersDoneRequestsAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "PendingOrderEdits",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Edit")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "AcquisitionClickId",
                table: "Orders",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "AcquisitionClickIdType",
                table: "Orders",
                type: "varchar(10)",
                maxLength: 10,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "AcquisitionLandingPage",
                table: "Orders",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "AcquisitionReferrer",
                table: "Orders",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "IsCommercial",
                table: "Orders",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // The composite index is created BEFORE the single-column one is dropped: that one backs
            // the OrderId foreign key, and MariaDB refuses to drop the only index a foreign key uses.
            migrationBuilder.CreateIndex(
                name: "IX_PendingOrderEdits_OrderId_Status_Kind",
                table: "PendingOrderEdits",
                columns: new[] { "OrderId", "Status", "Kind" });

            migrationBuilder.DropIndex(
                name: "IX_PendingOrderEdits_OrderId",
                table: "PendingOrderEdits");

            // Backfill (owner's rule, 2026-10): every existing order of a business customer is
            // Commercial, and so is any order billed to a commercial client or performed under a
            // commercial contract. Everything else stays Regular (the column default). Same rule
            // as OrderCommercialPolicy.ResolveForNewOrder, run once over history.
            migrationBuilder.Sql(@"
UPDATE Orders o
LEFT JOIN Users u ON u.Id = o.UserId
SET o.IsCommercial = 1
WHERE u.IsBusiness = 1
   OR o.ContractClientId IS NOT NULL
   OR o.ContractId IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mirror of Up: restore the FK's own index before dropping the composite one.
            migrationBuilder.CreateIndex(
                name: "IX_PendingOrderEdits_OrderId",
                table: "PendingOrderEdits",
                column: "OrderId");

            migrationBuilder.DropIndex(
                name: "IX_PendingOrderEdits_OrderId_Status_Kind",
                table: "PendingOrderEdits");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "PendingOrderEdits");

            migrationBuilder.DropColumn(
                name: "AcquisitionClickId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AcquisitionClickIdType",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AcquisitionLandingPage",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AcquisitionReferrer",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "IsCommercial",
                table: "Orders");
        }
    }
}

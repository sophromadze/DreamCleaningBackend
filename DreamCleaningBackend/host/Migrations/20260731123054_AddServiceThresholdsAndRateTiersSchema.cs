using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceThresholdsAndRateTiersSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MinimumPrice",
                table: "ServiceTypes",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "ChargeAboveThreshold",
                table: "Services",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ZeroQuantityCost",
                table: "Services",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ZeroQuantityDuration",
                table: "Services",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServiceRateTiers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ServiceId = table.Column<int>(type: "int", nullable: false),
                    FromQuantity = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    TimeDuration = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceRateTiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceRateTiers_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ServiceThresholds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ServiceId = table.Column<int>(type: "int", nullable: false),
                    SourceServiceId = table.Column<int>(type: "int", nullable: false),
                    SourceQuantity = table.Column<int>(type: "int", nullable: false),
                    IncludedQuantity = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceThresholds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceThresholds_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServiceThresholds_Services_SourceServiceId",
                        column: x => x.SourceServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            // NOTE: EF generated seven UpdateData blocks here (ServiceTypes 1-2, Services 1-5),
            // one per HasData-seeded row, writing the new columns' CLR defaults. Every value was
            // identical to the defaultValue the AddColumn above already applied, or NULL on a
            // nullable column — provable no-ops whose only real effect was hardcoding local row
            // Ids into a migration that runs against databases with different Ids. Removed
            // deliberately. They do not reappear: the model snapshot is a separate file and is
            // unchanged, so the next `migrations add` still sees no diff here.

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRateTiers_Service_FromQuantity",
                table: "ServiceRateTiers",
                columns: new[] { "ServiceId", "FromQuantity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceThresholds_Service_Source_Quantity",
                table: "ServiceThresholds",
                columns: new[] { "ServiceId", "SourceServiceId", "SourceQuantity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceThresholds_SourceServiceId",
                table: "ServiceThresholds",
                column: "SourceServiceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServiceRateTiers");

            migrationBuilder.DropTable(
                name: "ServiceThresholds");

            migrationBuilder.DropColumn(
                name: "MinimumPrice",
                table: "ServiceTypes");

            migrationBuilder.DropColumn(
                name: "ChargeAboveThreshold",
                table: "Services");

            migrationBuilder.DropColumn(
                name: "ZeroQuantityCost",
                table: "Services");

            migrationBuilder.DropColumn(
                name: "ZeroQuantityDuration",
                table: "Services");
        }
    }
}

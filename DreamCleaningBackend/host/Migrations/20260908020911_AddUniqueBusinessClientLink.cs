using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <summary>
    /// ONE BUSINESS ACCOUNT = ONE COMMERCIAL CLIENT, enforced by the database.
    ///
    /// UNIQUE is the right MySQL/MariaDB spelling of "at most one non-null": the engine never
    /// considers two NULLs equal, so any number of standalone clients (no website account) still
    /// coexist while a second row claiming an account that already has one is refused.
    ///
    /// THE DE-DUPLICATION STEP IS NOT OPTIONAL. Until now nothing stopped two clients pointing at
    /// the same account — the contract form could create a second one — so on a live database the
    /// CREATE INDEX would fail outright and take the deployment with it. The statement below makes
    /// the constraint satisfiable first, and it is deliberately NON-DESTRUCTIVE: the oldest row
    /// keeps the link (it is the one the portal has been resolving), and each later duplicate is
    /// simply demoted to a standalone client. It keeps its id, its name, its contracts, its
    /// invoices and its reference numbers; all it loses is the account link, which a person can
    /// re-establish. Deleting a row here would take real commercial history with it.
    ///
    /// THE FOREIGN KEY HAS TO COME OFF FIRST, and this is not optional either.
    /// <c>FK_ContractClients_Users_SourceUserId</c> is backed by this very index — it is the only
    /// one on the column — and MySQL refuses to drop the last index a foreign key depends on:
    ///
    ///     Cannot drop index 'IX_ContractClients_SourceUserId': needed in a foreign key constraint
    ///
    /// So the constraint is dropped, the index is swapped, and the constraint is put back
    /// IDENTICALLY (Restrict on delete, as before). The FK is absent only for the two statements in
    /// between. That is a schema migration on a table nothing writes to during deployment, and the
    /// alternative — creating the unique index under a second name — would leave the column
    /// carrying two indexes and the model snapshot describing only one.
    /// </summary>
    public partial class AddUniqueBusinessClientLink : Migration
    {
        private const string ForeignKeyName = "FK_ContractClients_Users_SourceUserId";
        private const string IndexName = "IX_ContractClients_SourceUserId";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Multi-table UPDATE against a derived table: MySQL forbids reading the target table in
            // a plain subquery, and this is the supported way round it.
            //
            // Safe to re-run. A first attempt at this migration can have committed this statement
            // and then failed on the DDL below (MySQL DDL is not transactional, so nothing rolls
            // back and no history row is written) - on the retry there are simply no duplicates
            // left to demote and it updates nothing.
            migrationBuilder.Sql(@"
                UPDATE `ContractClients` c
                JOIN (
                    SELECT `SourceUserId`, MIN(`Id`) AS `KeepId`
                    FROM `ContractClients`
                    WHERE `SourceUserId` IS NOT NULL
                    GROUP BY `SourceUserId`
                    HAVING COUNT(*) > 1
                ) dup ON c.`SourceUserId` = dup.`SourceUserId`
                SET c.`SourceUserId` = NULL, c.`UpdatedAt` = UTC_TIMESTAMP()
                WHERE c.`Id` <> dup.`KeepId`;");

            migrationBuilder.DropForeignKey(
                name: ForeignKeyName,
                table: "ContractClients");

            migrationBuilder.DropIndex(
                name: IndexName,
                table: "ContractClients");

            migrationBuilder.CreateIndex(
                name: IndexName,
                table: "ContractClients",
                column: "SourceUserId",
                unique: true);

            // Restrict, unchanged: deleting a customer account must never take a commercial
            // client - and with it that company's contracts and invoices - along with it.
            migrationBuilder.AddForeignKey(
                name: ForeignKeyName,
                table: "ContractClients",
                column: "SourceUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Same dance in reverse. The de-duplication is deliberately NOT undone: those clients
            // are standalone records now, and re-linking them would need to invent which account
            // each belonged to.
            migrationBuilder.DropForeignKey(
                name: ForeignKeyName,
                table: "ContractClients");

            migrationBuilder.DropIndex(
                name: IndexName,
                table: "ContractClients");

            migrationBuilder.CreateIndex(
                name: IndexName,
                table: "ContractClients",
                column: "SourceUserId");

            migrationBuilder.AddForeignKey(
                name: ForeignKeyName,
                table: "ContractClients",
                column: "SourceUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}

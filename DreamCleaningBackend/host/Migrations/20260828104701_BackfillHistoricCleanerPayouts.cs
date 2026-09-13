using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <summary>
    /// Everything that was already finished when the Outgoing Payments page shipped had, by
    /// definition, already been paid the old way (the manager's WhatsApp run). Left alone, every
    /// one of those cleaners would have shown up as OWED money on day one, and the page would
    /// have opened on a five-figure "still to pay" figure that was pure fiction.
    ///
    /// So this marks every cleaner assignment on an already-performed order as paid.
    ///
    /// Two deliberate choices:
    ///
    ///  • <c>PaidAmount</c>, <c>PaidAt</c>, <c>PaidVia</c> and <c>PaidByUserId</c> stay NULL. We
    ///    do not know what was handed over, when, how, or by whom, and inventing those values
    ///    would put fabricated history into the audit trail. The UI already falls back to the
    ///    computed payout for display when PaidAmount is null.
    ///  • <c>PaymentNote</c> carries the reason instead, so anybody who opens one of these rows
    ///    and finds no date or method can see why in one line rather than reading it as data loss.
    ///
    /// "Already performed" is the same predicate the page itself filters on
    /// (OrderStatuses.WasPerformed): Done, plus an order that was Done and later fully refunded.
    /// Anything still Pending/Active is untouched and will appear as unpaid when it finishes,
    /// which is the point.
    ///
    /// This is a one-off data fix, not schema. It is written as raw SQL because it is a set-based
    /// UPDATE over a join — loading every historic assignment into memory to flip a boolean would
    /// be slower and no clearer. Rows that somehow already carry IsPaid = 1 are excluded so
    /// re-running cannot overwrite a real payout record with the backfill note.
    /// </summary>
    public partial class BackfillHistoricCleanerPayouts : Migration
    {
        private const string BackfillNote = "Paid before payout tracking existed";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
                UPDATE `OrderCleaners` oc
                INNER JOIN `Orders` o ON o.`Id` = oc.`OrderId`
                SET oc.`IsPaid` = 1,
                    oc.`PaymentNote` = '{BackfillNote}'
                WHERE oc.`IsPaid` = 0
                  AND (o.`Status` = 'Done'
                       OR (o.`Status` = 'Refunded' AND o.`StatusBeforeRefund` = 'Done'));
            ");
        }

        /// <summary>
        /// Reverses ONLY the rows this migration wrote, identified by the note it stamped. A blanket
        /// "un-pay every historic assignment" would also erase real payouts recorded through the
        /// page between this migration running and being rolled back.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
                UPDATE `OrderCleaners`
                SET `IsPaid` = 0,
                    `PaymentNote` = NULL
                WHERE `IsPaid` = 1
                  AND `PaymentNote` = '{BackfillNote}';
            ");
        }
    }
}

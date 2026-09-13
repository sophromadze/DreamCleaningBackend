using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs.Commercial;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Helpers.Commercial;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Models.Contracts;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Commercial
{
    /// <summary>
    /// AN INVOICE COVERS ONE OR MANY CLEANINGS, and this is where that link is decided.
    ///
    /// ══ DRAFT IS A PROPOSAL ══
    ///
    /// While the invoice is a Draft, selecting orders and typing an agreed group total writes
    /// <see cref="CommercialInvoiceOrder"/> rows and rebuilds the invoice's LINE ITEMS — and
    /// touches the orders themselves not at all. An admin who types $3,500, thinks better of it and
    /// abandons the draft has re-priced nothing. <see cref="CommitAllocationsAsync"/> is what writes
    /// the agreed price onto each order, and it runs when the invoice is finalized (sent).
    ///
    /// ══ EQUAL SHARES, EXACT TO THE CENT ══
    ///
    /// The arithmetic is <see cref="InvoiceOrderAllocator"/>: integer cents, equal shares, the
    /// remainder to the earliest service dates. $3,500 across four visits is four times $875.00;
    /// $100 across three is $33.34 / $33.33 / $33.33. The shares always sum to the invoice total
    /// exactly, which is the only acceptable outcome when the same figure is printed on a document
    /// and stored on four bookings.
    ///
    /// ══ ONE LINE PER VISIT ══
    ///
    /// The invoice shows "Commercial Cleaning — Oct 4 … $875.00" four times rather than one lump,
    /// because a client checking a bill wants to see the visits they received. The lines are
    /// REBUILT from the selection on every save, so the two can never disagree.
    ///
    /// ══ NOTHING IS BILLED TWICE ══
    ///
    /// An order already on another live (non-void) invoice is reported by number and refused —
    /// <b>at the endpoint, not just greyed out in the picker</b>. Void invoices keep their links
    /// for the audit trail and stop being a claim, which is what frees an order to be re-billed
    /// after a mistake.
    ///
    /// ══ THE WALL ══
    ///
    /// This service reads and writes <c>Orders</c> — that is its job — but it never reaches into
    /// the residential PRICING chain. Turning an allocated dollar amount into an order's subtotal
    /// and tax needs the residential tax split, so it goes through
    /// <see cref="IOrderInvoiceAllocationService"/>, across which only primitives travel. See
    /// <c>CommercialResidentialSeparationTests</c>.
    /// </summary>
    public class InvoiceOrderLinkService
    {
        private readonly ApplicationDbContext _context;
        private readonly InvoiceService _invoices;
        private readonly IOrderInvoiceAllocationService _orderAllocation;
        private readonly ILogger<InvoiceOrderLinkService> _logger;

        public InvoiceOrderLinkService(
            ApplicationDbContext context,
            InvoiceService invoices,
            IOrderInvoiceAllocationService orderAllocation,
            ILogger<InvoiceOrderLinkService> logger)
        {
            _context = context;
            _invoices = invoices;
            _orderAllocation = orderAllocation;
            _logger = logger;
        }

        // ── The picker ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The cleanings an invoice for this client could cover.
        ///
        /// Offered: orders billed to this commercial client, <b>plus the unclaimed orders of the
        /// customer account the client is linked to</b>, that are not yet settled and not
        /// cancelled. <b>Paid orders are not offered as candidates</b> — there is nothing left to
        /// invoice — and an order already claimed by another live invoice is listed but not
        /// selectable, naming the invoice that has it.
        ///
        /// ══ WHY THE LINKED ACCOUNT IS INCLUDED (2026-09) ══
        ///
        /// <c>Order.ContractClientId</c> is only stamped when an order is CREATED with (or later
        /// switched to) the Invoice payment method. A business customer's ordinary bookings —
        /// taken by phone, or booked by the customer themselves — carry none, so the picker showed
        /// "this client has no cleanings to bill" for a client with a diary full of them, and the
        /// only way out was to go and change each order's payment method by hand first. That is
        /// backwards: choosing to bill a cleaning on an invoice is what should make it
        /// invoice-billed, and it now does — <see cref="CommitAllocationsAsync"/> adopts the order
        /// when the invoice is sent.
        ///
        /// The widening is deliberately narrow. Only orders with NO client of their own join in
        /// (<c>ContractClientId == null</c>): a cleaning already billed to a different company
        /// must never appear on this one's invoice, however the accounts are linked.
        /// </summary>
        public async Task<InvoiceEligibleOrdersDto> GetEligibleOrdersAsync(
            int contractClientId, int? forInvoiceId, DateTime? from, DateTime? to)
        {
            var linkedUserId = await ResolveLinkedAccountIdAsync(contractClientId);

            var query = _context.Orders
                .Include(o => o.ServiceType)
                .Where(o => o.ContractClientId == contractClientId
                            || (linkedUserId != null
                                && o.UserId == linkedUserId
                                && o.ContractClientId == null))
                .AsQueryable();

            if (from.HasValue) query = query.Where(o => o.ServiceDate >= from.Value.Date);
            if (to.HasValue) query = query.Where(o => o.ServiceDate <= to.Value.Date);

            var orders = await query
                .OrderBy(o => o.ServiceDate)
                .ThenBy(o => o.Id)
                .AsNoTracking()
                .ToListAsync();

            var orderIds = orders.Select(o => o.Id).ToList();

            // Every LIVE claim on these orders. Void invoices are deliberately excluded: their
            // links survive for the trail, but a cancelled bill is not a claim on the money.
            var claims = await _context.CommercialInvoiceOrders
                .Include(l => l.Invoice)
                .Where(l => orderIds.Contains(l.OrderId)
                            && l.Invoice != null
                            && l.Invoice.Status != InvoiceStatus.Void)
                .AsNoTracking()
                .ToListAsync();

            var result = new InvoiceEligibleOrdersDto { ContractClientId = contractClientId };

            foreach (var order in orders)
            {
                var mine = forInvoiceId.HasValue
                    ? claims.FirstOrDefault(c => c.OrderId == order.Id && c.CommercialInvoiceId == forInvoiceId.Value)
                    : null;

                var other = claims.FirstOrDefault(
                    c => c.OrderId == order.Id && c.CommercialInvoiceId != (forInvoiceId ?? 0));

                var row = new InvoiceEligibleOrderDto
                {
                    OrderId = order.Id,
                    ServiceDate = order.ServiceDate,
                    ServiceTime = order.ServiceTime,
                    ServiceTypeName = order.GetDisplayServiceTypeName(),
                    ServiceAddress = order.ServiceAddress
                                     + (string.IsNullOrEmpty(order.AptSuite) ? "" : $", {order.AptSuite}"),
                    Status = order.Status,
                    Total = order.Total,
                    ContactName = $"{order.ContactFirstName} {order.ContactLastName}".Trim(),
                    IsOnThisInvoice = mine != null,
                    AllocatedAmount = mine?.AllocatedAmount,
                    BilledOnInvoiceId = other?.CommercialInvoiceId,
                    BilledOnInvoiceNumber = other?.Invoice?.InvoiceNumber
                };

                var blocked = ResolveBlockedReason(order, other);
                row.BlockedReason = blocked;
                row.CanSelect = blocked == null || row.IsOnThisInvoice;

                result.Orders.Add(row);
            }

            return result;
        }

        /// <summary>
        /// The customer account a commercial client is linked to, or null for a standalone client.
        ///
        /// One question, one answer, used by the picker and by the save guard — if the two
        /// resolved it separately, a cleaning the picker offered could be refused on save, which
        /// is the most confusing shape a validation error can take.
        /// </summary>
        private Task<int?> ResolveLinkedAccountIdAsync(int contractClientId) =>
            ResolveLinkedAccountIdAsync(_context, contractClientId);

        private static Task<int?> ResolveLinkedAccountIdAsync(
            ApplicationDbContext context, int contractClientId) =>
            context.ContractClients
                .Where(c => c.Id == contractClientId)
                .Select(c => c.SourceUserId)
                .FirstOrDefaultAsync();

        private static string? ResolveBlockedReason(Order order, CommercialInvoiceOrder? otherClaim)
        {
            if (otherClaim?.Invoice != null)
                return $"Already included in {otherClaim.Invoice.InvoiceNumber}.";

            if (OrderStatuses.IsCancelled(order.Status))
                return "This cleaning was cancelled.";

            if (OrderStatuses.IsRefunded(order.Status))
                return "This cleaning was refunded.";

            // Paid outside the invoice — by card, in cash, or by an earlier invoice.
            //
            // ONE DEFINITION, the reporting one: OrderPaymentFilter.IsSettledInMemory. It was
            // previously `IsPaid || InvoicePaidAt != null`, which is narrower than what
            // ApplySelectionAsync refuses, so a cash job the picker happily offered was rejected on
            // save — a validation error about a row the screen had just presented as selectable.
            // Both halves now ask the same question.
            if (OrderPaymentFilter.IsSettledInMemory(order))
                return "This cleaning has already been paid for.";

            if (order.ManualPaymentRecordedAt != null)
                return "A payment was already recorded against this cleaning.";

            return null;
        }

        // ── Saving the selection (draft only) ─────────────────────────────────────────────────

        /// <summary>
        /// Replaces the invoice's covered-orders selection and rebuilds its line items.
        ///
        /// Refused once the invoice has been finalized: the allocation is committed onto the
        /// orders at that point, and quietly re-cutting it would leave the client's copy and the
        /// bookings saying different things. Correcting an issued invoice is what voiding and
        /// re-issuing is for.
        /// </summary>
        public async Task<InvoiceOrdersResultDto> SaveSelectionAsync(
            int invoiceId, SaveInvoiceOrdersDto dto, int userId)
        {
            var invoice = await _context.CommercialInvoices
                .Include(i => i.Items)
                .Include(i => i.CoveredOrders)
                .FirstOrDefaultAsync(i => i.Id == invoiceId)
                ?? throw new InvoiceWorkflowException("Invoice not found.");

            if (invoice.Status != InvoiceStatus.Draft)
                throw new InvoiceWorkflowException(
                    "The cleanings an invoice covers can only be changed while it is a draft. "
                    + "Void this invoice and raise a corrected one instead.");

            var result = await ApplySelectionAsync(_context, invoice, dto);
            await _context.SaveChangesAsync();

            await _invoices.LogActivityAsync(invoice.Id, "invoice_orders_selected",
                dto.NegotiatedGroupTotal.HasValue
                    ? $"{result.Allocations.Count} cleaning(s) selected, with an agreed total of "
                      + $"{dto.NegotiatedGroupTotal.Value:C} — the allocated prices are not applied "
                      + "to the cleanings until the invoice is sent."
                    : $"{result.Allocations.Count} cleaning(s) selected, totalling {result.DefaultTotal:C}.",
                userId);

            return result;
        }
        // Shared by Draft save, the selection endpoint, and the read-only preview.
        internal static async Task<InvoiceOrdersResultDto> ApplySelectionAsync(
            ApplicationDbContext _context, CommercialInvoice invoice, SaveInvoiceOrdersDto dto, bool updateLinks = true)
        {
            if (invoice.Status != InvoiceStatus.Draft)
                throw new InvoiceWorkflowException("Linked cleaning amounts and dates can only be derived for a Draft.");
            if (invoice.ContractId.HasValue && !await _context.Contracts.AnyAsync(c => c.Id == invoice.ContractId
                && c.ContractClientId == invoice.ContractClientId))
                throw new InvoiceWorkflowException("The selected contract does not belong to this client.");
            var requested = dto.OrderIds.Distinct().ToList();
            var result = new InvoiceOrdersResultDto
            {
                InvoiceId = invoice.Id,
                InvoiceNumber = invoice.InvoiceNumber,
                NegotiatedGroupTotal = dto.NegotiatedGroupTotal
            };

            var orders = requested.Count == 0
                ? new List<Order>()
                : await _context.Orders
                    .Include(o => o.ServiceType)
                    .Where(o => requested.Contains(o.Id))
                    .OrderBy(o => o.ServiceDate)
                    .ThenBy(o => o.Id)
                    .ToListAsync();

            var missing = requested.Except(orders.Select(o => o.Id)).ToList();
            if (missing.Count > 0)
                throw new InvoiceWorkflowException(
                    $"Order{(missing.Count == 1 ? "" : "s")} {string.Join(", ", missing)} could not be found.");

            // Every order must belong to the client being invoiced — either because it is already
            // billed to them, or because it is an unclaimed cleaning of the customer account this
            // client is linked to. Exactly the set GetEligibleOrdersAsync offers, resolved through
            // the same helper so the picker and this guard cannot disagree.
            //
            // Checked server-side, so a hand-rolled request cannot put another company's cleaning
            // on this bill: an order already carrying a DIFFERENT ContractClientId is refused
            // whatever account it belongs to.
            var linkedUserId = await ResolveLinkedAccountIdAsync(_context, invoice.ContractClientId);

            var foreign = orders
                .Where(o => o.ContractClientId != invoice.ContractClientId
                            && !(o.ContractClientId == null
                                 && linkedUserId != null
                                 && o.UserId == linkedUserId))
                .ToList();

            if (foreign.Count > 0)
                throw new InvoiceWorkflowException(
                    "These cleanings are not billed to this client: "
                    + string.Join(", ", foreign.Select(o => $"#{o.Id}")) + ".");

            // THE DOUBLE-BILLING GUARD, enforced here and not only in the picker.
            var claimedElsewhere = await _context.CommercialInvoiceOrders
                .Include(l => l.Invoice)
                .Where(l => requested.Contains(l.OrderId)
                            && l.CommercialInvoiceId != invoice.Id
                            && l.Invoice != null
                            && l.Invoice.Status != InvoiceStatus.Void)
                .ToListAsync();

            if (claimedElsewhere.Count > 0)
            {
                var names = claimedElsewhere
                    .Select(c => $"#{c.OrderId} is already included in {c.Invoice!.InvoiceNumber}")
                    .Distinct();
                throw new InvoiceWorkflowException(string.Join("; ", names) + ".");
            }

            // Same definition the picker blocks on, so nothing it offered can be refused here.
            var alreadyPaid = orders
                .Where(o => OrderPaymentFilter.IsSettledInMemory(o) || o.ManualPaymentRecordedAt != null)
                .ToList();

            if (alreadyPaid.Count > 0)
                throw new InvoiceWorkflowException(
                    "These cleanings have already been paid for and cannot be invoiced again: "
                    + string.Join(", ", alreadyPaid.Select(o => $"#{o.Id}")) + ".");

            if (orders.Any(o => OrderStatuses.IsCancelled(o.Status) || OrderStatuses.IsRefunded(o.Status)))
                throw new InvoiceWorkflowException("Cancelled or refunded cleanings cannot be selected.");
            // Current order totals already include their discounts. A stale cloned invoice
            // discount must not reduce that selected-order sum for a second time.
            if (orders.Count > 0 && !dto.NegotiatedGroupTotal.HasValue)
            {
                invoice.DiscountType = InvoiceDiscountType.None;
                invoice.DiscountValue = null;
            }

            // ── Allocation ────────────────────────────────────────────────────────────────────
            var byDate = orders.ToDictionary(o => o.Id);
            var candidates = orders
                .Select(o => new InvoiceAllocationCandidate
                {
                    OrderId = o.Id,
                    ServiceDate = o.ServiceDate,
                    CurrentTotal = o.Total
                })
                .ToList();

            result.DefaultTotal = InvoiceOrderAllocator.SumCurrentTotals(candidates);

            List<InvoiceAllocationLine> lines;

            if (dto.NegotiatedGroupTotal.HasValue && candidates.Count > 0)
            {
                var solve = InvoiceGroupTotalSolver.Solve(
                    dto.NegotiatedGroupTotal.Value, invoice.TaxType, invoice.TaxRate,
                    invoice.DiscountType, invoice.DiscountValue);

                if (!solve.IsExact) throw new InvoiceWorkflowException(solve.Error!);

                // The ORDERS split the agreed TOTAL (what the client pays per visit); the invoice
                // LINES sum to whatever produces that total under its tax mode. For the ordinary
                // tax-inclusive commercial invoice those are the same figure.
                lines = InvoiceOrderAllocator.DistributeEqually(candidates, dto.NegotiatedGroupTotal.Value);

                if (!InvoiceOrderAllocator.SumsExactlyTo(lines, dto.NegotiatedGroupTotal.Value))
                    throw new InvoiceWorkflowException(
                        "The allocation did not add up to the agreed total. Nothing has been saved.");

                RebuildLineItems(invoice, orders,
                    InvoiceOrderAllocator.DistributeEqually(candidates, solve.LineSubTotal));
            }
            else
            {
                lines = InvoiceOrderAllocator.KeepCurrentTotals(candidates);
                var solve = InvoiceGroupTotalSolver.Solve(result.DefaultTotal, invoice.TaxType, invoice.TaxRate,
                    invoice.DiscountType, invoice.DiscountValue);
                if (!solve.IsExact) throw new InvoiceWorkflowException(solve.Error!);
                // In added-tax mode the gross order amounts still define the amount due. Convert
                // the line inputs to their share of the solved pre-tax sum, preserving unequal prices.
                decimal cumulativeGross = 0, previousNet = 0;
                var invoiceLines = lines.Select(line => {
                    cumulativeGross += line.AllocatedAmount;
                    var cumulativeNet = result.DefaultTotal == 0 ? 0 : InvoiceCalculator.Round2(
                        solve.LineSubTotal * cumulativeGross / result.DefaultTotal);
                    var amount = cumulativeNet - previousNet;
                    previousNet = cumulativeNet;
                    return new InvoiceAllocationLine { OrderId = line.OrderId, ServiceDate = line.ServiceDate, AllocatedAmount = amount };
                }).ToList();
                RebuildLineItems(invoice, orders, invoiceLines);
            }

            // ── The link rows ─────────────────────────────────────────────────────────────────
            if (updateLinks)
            {
                var existing = invoice.CoveredOrders.ToList();
                foreach (var stale in existing.Where(l => !requested.Contains(l.OrderId)))
                {
                    _context.CommercialInvoiceOrders.Remove(stale);
                    invoice.CoveredOrders.Remove(stale);
                }

                var sort = 0;

                foreach (var line in lines)
                {
                    var order = byDate[line.OrderId];
                    var row = existing.FirstOrDefault(l => l.OrderId == line.OrderId);

                    if (row == null)
                    {
                        row = new CommercialInvoiceOrder
                        {
                            CommercialInvoiceId = invoice.Id,
                            OrderId = line.OrderId,
                            // Captured when the row is created, never rewritten — it is the record of
                            // what the job cost before this invoice renegotiated it.
                            OriginalOrderTotal = order.Total,
                            CreatedAt = DateTime.UtcNow
                        };
                        invoice.CoveredOrders.Add(row);
                    }

                    row.AllocatedAmount = line.AllocatedAmount;
                    row.LineDescription = BuildLineDescription(order);
                    row.SortOrder = sort++;
                    row.UpdatedAt = DateTime.UtcNow;
                }

            }

            invoice.NegotiatedOrderGroupTotal = dto.NegotiatedGroupTotal;
            InvoiceService.RecomputeTotalsFromRows(invoice);
            invoice.UpdatedAt = DateTime.UtcNow;

            result.InvoiceTotal = invoice.Total;
            result.Allocations = lines.Select(l => new InvoiceOrderAllocationDto
            {
                OrderId = l.OrderId,
                ServiceDate = l.ServiceDate,
                Description = BuildLineDescription(byDate[l.OrderId]),
                OriginalOrderTotal = l.PreviousTotal,
                AllocatedAmount = l.AllocatedAmount,
                IsProposal = true,
                OrderStatus = byDate[l.OrderId].Status
            }).ToList();

            if (dto.NegotiatedGroupTotal.HasValue && lines.Any(l => l.ChangesOrderTotal))
            {
                result.Warnings.Add(
                    $"Sending this invoice will change {lines.Count(l => l.ChangesOrderTotal)} order "
                    + "total(s) to the allocated amounts. Nothing has changed yet.");
            }

            result.ServiceDates = orders.Select(o => o.ServiceDate.Date).Distinct().OrderBy(d => d).ToList();
            if (result.ServiceDates.Count > 0)
            {
                invoice.ServiceStartDate = result.ServiceDates.First();
                invoice.ServiceEndDate = result.ServiceDates.Last();
                invoice.ServiceDatesJson = System.Text.Json.JsonSerializer.Serialize(result.ServiceDates);
            }
            result.Items = invoice.Items.OrderBy(i => i.SortOrder).Select(i => new SaveInvoiceItemDto
            { Description = i.Description, Quantity = i.Quantity, UnitPrice = i.UnitPrice, SortOrder = i.SortOrder }).ToList();
            return result;
        }


        /// <summary>
        /// One line per visit, so the client can check the bill against their own diary.
        /// Rebuilt from the selection on every save — the lines and the covered orders can
        /// therefore never disagree.
        /// </summary>
        private static void RebuildLineItems(
            CommercialInvoice invoice, List<Order> orders, List<InvoiceAllocationLine> lines)
        {
            invoice.Items.Clear();

            var byId = orders.ToDictionary(o => o.Id);
            var sort = 0;

            foreach (var line in lines)
            {
                invoice.Items.Add(new CommercialInvoiceItem
                {
                    Description = BuildLineDescription(byId[line.OrderId]),
                    Quantity = 1m,
                    UnitPrice = line.AllocatedAmount,
                    Amount = InvoiceCalculator.LineAmount(1m, line.AllocatedAmount),
                    SortOrder = sort++,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        private static string BuildLineDescription(Order order)
        {
            var name = order.GetDisplayServiceTypeName();
            var text = $"{name} — {order.ServiceDate:MMM d, yyyy}";
            return text.Length > 500 ? text[..500] : text;
        }

        // ── Finalizing ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes the agreed price onto every covered order. Called when the invoice is SENT.
        ///
        /// One transaction: either every order carries the allocation the client is about to
        /// receive, or none of them does. A partial commit would leave the invoice and the
        /// bookings quoting different money, which is the failure this whole flow exists to avoid.
        ///
        /// Idempotent — a row already carrying <c>CommittedAt</c> is skipped, so resending an
        /// invoice does not re-apply (or re-audit) anything.
        /// </summary>
        public async Task CommitAllocationsAsync(int invoiceId, int userId)
        {
            var invoice = await _context.CommercialInvoices
                .Include(i => i.CoveredOrders)
                .FirstOrDefaultAsync(i => i.Id == invoiceId);

            if (invoice == null) return;

            var pending = invoice.CoveredOrders.Where(l => l.CommittedAt == null).ToList();
            if (pending.Count == 0) return;

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                foreach (var link in pending)
                {
                    var previous = await _orderAllocation.ApplyAllocatedTotalAsync(
                        link.OrderId, link.AllocatedAmount, invoice.ContractClientId,
                        invoice.Id, invoice.InvoiceNumber, userId);

                    // Refreshed here rather than trusting the draft snapshot: the order may have
                    // been edited between selection and sending, and the figure that has to be
                    // recoverable is the one that was actually replaced. The billing arrangement
                    // is captured for the same reason — sending adopts the cleaning onto this
                    // client's invoice, and voiding has to be able to hand it back.
                    link.OriginalOrderTotal = previous.PreviousTotal;
                    link.OriginalPaymentMethod = previous.PreviousPaymentMethod;
                    link.OriginalContractClientId = previous.PreviousContractClientId;
                    link.CommittedAt = DateTime.UtcNow;
                    link.CommittedByUserId = userId;
                    link.UpdatedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            await _invoices.LogActivityAsync(invoice.Id, "invoice_orders_committed",
                $"Agreed prices applied to {pending.Count} cleaning(s): "
                + string.Join(", ", pending.Select(p => $"#{p.OrderId} at {p.AllocatedAmount:C}")) + ".",
                userId);
        }

        // ── Payment ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Activates every pending order this invoice covers, because the invoice is fully paid.
        ///
        /// THE SINGLE ACTIVATION PATH. A Stripe ACH settlement and an admin recording a manual
        /// bank transfer both land here, so "Mark as Paid" and a webhook produce identical
        /// results — there is deliberately no second implementation for the manual side.
        ///
        /// Called only when the balance is actually zero; a Draft, Sent, Viewed, Overdue,
        /// part-paid or Void invoice activates nothing, and neither does an ACH debit that is
        /// still processing (no money has moved during Processing, so no payment row exists yet).
        ///
        /// Idempotent at the ORDER level — <c>Order.InvoicePaidAt</c> is the marker — so a retried
        /// webhook delivery finds the work done and changes nothing.
        /// </summary>
        public async Task<int> ActivateCoveredOrdersAsync(int invoiceId)
        {
            var invoice = await _context.CommercialInvoices
                .Include(i => i.CoveredOrders)
                .FirstOrDefaultAsync(i => i.Id == invoiceId);

            if (invoice == null) return 0;

            // The arithmetic is what decides, not the label: an invoice reads Paid because its
            // balance is zero (InvoiceStatusPolicy), and this asks the same question directly.
            if (invoice.Status == InvoiceStatus.Void || invoice.BalanceDue > 0m) return 0;

            var activated = 0;
            var paidAt = invoice.PaidAt ?? DateTime.UtcNow;

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                foreach (var link in invoice.CoveredOrders.Where(l => l.ActivatedOrderAt == null))
                {
                    var moved = await _orderAllocation.ActivateAfterInvoicePaidAsync(
                        link.OrderId, invoice.Id, invoice.InvoiceNumber, paidAt);

                    // Stamped whether or not the status moved: an order that was already Done is
                    // still settled by this invoice, and re-running must not try again.
                    link.ActivatedOrderAt = DateTime.UtcNow;
                    link.UpdatedAt = DateTime.UtcNow;

                    if (moved) activated++;
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                // Never rethrow into a webhook: a bookkeeping failure must not make Stripe retry a
                // settlement that already succeeded. Logged loudly instead.
                _logger.LogError(ex,
                    "Failed to activate orders covered by invoice {InvoiceId}.", invoiceId);
                return 0;
            }

            if (activated > 0)
            {
                await _invoices.LogActivityAsync(invoice.Id, "invoice_orders_activated",
                    $"{activated} cleaning(s) became active because this invoice was paid in full.",
                    null, "System");
            }

            return activated;
        }

        // ── Void ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Puts back the prices a voided invoice had renegotiated, and releases its orders to be
        /// billed again.
        ///
        /// The LINK ROWS ARE KEPT. A voided invoice is a permanent record that its number was
        /// issued and cancelled, and the trail has to be able to say what it covered. They simply
        /// stop counting as a claim — see <see cref="GetEligibleOrdersAsync"/>.
        ///
        /// An order the invoice already ACTIVATED is left alone: money arrived, the job was
        /// settled, and voiding the paperwork afterwards is not a reason to un-settle the booking.
        /// </summary>
        public async Task RevertAllocationsAsync(int invoiceId, int userId)
        {
            var invoice = await _context.CommercialInvoices
                .Include(i => i.CoveredOrders)
                .FirstOrDefaultAsync(i => i.Id == invoiceId);

            if (invoice == null) return;

            var reverted = 0;

            foreach (var link in invoice.CoveredOrders)
            {
                if (link.CommittedAt == null) continue;      // never applied — nothing to undo
                if (link.ActivatedOrderAt != null) continue; // already paid and settled

                await _orderAllocation.RevertAllocatedTotalAsync(
                    link.OrderId, link.OriginalOrderTotal, link.OriginalPaymentMethod,
                    link.OriginalContractClientId, invoice.Id, invoice.InvoiceNumber, userId);

                reverted++;
            }

            if (reverted > 0)
            {
                await _invoices.LogActivityAsync(invoice.Id, "invoice_orders_reverted",
                    $"{reverted} cleaning(s) returned to their previous prices because this invoice "
                    + "was voided.", userId);
            }
        }

        // ── Looking the other way: from an order (or a customer) to its invoices ──────────────

        /// <summary>
        /// "Can I bill this cleaning, and on what?" — everything the admin Orders panel needs to
        /// replace Send Payment Link with Send Invoice.
        ///
        /// Answers BOTH halves in one call: the invoices that already cover the order, and — when
        /// none does — the client a new draft would be raised for. The panel must never infer the
        /// second from the absence of the first, because "no invoice yet" and "no client to invoice"
        /// are different situations with different buttons.
        ///
        /// <b>Nothing here sends anything.</b> It is a read; the send is the existing
        /// <c>POST invoices/{id}/send</c>, so there is exactly one path that emails a client.
        /// </summary>
        public async Task<OrderInvoicesDto> GetOrderInvoicesAsync(int orderId)
        {
            var order = await _context.Orders
                .Include(o => o.ContractClient)
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null) return new OrderInvoicesDto { OrderId = orderId };

            var result = new OrderInvoicesDto
            {
                OrderId = orderId,
                ContractClientId = order.ContractClientId,
                ClientName = order.ContractClient?.LegalEntityName
            };

            // The same predicate the picker blocks on — a cleaning the Orders panel offers to
            // invoice must be one the invoice form would actually accept.
            result.BlockedReason = ResolveBlockedReason(order, null);
            result.CanBeInvoiced = result.BlockedReason == null;

            // No client of its own: fall back to the commercial client the CUSTOMER ACCOUNT is
            // linked to. This is the ordinary case — a business customer's normal booking carries
            // no ContractClientId until an invoice adopts it.
            if (result.ContractClientId == null)
            {
                var suggested = await _context.ContractClients
                    .Where(c => c.SourceUserId == order.UserId && c.IsActive)
                    .OrderBy(c => c.Id)
                    .Select(c => new { c.Id, c.LegalEntityName })
                    .FirstOrDefaultAsync();

                result.SuggestedContractClientId = suggested?.Id;
                result.SuggestedClientName = suggested?.LegalEntityName;
            }

            var invoiceIds = await _context.CommercialInvoiceOrders
                .Where(l => l.OrderId == orderId)
                .Select(l => l.CommercialInvoiceId)
                .Distinct()
                .ToListAsync();

            result.Invoices = await BuildLinkedSummariesAsync(invoiceIds, orderId);
            return result;
        }

        /// <summary>
        /// Every invoice that belongs to this CUSTOMER, from either direction:
        /// the invoices addressed to the commercial client their account is linked to, and any
        /// invoice covering one of their own cleanings.
        ///
        /// The second half is why an ordinary (non-business) customer can legitimately have
        /// invoices: a cleaning of theirs may have been billed on a company's invoice. Both sets
        /// are unioned rather than chosen between, so the tab shows a complete picture or is not
        /// offered at all.
        ///
        /// <b>Drafts are included.</b> Unlike the customer-facing My Invoices page — which hides
        /// anything unsent, because the question there is "what have I been billed?" — this is an
        /// admin looking at an account, and a draft sitting unsent for three weeks is exactly the
        /// thing they need to see.
        /// </summary>
        public async Task<List<LinkedInvoiceSummaryDto>> GetUserInvoicesAsync(int userId)
        {
            var clientIds = await _context.ContractClients
                .Where(c => c.SourceUserId == userId)
                .Select(c => c.Id)
                .ToListAsync();

            var byClient = clientIds.Count == 0
                ? new List<int>()
                : await _context.CommercialInvoices
                    .Where(i => clientIds.Contains(i.ContractClientId))
                    .Select(i => i.Id)
                    .ToListAsync();

            var byOrder = await _context.CommercialInvoiceOrders
                .Where(l => l.Order != null && l.Order.UserId == userId)
                .Select(l => l.CommercialInvoiceId)
                .ToListAsync();

            return await BuildLinkedSummariesAsync(byClient.Concat(byOrder).Distinct().ToList(), null);
        }

        /// <summary>
        /// The shared projection. <paramref name="forOrderId"/> adds that order's allocation to
        /// each row; null leaves it off, which is what the per-customer listing wants.
        /// </summary>
        private async Task<List<LinkedInvoiceSummaryDto>> BuildLinkedSummariesAsync(
            List<int> invoiceIds, int? forOrderId)
        {
            if (invoiceIds.Count == 0) return new List<LinkedInvoiceSummaryDto>();

            var invoices = await _context.CommercialInvoices
                .Include(i => i.Client)
                .Where(i => invoiceIds.Contains(i.Id))
                .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id)
                .AsNoTracking()
                .ToListAsync();

            var clientIds = invoices.Select(i => i.ContractClientId).Distinct().ToList();

            // The billing contact's address wins over the client's notice address — the same
            // precedence InvoiceService and the Clients screen use, so the panel names the address
            // the mail would actually go to rather than a second-best guess.
            var contacts = await _context.ContractContacts
                .Where(c => c.ContractClientId != null
                            && clientIds.Contains(c.ContractClientId!.Value)
                            && c.IsActive)
                .OrderBy(c => c.Role == ContractContactRole.ClientSigner ? 0 : 1).ThenBy(c => c.Id)
                .AsNoTracking()
                .ToListAsync();

            var links = await _context.CommercialInvoiceOrders
                .Where(l => invoiceIds.Contains(l.CommercialInvoiceId))
                .AsNoTracking()
                .ToListAsync();

            return invoices.Select(invoice =>
            {
                var mine = forOrderId == null
                    ? null
                    : links.FirstOrDefault(l => l.CommercialInvoiceId == invoice.Id
                                                && l.OrderId == forOrderId.Value);

                var contact = contacts.FirstOrDefault(c => c.ContractClientId == invoice.ContractClientId);

                return new LinkedInvoiceSummaryDto
                {
                    Id = invoice.Id,
                    InvoiceNumber = invoice.InvoiceNumber,
                    ContractClientId = invoice.ContractClientId,
                    ClientName = invoice.Client?.LegalEntityName ?? "",
                    InvoiceDate = invoice.InvoiceDate,
                    DueDate = invoice.DueDate,
                    Total = invoice.Total,
                    AmountPaid = invoice.AmountPaid,
                    BalanceDue = invoice.BalanceDue,
                    Status = invoice.Status,
                    StatusLabel = InvoiceStatusPolicy.Label(invoice.Status),
                    HasBeenSent = invoice.FirstSentAt != null,
                    LastSentAt = invoice.LastSentAt,
                    PaidAt = invoice.PaidAt,
                    CanSend = InvoiceStatusPolicy.CanSend(invoice.Status),
                    CanSendReminder = InvoiceStatusPolicy.CanSendReminder(invoice.Status),
                    BillingEmail = contact?.Email ?? invoice.Client?.NoticeEmail,
                    CoveredOrderCount = links.Count(l => l.CommercialInvoiceId == invoice.Id),
                    AllocatedAmount = mine?.AllocatedAmount,
                    AllocationIsProposal = mine == null ? null : mine.CommittedAt == null
                };
            }).ToList();
        }

        // ── Detail projection ─────────────────────────────────────────────────────────────────

        public async Task<List<InvoiceOrderAllocationDto>> GetAllocationsAsync(int invoiceId) =>
            await _context.CommercialInvoiceOrders
                .Where(l => l.CommercialInvoiceId == invoiceId)
                .OrderBy(l => l.SortOrder)
                .Select(l => new InvoiceOrderAllocationDto
                {
                    OrderId = l.OrderId,
                    ServiceDate = l.Order!.ServiceDate,
                    Description = l.LineDescription ?? "",
                    OriginalOrderTotal = l.OriginalOrderTotal,
                    AllocatedAmount = l.AllocatedAmount,
                    IsProposal = l.CommittedAt == null,
                    CommittedAt = l.CommittedAt,
                    ActivatedOrderAt = l.ActivatedOrderAt,
                    OrderStatus = l.Order!.Status
                })
                .AsNoTracking()
                .ToListAsync();
    }
}

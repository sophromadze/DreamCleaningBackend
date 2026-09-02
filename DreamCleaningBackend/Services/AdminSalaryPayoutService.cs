using System.Globalization;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Staff salaries on the Outgoing Payments page: what each admin is owed for a month, split
    /// into the two instalments they are actually paid in, and the record of paying them.
    ///
    /// <b>What is OWED is never stored.</b> It is derived from the Salaries expenses through the
    /// same <see cref="IExpenseService.GetOccurrencesInRangeAsync"/> that Statistics and Finances
    /// read, so the page cannot disagree with the reported cost — editing a salary moves both at
    /// once. Only the PAYMENTS are rows in a table.
    /// </summary>
    public class AdminSalaryPayoutService : IAdminSalaryPayoutService
    {
        private readonly ApplicationDbContext _context;
        private readonly IExpenseService _expenses;
        private readonly IAuditService _audit;
        private readonly IAdminBonusService _bonuses;
        private readonly IFinancialRateService _rates;

        public AdminSalaryPayoutService(
            ApplicationDbContext context,
            IExpenseService expenses,
            IAuditService audit,
            IAdminBonusService bonuses,
            IFinancialRateService rates)
        {
            _context = context;
            _expenses = expenses;
            _audit = audit;
            _bonuses = bonuses;
            _rates = rates;
        }

        public async Task<AdminSalaryPayoutListDto> GetAsync(int year, int month)
        {
            if (month < 1 || month > 12)
                throw new InvalidOperationException("Month must be between 1 and 12.");

            var monthStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEnd = monthStart.AddMonths(1);

            // Every salary occurrence landing in this month, already converted to USD and carrying
            // the currency it was entered in.
            var occurrences = (await _expenses.GetOccurrencesInRangeAsync(monthStart, monthEnd))
                .Where(o => SalaryExpenseRules.IsSalaryCategory(o.CategoryId))
                .ToList();

            var payments = await _context.AdminSalaryPayments
                .Include(p => p.PaidByUser)
                .Where(p => p.Year == year && p.Month == month)
                .ToListAsync();

            // A person appears if the month owes them anything OR if they were already paid for it.
            // The second half matters: a salary deleted after it was paid must not make the payment
            // disappear from the page, or the money looks like it never left.
            var byPayee = new Dictionary<string, List<ExpenseOccurrenceDto>>();
            foreach (var o in occurrences)
            {
                var key = SalaryExpenseRules.GroupingKey(o.StaffUserId, o.Name);
                if (!byPayee.TryGetValue(key, out var list))
                    byPayee[key] = list = new List<ExpenseOccurrenceDto>();
                list.Add(o);
            }
            foreach (var p in payments)
            {
                if (!byPayee.ContainsKey(p.PayeeKey))
                    byPayee[p.PayeeKey] = new List<ExpenseOccurrenceDto>();
            }

            // Payees that exist only because of a bonus carry their name here — there are no
            // salary rows to read it off.
            var bonusOnlyNames = new Dictionary<string, (int UserId, string Name)>();

            // What everyone earned in staff bonuses this month. Resolved BEFORE the role and
            // payment-details lookups below, because it can add people to the list and both of
            // those have to cover everybody who ends up on screen.
            var bonuses = await LoadBonusesGelAsync(
                monthStart, monthEnd,
                byPayee.Values.SelectMany(v => v).Select(o => o.StaffUserId)
                    .Concat(payments.Select(p => p.StaffUserId))
                    .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList());

            // Somebody can earn a bonus without having a salary row on the Expenses page at all.
            // They are still owed that money, so they join the list rather than being silently
            // absent — the whole point of the tab is that nothing owed goes unnoticed.
            foreach (var b in bonuses.Values.Where(b => b.Gel != 0m))
            {
                var key = SalaryExpenseRules.GroupingKey(b.UserId, b.Name);
                if (!byPayee.ContainsKey(key))
                {
                    byPayee[key] = new List<ExpenseOccurrenceDto>();
                    bonusOnlyNames[key] = (b.UserId, b.Name);
                }
            }

            var staffIds = byPayee.Values.SelectMany(v => v).Select(o => o.StaffUserId)
                .Concat(payments.Select(p => p.StaffUserId))
                .Concat(bonusOnlyNames.Values.Select(v => (int?)v.UserId))
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();

            var staffById = staffIds.Count == 0
                ? new Dictionary<int, (string Role, bool IsStaff)>()
                : (await _context.Users
                        .Where(u => staffIds.Contains(u.Id))
                        .Select(u => new { u.Id, u.Role, u.IsDeleted })
                        .ToListAsync())
                    .ToDictionary(
                        u => u.Id,
                        u => (u.Role.ToString(), IsStaffRole(u.Role) && !u.IsDeleted));

            // Where each of them is paid. Loaded for the keys on screen rather than the whole
            // table — this list is one month of employees, not the company's whole history.
            var keys = byPayee.Keys.ToList();
            var detailsByKey = await _context.AdminSalaryPayees
                .Where(p => keys.Contains(p.PayeeKey))
                .ToDictionaryAsync(p => p.PayeeKey, p => p.PaymentDetails);

            var fxSnapshot = await _rates.GetOrCreateAsync(year, month);

            var payees = new List<AdminSalaryPayoutDto>();
            foreach (var (key, occs) in byPayee)
            {
                var paidForPayee = payments.Where(p => p.PayeeKey == key).ToList();
                bonusOnlyNames.TryGetValue(key, out var bonusOnly);

                var staffUserId = occs.FirstOrDefault()?.StaffUserId
                    ?? paidForPayee.FirstOrDefault()?.StaffUserId
                    ?? (bonusOnly.UserId != 0 ? bonusOnly.UserId : (int?)null);

                var bonusGel = staffUserId.HasValue && bonuses.TryGetValue(staffUserId.Value, out var b)
                    ? b.Gel
                    : 0m;

                var dto = BuildPayee(
                    key, occs, paidForPayee, staffById, bonusGel, fxSnapshot.UsdPerGel,
                    staffUserId, bonusOnly.Name);
                dto.PaymentDetails = detailsByKey.TryGetValue(key, out var d) ? d : null;
                payees.Add(dto);
            }

            var ordered = payees
                // Anyone still owed money comes first — the page exists to answer "what has to go
                // out today" — then by size, so the biggest outstanding sum leads.
                .OrderByDescending(p => p.UnpaidAmount > 0)
                .ThenByDescending(p => p.MonthTotalUsd)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new AdminSalaryPayoutListDto
            {
                Year = year,
                Month = month,
                MonthLabel = new DateTime(year, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                Payees = ordered,
                TotalUsd = ordered.Sum(p => p.MonthTotalUsd),
                PaidUsd = ordered.Sum(p => p.Instalments.Where(i => i.IsPaid).Sum(i => i.AmountUsd)),
                UnpaidUsd = ordered.Sum(p => p.Instalments.Where(i => !i.IsPaid).Sum(i => i.AmountUsd)),
                UnpaidInstalmentCount = ordered.Sum(p => p.Instalments.Count(i => !i.IsPaid))
            };
        }

        public async Task<AdminSalaryPayoutListDto> MarkPaidAsync(
            int year, int month, string payeeKey, int half, MarkSalaryPaidDto dto, int paidByUserId)
        {
            if (!SalaryPaymentSchedule.IsValidHalf(half))
                throw new InvalidOperationException("A salary month has two payments; pick the first or the second.");

            var view = await GetAsync(year, month);
            var payee = view.Payees.FirstOrDefault(p => p.PayeeKey == payeeKey)
                ?? throw new InvalidOperationException("Nobody by that name is owed a salary in that month.");

            var instalment = payee.Instalments.First(i => i.Half == half);
            if (instalment.IsPaid)
                throw new InvalidOperationException("That payment is already recorded. Undo it first if it was a mistake.");

            var row = new AdminSalaryPayment
            {
                PayeeKey = payeeKey,
                StaffUserId = payee.StaffUserId,
                PayeeName = payee.Name,
                Year = year,
                Month = month,
                Half = half,
                // Frozen at pay time — the amount, its bonus share, the USD figure and the rate
                // between them. Editing the salary, earning another bonus or the lari moving
                // afterwards changes what is owed NEXT month, never what has already been handed
                // over. The USD figure in particular must not drift with the exchange rate: it is
                // a record of money that left, not an estimate.
                PaidAmount = instalment.Amount,
                PaidBonusAmount = instalment.BonusAmount,
                Currency = ExpenseCurrency.Normalize(instalment.Currency),
                PaidAmountUsd = instalment.AmountUsd,
                UsdPerGel = payee.UsdPerGel,
                PaidAt = DateTime.UtcNow,
                PaidByUserId = paidByUserId,
                PaymentNote = string.IsNullOrWhiteSpace(dto.PaymentNote) ? null : dto.PaymentNote.Trim()
            };

            _context.AdminSalaryPayments.Add(row);
            await _context.SaveChangesAsync();
            await _audit.LogCreateAsync(row);

            return await GetAsync(year, month);
        }

        public async Task<AdminSalaryPayoutListDto> UndoPaymentAsync(int year, int month, string payeeKey, int half)
        {
            var row = await _context.AdminSalaryPayments
                .FirstOrDefaultAsync(p => p.Year == year && p.Month == month
                                          && p.PayeeKey == payeeKey && p.Half == half)
                ?? throw new InvalidOperationException("There is no recorded payment to undo.");

            // Logged while the row still carries its values — the audit trail is the only place a
            // reversed payment survives, because the row itself is removed.
            await _audit.LogDeleteAsync(row);

            _context.AdminSalaryPayments.Remove(row);
            await _context.SaveChangesAsync();

            return await GetAsync(year, month);
        }

        public async Task<AdminSalaryPayoutListDto> UpdatePayeeDetailsAsync(
            int year, int month, string payeeKey, UpdateSalaryPayeeDetailsDto dto, int byUserId)
        {
            var view = await GetAsync(year, month);
            var payee = view.Payees.FirstOrDefault(p => p.PayeeKey == payeeKey)
                ?? throw new InvalidOperationException("Nobody by that name is owed a salary in that month.");

            // Blank CLEARS it. A destination that turns out to be wrong has to be removable, not
            // only replaceable — leaving a stale account number on file is how money goes astray.
            var details = string.IsNullOrWhiteSpace(dto.PaymentDetails) ? null : dto.PaymentDetails.Trim();

            var row = await _context.AdminSalaryPayees.FirstOrDefaultAsync(p => p.PayeeKey == payeeKey);
            if (row == null)
            {
                // Created lazily, the first time somebody actually fills one in.
                row = new AdminSalaryPayee
                {
                    PayeeKey = payeeKey,
                    StaffUserId = payee.StaffUserId,
                    PaymentDetails = details,
                    UpdatedAt = DateTime.UtcNow,
                    UpdatedByUserId = byUserId
                };
                _context.AdminSalaryPayees.Add(row);
                await _context.SaveChangesAsync();
                await _audit.LogCreateAsync(row);
            }
            else
            {
                var before = AuditSnapshot.Of(row);
                row.PaymentDetails = details;
                // Re-stamped in case the salary only gained its staff link after the destination
                // was first entered.
                row.StaffUserId = payee.StaffUserId ?? row.StaffUserId;
                row.UpdatedAt = DateTime.UtcNow;
                row.UpdatedByUserId = byUserId;
                await _context.SaveChangesAsync();
                await _audit.LogUpdateAsync(before, row);
            }

            return await GetAsync(year, month);
        }

        /// <summary>
        /// What each of these people earned in staff bonuses for the month, in GEL, keyed by user
        /// id. Taken from <see cref="IAdminBonusService"/> — the SAME source the shifts panel pays
        /// out from — so the two screens can never quote a different bonus for one person.
        /// </summary>
        /// <remarks>
        /// This is a PAYOUT figure only. The bonus is already reported as a company cost through
        /// the bonus path (AdminStatisticsController adds it to total expenses separately from the
        /// expense breakdown), so nothing here writes to Expenses and nothing double-counts —
        /// this tab is only answering "what do I hand this person".
        /// </remarks>
        private async Task<Dictionary<int, (int UserId, string Name, decimal Gel)>> LoadBonusesGelAsync(
            DateTime monthStart, DateTime monthEnd, List<int> knownStaffUserIds)
        {
            var result = new Dictionary<int, (int UserId, string Name, decimal Gel)>();

            // One aggregate for every current admin, rather than a query per person. Not narrowed
            // to the people already on screen: somebody can earn a bonus with no salary row at
            // all, and they still have to appear.
            var summaries = await _bonuses.GetBonusesAsync(
                monthStart, monthEnd, viewerUserId: 0, viewerIsSuperAdmin: true);

            foreach (var s in summaries)
                result[s.AdminId] = (s.AdminId, $"{s.FirstName} {s.LastName}".Trim(), s.BonusAmount);

            // That list covers ACTIVE Admin-role users only. Somebody demoted or blocked partway
            // through the month still earned on the orders they took, and dropping their bonus
            // would quietly underpay them — so anyone on screen who is still unaccounted for is
            // asked about directly. Rare, so the extra queries cost nothing in practice.
            foreach (var id in knownStaffUserIds.Where(id => !result.ContainsKey(id)))
            {
                try
                {
                    var summary = await _bonuses.GetSummaryForAdminAsync(id, monthStart, monthEnd);
                    if (summary.BonusAmount != 0m)
                        result[id] = (id, $"{summary.FirstName} {summary.LastName}".Trim(), summary.BonusAmount);
                }
                catch (InvalidOperationException)
                {
                    // The account is gone entirely. There is no bonus to attribute, and a salary
                    // still owed to them is unaffected.
                }
            }

            return result;
        }

        // ──────────────────────────────────────────────────────────────────────────────

        private static AdminSalaryPayoutDto BuildPayee(
            string payeeKey,
            List<ExpenseOccurrenceDto> occurrences,
            List<AdminSalaryPayment> payments,
            IReadOnlyDictionary<int, (string Role, bool IsStaff)> staffById,
            decimal bonusGel,
            decimal usdPerGelForMonth,
            int? resolvedStaffUserId,
            string? bonusOnlyName)
        {
            var reference = occurrences.FirstOrDefault();
            var staffUserId = resolvedStaffUserId
                ?? reference?.StaffUserId
                ?? payments.FirstOrDefault()?.StaffUserId;
            var name = reference?.Name
                ?? payments.FirstOrDefault()?.PayeeName
                ?? bonusOnlyName
                ?? string.Empty;

            // A month can legitimately hold two salary rows for one person (a base salary plus a
            // one-off), and they could be in different currencies. The USD total is always right;
            // the entered-currency total is only shown when there is a single currency to show.
            var currencies = occurrences.Select(o => ExpenseCurrency.Normalize(o.Currency)).Distinct().ToList();
            var currency = currencies.Count == 1
                ? currencies[0]
                // No salary rows to read a currency off. A previous payment says what was used
                // last time; failing that, a bonus is set in GEL and is the only thing owed.
                : (payments.FirstOrDefault()?.Currency
                   ?? (bonusGel != 0m && currencies.Count == 0 ? ExpenseCurrency.Gel : ExpenseCurrency.Usd));
            var mixedCurrencies = currencies.Count > 1;

            var salaryUsd = occurrences.Sum(o => o.Amount);
            // Mixed currencies have no single native figure, so USD stands in for both.
            var salaryTotal = mixedCurrencies ? salaryUsd : occurrences.Sum(o => o.AmountInCurrency);
            var usdPerGel = occurrences.Select(o => o.UsdPerGel).FirstOrDefault(r => r.HasValue)
                            ?? (usdPerGelForMonth > 0 ? usdPerGelForMonth : (decimal?)null);

            // Bonus rates are always set in GEL. Expressed in the SALARY's currency so the two can
            // be added at all: a GEL salary adds it straight, a USD salary converts it first.
            var bonusUsd = ExpenseCurrency.ToUsd(bonusGel, ExpenseCurrency.Gel, usdPerGelForMonth);
            var bonusInCurrency = ExpenseCurrency.IsGel(currency) && !mixedCurrencies ? bonusGel : bonusUsd;

            var monthTotal = salaryTotal + bonusInCurrency;
            var monthTotalUsd = salaryUsd + bonusUsd;

            var instalments = new List<AdminSalaryInstalmentDto>();
            foreach (var half in SalaryPaymentSchedule.Halves)
            {
                var paid = payments.FirstOrDefault(p => p.Half == half);
                var owed = SalaryPaymentSchedule.Instalment(salaryTotal, bonusInCurrency, half);
                var owedUsd = SalaryPaymentSchedule.Instalment(salaryUsd, bonusUsd, half);

                instalments.Add(new AdminSalaryInstalmentDto
                {
                    Half = half,
                    Label = SalaryPaymentSchedule.Label(half),
                    // A paid instalment shows what was PAID, not today's arithmetic. The two differ
                    // whenever the salary or the month's bonuses moved after the fact, and the
                    // frozen figure is the one that describes money that actually left.
                    Amount = paid?.PaidAmount ?? owed.Total,
                    Currency = paid?.Currency ?? currency,
                    AmountUsd = paid?.PaidAmountUsd ?? owedUsd.Total,
                    // A settled instalment keeps its own breakdown, or a second payment that came
                    // to more than half the salary reads as an error rather than a month with
                    // bonuses in it.
                    SalaryAmount = paid != null ? paid.PaidAmount - paid.PaidBonusAmount : owed.Salary,
                    BonusAmount = paid?.PaidBonusAmount ?? owed.Bonus,
                    IsPaid = paid != null,
                    PaidAt = paid?.PaidAt,
                    PaidByName = paid?.PaidByUser == null
                        ? null
                        : $"{paid.PaidByUser.FirstName} {paid.PaidByUser.LastName}".Trim(),
                    PaymentNote = paid?.PaymentNote
                });
            }

            string? role = null;
            var isFormer = false;
            if (staffUserId.HasValue && staffById.TryGetValue(staffUserId.Value, out var found))
            {
                role = found.IsStaff ? found.Role : null;
                isFormer = !found.IsStaff;
            }
            else if (staffUserId.HasValue)
            {
                // Linked to an account that no longer exists at all.
                isFormer = true;
            }

            var dto = new AdminSalaryPayoutDto
            {
                PayeeKey = payeeKey,
                StaffUserId = staffUserId,
                Name = name,
                Role = role,
                IsFormerStaff = isFormer,
                SalaryTotal = salaryTotal,
                BonusTotal = bonusInCurrency,
                BonusTotalGel = bonusGel,
                BonusTotalUsd = bonusUsd,
                MonthTotal = monthTotal,
                Currency = currency,
                MonthTotalUsd = monthTotalUsd,
                UsdPerGel = usdPerGel,
                Instalments = instalments,
                UnpaidAmount = instalments.Where(i => !i.IsPaid).Sum(i => i.Amount)
            };

            dto.IsFullyPaid = instalments.All(i => i.IsPaid);
            dto.IsPartiallyPaid = !dto.IsFullyPaid && instalments.Any(i => i.IsPaid);

            if (mixedCurrencies)
                dto.Warnings.Add("This month mixes currencies for this person, so the amounts are shown in USD.");

            // A GEL salary with no rate behind it is reporting lari as dollars — visibly wrong
            // rather than silently wrong, but the owner should know before paying against it.
            if (!mixedCurrencies && ExpenseCurrency.IsGel(currency) && !usdPerGel.HasValue && monthTotal != 0)
                dto.Warnings.Add("No exchange rate for this month yet, so the USD figure is not converted.");

            if (salaryTotal == 0 && bonusGel == 0 && instalments.Any(i => i.IsPaid))
                dto.Warnings.Add("This month no longer has a salary on file, but a payment was recorded against it.");

            // The bonus was earned but the second payment has already gone out at the old figure.
            // Frozen on purpose — but somebody has to know they are still owed the difference.
            var secondPaid = payments.FirstOrDefault(p => p.Half == SalaryPaymentSchedule.SecondHalf);
            if (secondPaid != null && bonusInCurrency != secondPaid.PaidBonusAmount)
                dto.Warnings.Add(
                    "The bonus for this month changed after the second payment was recorded. " +
                    "What was paid stays as it was — undo and re-record it if the difference is owed.");

            return dto;
        }

        private static bool IsStaffRole(UserRole role)
            => role == UserRole.SuperAdmin || role == UserRole.Admin || role == UserRole.Moderator;
    }

    public interface IAdminSalaryPayoutService
    {
        Task<AdminSalaryPayoutListDto> GetAsync(int year, int month);
        Task<AdminSalaryPayoutListDto> MarkPaidAsync(
            int year, int month, string payeeKey, int half, MarkSalaryPaidDto dto, int paidByUserId);
        Task<AdminSalaryPayoutListDto> UndoPaymentAsync(int year, int month, string payeeKey, int half);
        Task<AdminSalaryPayoutListDto> UpdatePayeeDetailsAsync(
            int year, int month, string payeeKey, UpdateSalaryPayeeDetailsDto dto, int byUserId);
    }
}

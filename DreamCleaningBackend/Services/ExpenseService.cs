using System.Globalization;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.DTOs;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services
{
    public class ExpenseService : IExpenseService
    {
        private readonly ApplicationDbContext _context;
        private readonly Interfaces.IAuditService _audit;
        private readonly IFinancialRateService _rates;

        public ExpenseService(
            ApplicationDbContext context,
            Interfaces.IAuditService audit,
            IFinancialRateService rates)
        {
            _context = context;
            _audit = audit;
            _rates = rates;
        }

        public async Task<List<ExpenseDto>> GetAllAsync()
        {
            var rows = await _context.Expenses
                .Include(e => e.CreatedByUser)
                .Include(e => e.Category)
                .OrderByDescending(e => e.StartDate)
                .ThenByDescending(e => e.Id)
                .ToListAsync();

            var staffNames = await LoadStaffNamesAsync(rows);
            return rows.Select(e => ToDto(e, staffNames)).ToList();
        }

        public async Task<ExpenseDto?> GetByIdAsync(int id)
        {
            var row = await _context.Expenses
                .Include(e => e.CreatedByUser)
                .Include(e => e.Category)
                .FirstOrDefaultAsync(e => e.Id == id);
            if (row == null) return null;
            return ToDto(row, await LoadStaffNamesAsync(new[] { row }));
        }

        public async Task<ExpenseDto> CreateAsync(CreateExpenseDto dto, int byUserId)
        {
            await ValidateInputAsync(dto);
            var (staffUserId, name) = await ResolveSalaryIdentityAsync(dto);

            var row = new Expense
            {
                Name = name,
                StaffUserId = staffUserId,
                Amount = dto.Amount,
                Currency = ResolveCurrency(dto),
                CategoryId = dto.CategoryId,
                StartDate = dto.StartDate.Date,
                IsRecurring = dto.IsRecurring,
                FrequencyMonths = dto.IsRecurring ? dto.FrequencyMonths : null,
                EndDate = dto.IsRecurring ? dto.EndDate?.Date : null,
                ProrateByDay = dto.IsRecurring && dto.FrequencyMonths == 1 && dto.ProrateByDay,
                Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
                CreatedByUserId = byUserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Expenses.Add(row);
            await _context.SaveChangesAsync();

            await _audit.LogCreateAsync(row);

            return (await GetByIdAsync(row.Id))!;
        }

        public async Task<ExpenseDto> UpdateAsync(int id, UpdateExpenseDto dto)
        {
            await ValidateInputAsync(dto);
            var (staffUserId, name) = await ResolveSalaryIdentityAsync(dto);

            var row = await _context.Expenses.FirstOrDefaultAsync(e => e.Id == id);
            if (row == null)
                throw new InvalidOperationException("Expense not found.");

            // Full scalar copy before anything moves — see AuditSnapshot for why a hand-picked
            // subset would record every uncopied field as a change from zero.
            var before = AuditSnapshot.Of(row);

            row.Name = name;
            row.StaffUserId = staffUserId;
            row.Amount = dto.Amount;
            row.Currency = ResolveCurrency(dto);
            row.CategoryId = dto.CategoryId;
            row.StartDate = dto.StartDate.Date;
            row.IsRecurring = dto.IsRecurring;
            row.FrequencyMonths = dto.IsRecurring ? dto.FrequencyMonths : null;
            row.EndDate = dto.IsRecurring ? dto.EndDate?.Date : null;
            row.ProrateByDay = dto.IsRecurring && dto.FrequencyMonths == 1 && dto.ProrateByDay;
            row.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
            row.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await _audit.LogUpdateAsync(before, row);

            return (await GetByIdAsync(row.Id))!;
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var row = await _context.Expenses.FirstOrDefaultAsync(e => e.Id == id);
            if (row == null) return false;
            // Before the remove, while the row still carries its values.
            await _audit.LogDeleteAsync(row);
            _context.Expenses.Remove(row);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<List<ExpenseOccurrenceDto>> GetOccurrencesInRangeAsync(DateTime from, DateTime to)
        {
            // Pull every row whose start could plausibly hit [from, to). For one-time rows that
            // means StartDate ∈ [from, to). For recurring rows we can't push the projection
            // into SQL cleanly, so we filter SQL-side only by `StartDate < to`, then expand
            // in memory and filter by [from, to) per occurrence.
            var fromD = from.Date;
            var toD = to.Date;

            var rows = await _context.Expenses
                .Include(e => e.Category)
                .Where(e => e.StartDate < toD)
                .ToListAsync();

            var staffNames = await LoadStaffNamesAsync(rows);
            var fx = await LoadFxRatesAsync(rows);

            var output = new List<ExpenseOccurrenceDto>();
            foreach (var e in rows)
            {
                foreach (var (date, amount) in ProjectOccurrences(e, fromD, toD))
                {
                    // Converted per OCCURRENCE, at the rate locked for the month that occurrence
                    // falls in — a salary running across a year is reported at each month's own
                    // rate, exactly like the admin bonuses beside it.
                    var (usd, rate) = ToUsd(amount, e.Currency, date, fx);

                    output.Add(new ExpenseOccurrenceDto
                    {
                        ExpenseId = e.Id,
                        Name = ResolveRowDisplayName(e, staffNames),
                        StaffUserId = e.StaffUserId,
                        CategoryId = e.CategoryId,
                        CategoryName = e.Category?.Name ?? string.Empty,
                        Date = date,
                        Amount = usd,
                        AmountInCurrency = amount,
                        Currency = ExpenseCurrency.Normalize(e.Currency),
                        UsdPerGel = rate,
                        IsRecurring = e.IsRecurring
                    });
                }
            }

            return output.OrderByDescending(o => o.Date).ToList();
        }

        public async Task<decimal> GetTotalInRangeAsync(DateTime from, DateTime to)
        {
            var occs = await GetOccurrencesInRangeAsync(from, to);
            return occs.Sum(o => o.Amount);
        }

        public async Task<ExpenseBreakdownDto> GetBreakdownAsync(DateTime from, DateTime to)
        {
            var occs = await GetOccurrencesInRangeAsync(from, to);
            var grouped = occs
                .GroupBy(o => new { o.CategoryId, o.CategoryName })
                .OrderBy(g => g.Key.CategoryId)
                .Select(g => new ExpenseCategoryBreakdownDto
                {
                    CategoryId = g.Key.CategoryId,
                    CategoryName = g.Key.CategoryName,
                    Total = g.Sum(x => x.Amount),
                    Items = g.OrderByDescending(x => x.Date).ToList()
                })
                .ToList();

            return new ExpenseBreakdownDto
            {
                Total = occs.Sum(o => o.Amount),
                ByCategory = grouped
            };
        }

        public async Task<GroupedExpensesDto> GetGroupedAsync(int year, int month)
        {
            if (month < 1 || month > 12)
                throw new InvalidOperationException("Month must be between 1 and 12.");

            var monthStart = new DateTime(year, month, 1);
            var monthEnd = monthStart.AddMonths(1);
            // "All-time" = everything charged up to the end of the selected month (so a future
            // month doesn't pre-count occurrences that haven't happened yet relative to the view).
            var allTimeTo = monthEnd;

            var categories = await _context.ExpenseCategories
                .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Id)
                .ToListAsync();

            var rows = await _context.Expenses
                .Include(e => e.Category)
                .Where(e => e.StartDate < allTimeTo)
                .ToListAsync();

            var staffNames = await LoadStaffNamesAsync(rows);
            var fx = await LoadFxRatesAsync(rows);

            // Per-row month + all-time totals, in USD (every total on this page is comparable) and
            // again in the row's own currency (what the owner actually typed and hands over).
            var monthByRow = new Dictionary<int, decimal>();
            var allTimeByRow = new Dictionary<int, decimal>();
            var monthByRowInCurrency = new Dictionary<int, decimal>();
            var allTimeByRowInCurrency = new Dictionary<int, decimal>();
            foreach (var e in rows)
            {
                var inMonth = ProjectOccurrences(e, monthStart, monthEnd).ToList();
                var allTime = ProjectOccurrences(e, DateTime.MinValue, allTimeTo).ToList();

                monthByRowInCurrency[e.Id] = inMonth.Sum(o => o.Amount);
                allTimeByRowInCurrency[e.Id] = allTime.Sum(o => o.Amount);
                // Summed per occurrence rather than converting the total once: occurrences of one
                // recurring salary can fall in months with different rates.
                monthByRow[e.Id] = inMonth.Sum(o => ToUsd(o.Amount, e.Currency, o.Date, fx).Usd);
                allTimeByRow[e.Id] = allTime.Sum(o => ToUsd(o.Amount, e.Currency, o.Date, fx).Usd);
            }

            var rowsByCategory = rows.ToLookup(e => e.CategoryId);

            var categoryDtos = new List<GroupedCategoryDto>();
            foreach (var cat in categories)
            {
                var catRows = rowsByCategory[cat.Id].ToList();

                // Aggregate by normalized (trimmed, case-insensitive) name within the category —
                // except salary rows carrying a staff link, which group by PERSON instead. See
                // SalaryExpenseRules.GroupingKey for why.
                var names = catRows
                    .GroupBy(e => SalaryExpenseRules.GroupingKey(e.StaffUserId, e.Name), StringComparer.Ordinal)
                    .Select(g => new GroupedNameDto
                    {
                        // Display the most recently created spelling of the name — or, for a staff
                        // line, that person's current name while their account still exists.
                        Name = ResolveRowDisplayName(g.OrderByDescending(e => e.CreatedAt).First(), staffNames),
                        StaffUserId = g.First().StaffUserId,
                        StaffUserRemoved = IsRemovedStaffRow(g.First(), staffNames),
                        MonthTotal = g.Sum(e => monthByRow[e.Id]),
                        AllTimeTotal = g.Sum(e => allTimeByRow[e.Id]),
                        // Only when the whole line shares one non-USD currency — a mixed line has
                        // no single figure to print, and inventing one would be a fabricated total.
                        Currency = SingleNonUsdCurrency(g),
                        MonthTotalInCurrency = SingleNonUsdCurrency(g) == null
                            ? null : g.Sum(e => monthByRowInCurrency[e.Id]),
                        AllTimeTotalInCurrency = SingleNonUsdCurrency(g) == null
                            ? null : g.Sum(e => allTimeByRowInCurrency[e.Id]),
                        Entries = g
                            .OrderByDescending(e => e.StartDate).ThenByDescending(e => e.Id)
                            .Select(e => ToDto(e, staffNames)).ToList()
                    })
                    // Names with activity in the month sort first (by month spend), then the rest.
                    .OrderByDescending(n => n.MonthTotal)
                    .ThenByDescending(n => n.AllTimeTotal)
                    .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                categoryDtos.Add(new GroupedCategoryDto
                {
                    CategoryId = cat.Id,
                    CategoryName = cat.Name,
                    DisplayOrder = cat.DisplayOrder,
                    MonthTotal = names.Sum(n => n.MonthTotal),
                    Names = names
                });
            }

            return new GroupedExpensesDto
            {
                Year = year,
                Month = month,
                MonthLabel = monthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                MonthTotal = categoryDtos.Sum(c => c.MonthTotal),
                Categories = categoryDtos
            };
        }

        // ── Salary staff picker ─────────────────────────────────────────────────

        public async Task<List<ExpenseStaffMemberDto>> GetStaffMembersAsync()
        {
            // Everyone who could be paid a salary today.
            var current = await _context.Users
                .Where(u => !u.IsDeleted &&
                            (u.Role == UserRole.SuperAdmin || u.Role == UserRole.Admin || u.Role == UserRole.Moderator))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.Role, u.IsActive })
                .ToListAsync();

            // Plus everyone already on file. A salary is a record of what was paid, so somebody who
            // has left — demoted, blocked or deleted outright — has to stay pickable: their last
            // payment is usually entered AFTER they go.
            //
            // Grouped in memory rather than in SQL on purpose: the fallback name is the LATEST
            // spelling on file, and "order inside the group, then take the first" is not something
            // EF Core can translate — it compiles fine and throws at runtime. Three columns of the
            // salary rows is a small read.
            var linked = (await _context.Expenses
                    .Where(e => e.CategoryId == SalaryExpenseRules.SalariesCategoryId && e.StaffUserId != null)
                    .Select(e => new { StaffUserId = e.StaffUserId!.Value, e.Name, e.CreatedAt })
                    .ToListAsync())
                .GroupBy(e => e.StaffUserId)
                .Select(g => new
                {
                    StaffUserId = g.Key,
                    Count = g.Count(),
                    // The name to fall back on when the account itself is gone.
                    LatestName = g.OrderByDescending(e => e.CreatedAt).First().Name
                })
                .ToList();

            var countById = linked.ToDictionary(l => l.StaffUserId, l => l.Count);
            var currentIds = current.Select(c => c.Id).ToHashSet();

            var result = current
                .Select(u => new ExpenseStaffMemberDto
                {
                    Id = u.Id,
                    FullName = ResolveStaffLabel(SalaryExpenseRules.FormatStaffName(u.FirstName, u.LastName), u.Email),
                    Email = u.Email,
                    Role = u.Role.ToString(),
                    IsActive = u.IsActive,
                    IsFormer = false,
                    SalaryEntryCount = countById.TryGetValue(u.Id, out var c) ? c : 0
                })
                .ToList();

            var formerIds = linked.Select(l => l.StaffUserId).Where(id => !currentIds.Contains(id)).ToList();
            if (formerIds.Count > 0)
            {
                // They may still hold a User row (role changed to Customer, or account deactivated)
                // or be gone entirely — in which case the snapshot on their own rows names them.
                var stillOnFile = await _context.Users
                    .Where(u => formerIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.Role, u.IsActive })
                    .ToListAsync();
                var stillOnFileById = stillOnFile.ToDictionary(u => u.Id);

                foreach (var l in linked.Where(l => formerIds.Contains(l.StaffUserId)))
                {
                    stillOnFileById.TryGetValue(l.StaffUserId, out var u);
                    result.Add(new ExpenseStaffMemberDto
                    {
                        Id = l.StaffUserId,
                        FullName = u != null
                            ? ResolveStaffLabel(SalaryExpenseRules.FormatStaffName(u.FirstName, u.LastName), u.Email)
                            : l.LatestName,
                        Email = u?.Email,
                        // Null role = no longer staff at all (deleted, or moved off a staff role).
                        // Same rule as the "former" marker on the grouped list, so the picker and
                        // the list can never describe the same person differently.
                        Role = u != null && IsStaffRole(u.Role) ? u.Role.ToString() : null,
                        IsActive = u?.IsActive ?? false,
                        IsFormer = true,
                        SalaryEntryCount = l.Count
                    });
                }
            }

            return result
                .OrderBy(r => r.IsFormer)
                .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ── Category management ─────────────────────────────────────────────────

        public async Task<List<ExpenseCategoryDto>> GetCategoriesAsync()
        {
            var counts = await _context.Expenses
                .GroupBy(e => e.CategoryId)
                .Select(g => new { CategoryId = g.Key, Count = g.Count() })
                .ToListAsync();
            var countMap = counts.ToDictionary(c => c.CategoryId, c => c.Count);

            return await _context.ExpenseCategories
                .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Id)
                .Select(c => new ExpenseCategoryDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    DisplayOrder = c.DisplayOrder,
                    IsSystem = c.IsSystem,
                    ExpenseCount = countMap.ContainsKey(c.Id) ? countMap[c.Id] : 0
                })
                .ToListAsync();
        }

        public async Task<ExpenseCategoryDto> CreateCategoryAsync(SaveExpenseCategoryDto dto)
        {
            var name = (dto.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Category name is required.");
            if (await _context.ExpenseCategories.AnyAsync(c => c.Name == name))
                throw new InvalidOperationException("A category with that name already exists.");

            // PK is not auto-generated — assign the next free Id and append to the display order.
            var maxId = await _context.ExpenseCategories.MaxAsync(c => (int?)c.Id) ?? -1;
            var maxOrder = await _context.ExpenseCategories.MaxAsync(c => (int?)c.DisplayOrder) ?? -1;

            var row = new ExpenseCategory
            {
                Id = maxId + 1,
                Name = name,
                DisplayOrder = maxOrder + 1,
                IsSystem = false,
                CreatedAt = DateTime.UtcNow
            };
            _context.ExpenseCategories.Add(row);
            await _context.SaveChangesAsync();

            await _audit.LogCreateAsync(row);

            return new ExpenseCategoryDto
            {
                Id = row.Id,
                Name = row.Name,
                DisplayOrder = row.DisplayOrder,
                IsSystem = row.IsSystem,
                ExpenseCount = 0
            };
        }

        public async Task<ExpenseCategoryDto> UpdateCategoryAsync(int id, SaveExpenseCategoryDto dto)
        {
            var name = (dto.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Category name is required.");

            var row = await _context.ExpenseCategories.FirstOrDefaultAsync(c => c.Id == id);
            if (row == null)
                throw new InvalidOperationException("Category not found.");
            if (await _context.ExpenseCategories.AnyAsync(c => c.Name == name && c.Id != id))
                throw new InvalidOperationException("A category with that name already exists.");

            var beforeCategory = AuditSnapshot.Of(row);
            row.Name = name;
            await _context.SaveChangesAsync();

            await _audit.LogUpdateAsync(beforeCategory, row);

            var count = await _context.Expenses.CountAsync(e => e.CategoryId == id);
            return new ExpenseCategoryDto
            {
                Id = row.Id,
                Name = row.Name,
                DisplayOrder = row.DisplayOrder,
                IsSystem = row.IsSystem,
                ExpenseCount = count
            };
        }

        public async Task<bool> DeleteCategoryAsync(int id)
        {
            var row = await _context.ExpenseCategories.FirstOrDefaultAsync(c => c.Id == id);
            if (row == null) return false;
            if (row.IsSystem)
                throw new InvalidOperationException("Built-in categories can't be deleted.");
            if (await _context.Expenses.AnyAsync(e => e.CategoryId == id))
                throw new InvalidOperationException("This category still has expenses. Move or delete them first.");

            await _audit.LogDeleteAsync(row);

            _context.ExpenseCategories.Remove(row);
            await _context.SaveChangesAsync();
            return true;
        }

        // ──────────────────────────────────────────────────────────────────────────────

        // Yields (date, amount) for every occurrence of an expense that falls in [from, to).
        //
        //   • One-time row: yields (StartDate, Amount) if StartDate ∈ [from, to).
        //   • Recurring, NOT prorated: anchored occurrences StartDate + k*FrequencyMonths, each
        //     charging the full Amount (e.g. a $20 subscription that started mid-month still
        //     charges $20 every month).
        //   • Recurring, prorated (monthly only): one charge per calendar month from the start
        //     month through the end month, with the first/last partial months reduced by the
        //     fraction of days the expense was actually active that month.
        //
        // Dates are returned at midnight (date-only).
        private static IEnumerable<(DateTime Date, decimal Amount)> ProjectOccurrences(Expense e, DateTime from, DateTime to)
        {
            var start = e.StartDate.Date;

            // One-time.
            if (!e.IsRecurring || !e.FrequencyMonths.HasValue || e.FrequencyMonths.Value <= 0)
            {
                if (start >= from && start < to)
                    yield return (start, e.Amount);
                yield break;
            }

            var step = e.FrequencyMonths.Value;
            var endCap = e.EndDate?.Date;

            // Prorated monthly: walk calendar months, reduce the partial first/last month.
            if (e.ProrateByDay && step == 1)
            {
                var startMonth = new DateTime(start.Year, start.Month, 1);
                var endMonth = endCap.HasValue ? new DateTime(endCap.Value.Year, endCap.Value.Month, 1) : (DateTime?)null;

                const int hardCap = 2000;
                int k = 0;
                while (k < hardCap)
                {
                    var monthStart = startMonth.AddMonths(k);
                    if (monthStart >= to) yield break;
                    if (endMonth.HasValue && monthStart > endMonth.Value) yield break;

                    var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
                    var firstActiveDay = (monthStart == startMonth) ? start.Day : 1;
                    var lastActiveDay = (endMonth.HasValue && monthStart == endMonth.Value) ? endCap!.Value.Day : daysInMonth;
                    var activeDays = lastActiveDay - firstActiveDay + 1;
                    if (activeDays <= 0) { k++; continue; }

                    // Attribute the first month to the real start date; later months to the 1st.
                    var occDate = (monthStart == startMonth) ? start : monthStart;
                    var amount = activeDays >= daysInMonth
                        ? e.Amount
                        : Math.Round(e.Amount * activeDays / daysInMonth, 2, MidpointRounding.AwayFromZero);

                    if (occDate >= from && occDate < to)
                        yield return (occDate, amount);
                    k++;
                }
                yield break;
            }

            // Recurring, full amount on each anchored occurrence.
            {
                const int hardCap = 2000;
                int k = 0;
                while (k < hardCap)
                {
                    var occ = start.AddMonths(step * k);
                    if (occ >= to) yield break;
                    if (endCap.HasValue && occ > endCap.Value) yield break;
                    if (occ >= from) yield return (occ, e.Amount);
                    k++;
                }
            }
        }

        // ── Currency ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The month-locked USD/GEL rates needed by these rows, keyed year*100+month.
        /// </summary>
        /// <remarks>
        /// Returns EMPTY when nothing is in a foreign currency, which is the overwhelmingly common
        /// case — the FX snapshots are created on demand and hit an external API on a miss, so an
        /// all-USD page must not pay for that. The window is taken from the rows themselves rather
        /// than the caller's range because a recurring expense projects occurrences well past its
        /// StartDate.
        /// </remarks>
        private async Task<Dictionary<int, decimal>> LoadFxRatesAsync(IEnumerable<Expense> rows)
        {
            var foreign = rows.Where(r => ExpenseCurrency.IsGel(r.Currency)).ToList();
            if (foreign.Count == 0) return new Dictionary<int, decimal>();

            var earliest = foreign.Min(r => r.StartDate).Date;
            // Recurring rows run to today (or to their end date), and an ongoing month still needs
            // a rate — so the window always reaches at least the current month.
            var latest = foreign
                .Select(r => r.EndDate?.Date ?? DateTime.UtcNow.Date)
                .Append(DateTime.UtcNow.Date)
                .Max();

            var snaps = await _rates.GetOrCreateForRangeAsync(
                new DateTime(earliest.Year, earliest.Month, 1),
                new DateTime(latest.Year, latest.Month, 1).AddMonths(1));

            return snaps.ToDictionary(kv => kv.Key, kv => kv.Value.UsdPerGel);
        }

        /// <summary>
        /// One occurrence in USD, plus the rate that got it there (null when nothing was converted).
        /// </summary>
        private static (decimal Usd, decimal? Rate) ToUsd(
            decimal amount, string? currency, DateTime occurrenceDate, IReadOnlyDictionary<int, decimal> fx)
        {
            if (!ExpenseCurrency.IsGel(currency)) return (amount, null);

            // A month with no snapshot loaded leaves the amount unconverted rather than zeroing it
            // — see ExpenseCurrency.ToUsd for why losing the cost is the worse failure.
            if (!fx.TryGetValue(occurrenceDate.Year * 100 + occurrenceDate.Month, out var rate))
                return (amount, null);

            return (ExpenseCurrency.ToUsd(amount, currency, rate), rate);
        }

        /// <summary>
        /// The one non-USD currency every row in a group shares, or null when they are all USD or
        /// the group mixes currencies.
        /// </summary>
        private static string? SingleNonUsdCurrency(IEnumerable<Expense> group)
        {
            var currencies = group.Select(e => ExpenseCurrency.Normalize(e.Currency)).Distinct().ToList();
            return currencies.Count == 1 && currencies[0] != ExpenseCurrency.Usd ? currencies[0] : null;
        }

        /// <summary>
        /// Only a salary may be entered in a foreign currency; everything else is USD. Applied on
        /// write, so moving a row to another category cannot leave a stale GEL tag on it.
        /// </summary>
        private static string ResolveCurrency(CreateExpenseDto dto)
            => ExpenseCurrency.AllowsCurrencyChoice(dto.CategoryId)
                ? ExpenseCurrency.Normalize(dto.Currency)
                : ExpenseCurrency.Usd;

        // ── Salary identity ───────────────────────────────────────────────────────

        /// <summary>
        /// Resolves what a row is linked to and what it is called. A salary with a staff member
        /// picked takes its Name from that person's account — the client's Name is ignored, so the
        /// snapshot can never disagree with who was chosen. Every other row keeps its typed name,
        /// and any staff link is dropped: moving a row out of Salaries must not leave one behind.
        /// </summary>
        private async Task<(int? StaffUserId, string Name)> ResolveSalaryIdentityAsync(CreateExpenseDto dto)
        {
            var typedName = (dto.Name ?? string.Empty).Trim();

            if (!SalaryExpenseRules.IsSalaryCategory(dto.CategoryId) || !dto.StaffUserId.HasValue)
                return (null, typedName);

            var staffId = dto.StaffUserId.Value;
            var staff = await _context.Users
                .Where(u => u.Id == staffId)
                .Select(u => new { u.FirstName, u.LastName, u.Email })
                .FirstOrDefaultAsync();

            if (staff != null)
                return (staffId, ResolveStaffLabel(SalaryExpenseRules.FormatStaffName(staff.FirstName, staff.LastName), staff.Email));

            // The account is gone, and that is NOT an error here — it is the case this whole feature
            // exists for. A departed person's last salary is usually entered after they leave, and
            // their existing rows have to stay editable. Keep the link (it is what holds their rows
            // on one line) and take the name from what is already on file for them, so a re-save
            // cannot quietly rename somebody nobody can look up any more.
            var snapshot = await _context.Expenses
                .Where(e => e.StaffUserId == staffId)
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => e.Name)
                .FirstOrDefaultAsync();

            var fallback = !string.IsNullOrWhiteSpace(snapshot) ? snapshot.Trim() : typedName;
            if (string.IsNullOrWhiteSpace(fallback))
                throw new InvalidOperationException("That staff member is no longer on file, so this salary needs a name.");

            return (staffId, fallback);
        }

        /// <summary>
        /// The Name column is required, so a staff member with no name on file (possible — the
        /// column is only required at the API edge) falls back to their email rather than writing
        /// an empty label nobody can read.
        /// </summary>
        private static string ResolveStaffLabel(string fullName, string? email)
            => string.IsNullOrWhiteSpace(fullName) ? (email ?? string.Empty).Trim() : fullName;

        // Two different questions about one linked account, deliberately answered separately: what
        // the row is CALLED, and whether the person is still staff. An admin who was demoted to
        // Customer keeps their name (it is still theirs, and still current) but is no longer on the
        // team — collapsing the two would either hide the change or start showing a stale name.
        private sealed record StaffLookup(string Name, bool IsCurrentStaff);

        /// <summary>Every staff member referenced by these rows. Missing from the map = deleted.</summary>
        private async Task<Dictionary<int, StaffLookup>> LoadStaffNamesAsync(IEnumerable<Expense> rows)
        {
            var ids = rows
                .Where(r => r.StaffUserId.HasValue)
                .Select(r => r.StaffUserId!.Value)
                .Distinct()
                .ToList();
            if (ids.Count == 0) return new Dictionary<int, StaffLookup>();

            var users = await _context.Users
                .Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.Role, u.IsDeleted })
                .ToListAsync();

            return users.ToDictionary(
                u => u.Id,
                u => new StaffLookup(
                    ResolveStaffLabel(SalaryExpenseRules.FormatStaffName(u.FirstName, u.LastName), u.Email),
                    IsStaffRole(u.Role) && !u.IsDeleted));
        }

        // A BLOCKED account (IsActive = false) is still staff. Being unable to sign in says nothing
        // about whether the company owes somebody a salary — only their role does.
        private static bool IsStaffRole(UserRole role)
            => role == UserRole.SuperAdmin || role == UserRole.Admin || role == UserRole.Moderator;

        private static string ResolveRowDisplayName(Expense e, IReadOnlyDictionary<int, StaffLookup> staff)
        {
            string? live = null;
            if (e.StaffUserId.HasValue && staff.TryGetValue(e.StaffUserId.Value, out var found))
                live = found.Name;
            return SalaryExpenseRules.ResolveDisplayName(e.Name, live);
        }

        /// <summary>
        /// True when the row names somebody who is no longer staff — deleted outright, soft-deleted,
        /// or moved off a staff role. Their salaries stay exactly where they are either way; this is
        /// only what puts a "former" marker beside them.
        /// </summary>
        private static bool IsRemovedStaffRow(Expense e, IReadOnlyDictionary<int, StaffLookup> staff)
            => e.StaffUserId.HasValue
               && (!staff.TryGetValue(e.StaffUserId.Value, out var found) || !found.IsCurrentStaff);

        // ──────────────────────────────────────────────────────────────────────────────

        private async Task ValidateInputAsync(CreateExpenseDto dto)
        {
            // A salary with a staff member picked takes its name from that account, so the client
            // is not required to send one.
            var namedByStaff = SalaryExpenseRules.IsSalaryCategory(dto.CategoryId) && dto.StaffUserId.HasValue;
            if (!namedByStaff && string.IsNullOrWhiteSpace(dto.Name))
                throw new InvalidOperationException("Name is required.");
            if (dto.Amount < 0)
                throw new InvalidOperationException("Amount cannot be negative.");
            if (!await _context.ExpenseCategories.AnyAsync(c => c.Id == dto.CategoryId))
                throw new InvalidOperationException("Selected category does not exist.");
            if (dto.IsRecurring)
            {
                if (!dto.FrequencyMonths.HasValue || dto.FrequencyMonths.Value <= 0)
                    throw new InvalidOperationException("Recurring expenses need a frequency in months > 0.");
                if (dto.EndDate.HasValue && dto.EndDate.Value.Date < dto.StartDate.Date)
                    throw new InvalidOperationException("End date cannot be before start date.");
                if (dto.ProrateByDay && dto.FrequencyMonths.Value != 1)
                    throw new InvalidOperationException("Day-based proration is only available for monthly expenses.");
            }
        }

        private static ExpenseDto ToDto(Expense e, IReadOnlyDictionary<int, StaffLookup> staffNames) => new()
        {
            Id = e.Id,
            Name = ResolveRowDisplayName(e, staffNames),
            Amount = e.Amount,
            Currency = ExpenseCurrency.Normalize(e.Currency),
            CategoryId = e.CategoryId,
            CategoryName = e.Category?.Name ?? string.Empty,
            StaffUserId = e.StaffUserId,
            StaffUserRemoved = IsRemovedStaffRow(e, staffNames),
            StartDate = e.StartDate,
            IsRecurring = e.IsRecurring,
            FrequencyMonths = e.FrequencyMonths,
            EndDate = e.EndDate,
            ProrateByDay = e.ProrateByDay,
            Notes = e.Notes,
            CreatedByUserId = e.CreatedByUserId,
            CreatedByUserName = e.CreatedByUser != null
                ? $"{e.CreatedByUser.FirstName} {e.CreatedByUser.LastName}".Trim()
                : null,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt
        };
    }
}

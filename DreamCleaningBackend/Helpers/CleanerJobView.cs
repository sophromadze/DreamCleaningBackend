using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// WHAT A CLEANER IS TOLD ABOUT A JOB - the rules shared by the assignment email, the
    /// assignment SMS and the cleaner portal.
    ///
    /// The portal exists to show a cleaner the same job they were mailed about, so the two must not
    /// be able to describe it differently. Three questions were being answered inline in
    /// EmailService and would have been answered a second time here; they live in one place now:
    ///
    ///   1. Which extras does a cleaner see?  (<see cref="IsExtraHiddenFromCleaners"/>), and which
    ///      priced service lines?             (<see cref="IsServiceLineHiddenFromCleaners"/>)
    ///   2. What is the address, on one line? (<see cref="BuildFullAddress"/>)
    ///   3. Must they bring cleaning supplies? (<see cref="RequiresCleanerToBringSupplies"/>)
    ///   4. What kind of cleaning is it?      (<see cref="ResolveCleaningTypeName"/>)
    ///
    /// There is no "bring solutions" column anywhere in the model, and inventing one would have
    /// left the emails and the portal reading different sources. The signal is, and has always
    /// been, whether the customer bought the "Cleaning Supplies" extra: if they did not, the
    /// cleaner brings the products. That single fact is resolved through
    /// <see cref="CustomerSupplyChecklist.HasCleaningSuppliesExtra"/>, which is also what builds
    /// the customer's own "please provide the following items" list - so the two halves of the same
    /// arrangement cannot drift apart.
    /// </summary>
    public static class CleanerJobView
    {
        /// <summary>
        /// Extras that are never listed as work for the cleaner to do.
        ///
        /// "Cleaning Supplies" is answered by its own Supplies line, so repeating it in the task
        /// list reads as a second job. "Cleaning Essentials" is the same shape of thing and gets
        /// the same treatment - its own Essentials line answers it. "Extra Cleaners" is staffing,
        /// not work on site. These exclusions predate the portal - they were in the assignment
        /// email - and matching them here is why the portal's list is the same list the cleaner
        /// was sent.
        /// </summary>
        public static bool IsExtraHiddenFromCleaners(string? extraServiceName)
        {
            var name = extraServiceName?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return true;
            if (name.Contains(CustomerSupplyChecklist.CleaningSuppliesMatch, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains(CustomerSupplyChecklist.CleaningEssentialsMatch, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(name, OrderPricingCalculator.ExtraCleanersName, StringComparison.OrdinalIgnoreCase)) return true;
            // Deep / Super Deep is the CLEANING TYPE (see ResolveCleaningTypeName), the same rule
            // the booking page follows - it is never an extras card there either. Leaving it in the
            // task list as well would have the same job named twice on one screen.
            if (name.Contains("deep cleaning", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Priced service lines that are never listed as their own row for a cleaner, because
        /// something else on the same screen already answers them.
        ///
        /// LEVELS is the whole list. It is an ordinary Service row (ServiceKey "levels") so that it
        /// prices through the normal threshold machinery, but every cleaner-facing surface reads
        /// the count off the denormalized <c>Order.LevelsQuantity</c> column instead - the
        /// assignment mail and SMS have their own Levels row, and the portal has its own gated
        /// chip beside the property type. Left in the generic loop as well, the portal printed
        /// "House · 2 Levels · 2 Bedrooms · 1 Bathroom · 1,000 sq ft · 2 Levels": the same fact
        /// twice, in one row of chips, which reads as two different measurements.
        ///
        /// This is the same rule the booking page, the user order-edit page and the home hero
        /// already follow - levels is filtered out of every generic service loop because it has a
        /// gated block of its own. The portal was the one cleaner-facing surface that had both.
        ///
        /// Matched on the KEY, never the Id or the Name: both differ between dev and production.
        /// </summary>
        public static bool IsServiceLineHiddenFromCleaners(string? serviceKey)
        {
            var key = serviceKey?.Trim();
            if (string.IsNullOrWhiteSpace(key)) return false;
            return string.Equals(key, "levels", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the CLEANER has to bring cleaning solutions and supplies.
        ///
        /// THE DIRECTION OF THIS IS THE EASY THING TO GET WRONG, and it was wrong here: buying the
        /// "Cleaning Supplies" extra is the customer paying US to bring the products, so the extra
        /// being present means the cleaner LOADS THE CAR. Without it the customer supplies them,
        /// which is exactly why CustomerSupplyChecklist.BuildItems adds the Zep liquids, the Windex
        /// and the cloths to the customer's own "please provide" list in that case and not this
        /// one. The portal used to return the negation and told cleaners the opposite of what the
        /// assignment email had already told them ("Supplies: required" there).
        ///
        /// Reads the extras the order actually holds through the same
        /// <see cref="CustomerSupplyChecklist.HasCleaningSuppliesExtra"/> the checklist and the
        /// email read, so the three cannot disagree about what to put in the car.
        /// </summary>
        public static bool RequiresCleanerToBringSupplies(Order order)
        {
            var names = (order.OrderExtraServices ?? new List<OrderExtraService>())
                .Select(oes => oes.ExtraService?.Name);
            return CustomerSupplyChecklist.HasCleaningSuppliesExtra(names);
        }

        /// <summary>
        /// True when the CLEANER has to bring the essentials - paper towels, garbage bags, a
        /// toilet brush and a broom.
        ///
        /// Same direction and same reasoning as <see cref="RequiresCleanerToBringSupplies"/>:
        /// buying the "Cleaning Essentials" extra is the customer paying US to bring those three
        /// items, so the extra being present means the cleaner LOADS THE CAR - and it is exactly
        /// why CustomerSupplyChecklist.BuildItems drops them from the customer's own
        /// "please provide" list in that case. The two halves of one arrangement, read from one
        /// source, so the mail, the SMS, the portal and the customer's checklist cannot disagree
        /// about who is bringing the paper towels.
        ///
        /// NOTE the broom IS included as of 2026-09 (it was not before), which is why the
        /// customer's own checklist stops asking for "a broom or vacuum cleaner" the moment this
        /// extra is bought. A VACUUM is still the separate Vacuum Cleaner extra.
        /// </summary>
        public static bool RequiresCleanerToBringEssentials(Order order)
        {
            var names = (order.OrderExtraServices ?? new List<OrderExtraService>())
                .Select(oes => oes.ExtraService?.Name);
            return CustomerSupplyChecklist.HasCleaningEssentialsExtra(names);
        }

        /// <summary>
        /// THE CLEANING TYPE, as a cleaner needs it: "Deep Cleaning", "Super Deep Cleaning" or
        /// "Regular Cleaning" - never the raw "Residential Cleaning", which answers a question
        /// nobody working the job is asking. Deep and Regular are different work, different
        /// products and a different hourly rate; the service type they were both booked under is
        /// an accident of the catalogue.
        ///
        /// Deep is not a service type in this system, it is an EXTRA that carries a multiplier (see
        /// the booking rules in CLAUDE.md), so the name has to be derived from the extras rather
        /// than read off ServiceType. Order matters: "Super Deep" contains "deep", so it is tested
        /// first or every super-deep job reads as an ordinary deep one.
        ///
        /// Anything that is not the residential type keeps its own name - "Move In/Out Cleaning"
        /// and "Post Construction Cleaning" already say what the work is, and a custom
        /// ("Pre-Arranged") order carries the label an admin typed for it, which is the truth for
        /// that order by definition.
        /// </summary>
        public static string ResolveCleaningTypeName(Order order, string fallback = "")
        {
            var extraNames = (order.OrderExtraServices ?? new List<OrderExtraService>())
                .Select(oes => oes.ExtraService?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.Trim())
                .ToList();

            if (extraNames.Any(n => n.Contains("super deep", StringComparison.OrdinalIgnoreCase)))
                return "Super Deep Cleaning";

            if (extraNames.Any(n => n.Contains("deep cleaning", StringComparison.OrdinalIgnoreCase)))
                return "Deep Cleaning";

            var typeName = order.GetDisplayServiceTypeName(fallback);

            // Only the residential type is ambiguous between deep and regular, and only it gets
            // renamed. Widening this would rewrite "Office Cleaning" into "Regular Cleaning" and
            // lose the one word that told the crew what building they are walking into.
            return IsResidentialTypeName(typeName) ? "Regular Cleaning" : typeName;
        }

        /// <summary>
        /// The residential type, matched on its NAME because the Id differs between the local seed
        /// and production (see the service-type drift note in CLAUDE.md). "Residential", "Regular"
        /// and "Home" are the names it has gone by.
        /// </summary>
        private static bool IsResidentialTypeName(string? serviceTypeName)
        {
            var name = (serviceTypeName ?? string.Empty).Trim();
            if (name.Length == 0) return false;
            return name.Contains("residential", StringComparison.OrdinalIgnoreCase)
                || name.Contains("regular", StringComparison.OrdinalIgnoreCase)
                || name.Contains("home", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The service address on one line, assembled exactly as the assignment email and SMS do -
        /// street, apt/suite, city, state, zip - falling back to the apartment name when the order
        /// carries no address parts at all.
        /// </summary>
        public static string BuildFullAddress(Order order)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(order.ServiceAddress)) parts.Add(order.ServiceAddress);
            if (!string.IsNullOrWhiteSpace(order.AptSuite)) parts.Add(order.AptSuite);
            if (!string.IsNullOrWhiteSpace(order.City)) parts.Add(order.City);
            if (!string.IsNullOrWhiteSpace(order.State)) parts.Add(order.State);
            if (!string.IsNullOrWhiteSpace(order.ZipCode)) parts.Add(order.ZipCode);
            return parts.Count > 0 ? string.Join(", ", parts) : (order.ApartmentName ?? string.Empty);
        }

        /// <summary>
        /// The customer as a cleaner sees them: FIRST NAME only, matching the assignment email. A
        /// surname adds nothing to finding the door and is one more piece of a customer's identity
        /// on a screen that does not need it.
        /// </summary>
        public static string ResolveCustomerDisplayName(Order order) =>
            (order.ContactFirstName ?? string.Empty).Trim();

        /// <summary>
        /// WHAT A FINISHED JOB STOPS SAYING. A completed cleaning is a record - it earns its place
        /// in the cleaner's month because they worked it - but the reason they were ever given a
        /// customer's address was to get there, and that reason expired when the job did. So the
        /// address comes off the moment the work is done.
        ///
        /// Done here, on the cleaner's payload, rather than only in the template: a field the API
        /// still ships is a field that is one careless binding, one screenshot or one open dev-tools
        /// tab away from being visible, and "the UI does not render it" has never been the same
        /// claim as "they cannot see it". The SuperAdmin's list is untouched - they are looking at
        /// the company's own records, which is a different question.
        /// </summary>
        public static void RedactCompletedJob(DTOs.CleanerPortalJobDto job)
        {
            job.Address = string.Empty;
            // The way into somebody's home outlives the job even less than the address does.
            job.EntryMethod = null;
        }

        /// <summary>
        /// Is this job still ahead of, or in front of, the cleaner? Anything not finished and not
        /// called off - so a Pending order that has not been paid yet still shows, because the
        /// cleaner has been staffed on it and the customer's payment is not their business.
        /// </summary>
        public static bool IsCurrentJob(string? status) =>
            !OrderStatuses.Is(status, OrderStatuses.Done)
            && !OrderStatuses.IsCancelled(status)
            && !OrderStatuses.IsRefunded(status);

        /// <summary>
        /// Is this job in the cleaner's HISTORY? Uses the same "did the cleaning actually happen"
        /// test the payroll uses (<see cref="OrderStatuses.WasPerformed"/>), so a job that was done
        /// and later refunded still appears: the cleaner worked it, and a refund is a matter
        /// between the company and the customer.
        /// </summary>
        public static bool IsPastJob(string? status, string? statusBeforeRefund) =>
            OrderStatuses.WasPerformed(status, statusBeforeRefund);

        /// <summary>
        /// Does this cleaning belong on a cleaner-facing calendar AT ALL? Exactly
        /// <see cref="IsCurrentJob"/> OR <see cref="IsPastJob"/> - the two lists the cleaner's own
        /// view builds - so the system-wide month can never contain a job the cleaner's month
        /// would have dropped.
        ///
        /// What it drops is a cleaning that never happened: a CANCELLED order, and one REFUNDED
        /// BEFORE service (Refunded with anything but Done behind it). Neither is work, and both
        /// used to reach the system-wide calendar unfiltered, wearing the red pulsing dot that
        /// means work still ahead - a job called off months ago pulsing at an admin as though
        /// somebody still had to turn up for it.
        ///
        /// A job DONE and later refunded stays, as a COMPLETED one: the crew worked it, and the
        /// refund is a matter between the company and the customer. That is the same rule the
        /// payroll uses (<see cref="OrderStatuses.WasPerformed"/>) and the same one the cleaner's
        /// own history follows, so the three surfaces agree about which cleanings ever existed.
        /// </summary>
        public static bool BelongsOnTheCalendar(string? status, string? statusBeforeRefund) =>
            IsCurrentJob(status) || IsPastJob(status, statusBeforeRefund);
    }
}

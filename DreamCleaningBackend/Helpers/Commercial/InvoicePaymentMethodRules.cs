using DreamCleaningBackend.Models.Commercial;
using DreamCleaningBackend.Services.Commercial;

namespace DreamCleaningBackend.Helpers.Commercial
{
    /// <summary>
    /// The rules for the set of payment methods ONE commercial invoice offers (2026-10).
    ///
    /// Why it exists: the invoice form used to carry a single "Payment Method" radio that no
    /// payment path ever read — card availability came only from the global Billing Settings
    /// toggle — so an admin could pick Card and the customer's page would offer no card at all.
    /// Now the invoice's own set is the WHOLE answer, and these are the rules about it, in one
    /// place for the service, the public page, the PDF and the email:
    ///
    ///  - The set may not be empty and may hold only known methods. The server is the authority;
    ///    the form's own check is a convenience.
    ///  - Manual bank transfer can only be OFFERED while the bank details are complete. Half a
    ///    set of instructions is worse than none: the customer only finds out at their bank.
    ///  - The Billing Settings toggles are only the DEFAULTS a new invoice starts from
    ///    (<see cref="DefaultsFrom"/>).
    ///
    /// Pure and static, so the whole rulebook is asserted without a database.
    /// </summary>
    public static class InvoicePaymentMethodRules
    {
        /// <summary>Every method an invoice can offer, in the order every surface lists them.</summary>
        public static readonly IReadOnlyList<InvoicePaymentMethods> Ordered = new[]
        {
            InvoicePaymentMethods.OnlineBank,
            InvoicePaymentMethods.Card,
            InvoicePaymentMethods.ManualBankTransfer,
            InvoicePaymentMethods.Other
        };

        /// <summary>The bits a stored value may carry. Anything else is not a method.</summary>
        public const InvoicePaymentMethods All =
            InvoicePaymentMethods.OnlineBank
            | InvoicePaymentMethods.Card
            | InvoicePaymentMethods.ManualBankTransfer
            | InvoicePaymentMethods.Other;

        public static bool Offers(InvoicePaymentMethods set, InvoicePaymentMethods method) =>
            method != InvoicePaymentMethods.None && (set & method) == method;

        /// <summary>
        /// What the invoice FORM pre-ticks on a new invoice: exactly the three Billing Settings
        /// toggles. Manual transfer is left out while the bank details are incomplete, because the
        /// save would refuse it. May be <see cref="InvoicePaymentMethods.None"/> when every toggle
        /// is off — the admin then ticks what this invoice offers, and the form says so.
        /// </summary>
        public static InvoicePaymentMethods DefaultsFrom(BillingSettings settings)
        {
            var set = InvoicePaymentMethods.None;
            if (settings.StripeAchEnabled) set |= InvoicePaymentMethods.OnlineBank;
            if (settings.StripeCardEnabled) set |= InvoicePaymentMethods.Card;
            if (settings.ManualAchEnabled && BillingSettingsService.IsManualAchComplete(settings))
                set |= InvoicePaymentMethods.ManualBankTransfer;
            return set;
        }

        /// <summary>
        /// The set for an invoice the SYSTEM drafts with nobody at the form ("Create next
        /// invoice" from a contract): the defaults, and never an empty set — with every toggle off
        /// it falls back to manual transfer when the bank details are complete, otherwise online
        /// bank payment. The admin reviews the draft before it is sent either way.
        /// </summary>
        public static InvoicePaymentMethods DefaultsForGeneratedInvoice(BillingSettings settings)
        {
            var set = DefaultsFrom(settings);
            if (set != InvoicePaymentMethods.None) return set;

            return BillingSettingsService.IsManualAchComplete(settings)
                ? InvoicePaymentMethods.ManualBankTransfer
                : InvoicePaymentMethods.OnlineBank;
        }

        /// <summary>
        /// Why <paramref name="requested"/> cannot be saved, or null when it can.
        ///
        /// <paramref name="current"/> is the set the invoice already holds (null on create).
        /// Manual transfer that is ALREADY offered is never refused for incomplete bank details —
        /// an admin fixing a PO number must not be blocked because somebody else cleared the bank
        /// name in settings; the public page simply hides the incomplete block, as it always has.
        /// Only ADDING it needs the details to be there.
        /// </summary>
        public static string? DescribeProblem(
            InvoicePaymentMethods requested, InvoicePaymentMethods? current, BillingSettings settings)
        {
            if ((requested & ~All) != 0)
                return "The invoice names a payment method that does not exist.";

            if (requested == InvoicePaymentMethods.None)
                return "Choose at least one payment method for this invoice.";

            var addsManual = Offers(requested, InvoicePaymentMethods.ManualBankTransfer)
                             && !(current.HasValue && Offers(current.Value, InvoicePaymentMethods.ManualBankTransfer));

            if (addsManual && !BillingSettingsService.IsManualAchComplete(settings))
                return "Manual bank transfer needs complete bank details in Commercial → Billing Settings ("
                       + string.Join(", ", BillingSettingsService.MissingManualAchFields(settings))
                       + " missing). Fill them in first, or choose another payment method.";

            return null;
        }

        /// <summary>The name an admin reads on the form, the detail page and the list.</summary>
        public static string Label(InvoicePaymentMethods method) => method switch
        {
            InvoicePaymentMethods.OnlineBank => "Online bank payment (ACH)",
            InvoicePaymentMethods.Card => "Card",
            InvoicePaymentMethods.ManualBankTransfer => "Manual bank transfer",
            InvoicePaymentMethods.Other => "Other (check or arranged with us)",
            _ => method.ToString()
        };

        /// <summary>"Online bank payment (ACH), Card" — every method in the set, in display order.</summary>
        public static string Describe(InvoicePaymentMethods set)
        {
            var labels = Ordered.Where(m => Offers(set, m)).Select(Label).ToList();
            return labels.Count == 0 ? "None" : string.Join(", ", labels);
        }

        /// <summary>The individual methods in a set, in display order.</summary>
        public static List<InvoicePaymentMethods> Split(InvoicePaymentMethods set) =>
            Ordered.Where(m => Offers(set, m)).ToList();

        /// <summary>
        /// Which online method a Stripe Checkout for <paramref name="recordMethod"/> needs the
        /// invoice to offer. Null for anything Checkout cannot open, which the endpoint refuses.
        /// </summary>
        public static InvoicePaymentMethods? OnlineMethodFor(InvoicePaymentRecordMethod recordMethod) =>
            recordMethod switch
            {
                InvoicePaymentRecordMethod.AchBankTransfer => InvoicePaymentMethods.OnlineBank,
                InvoicePaymentRecordMethod.Card => InvoicePaymentMethods.Card,
                _ => null
            };
    }
}

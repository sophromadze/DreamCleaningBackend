using DreamCleaningBackend.Models.Commercial;

namespace DreamCleaningBackend.Helpers.Commercial;

/// <summary>Cash-basis invoice receipts, using the issued invoice's saved tax snapshot.</summary>
public static class InvoiceReceiptReporting
{
    public record Receipt(DateTime Date, decimal Gross, decimal Tax, decimal ProcessingFee)
    {
        public decimal NetServiceRevenue => Gross - Tax;
    }

    public static List<Receipt> Split(decimal invoiceTotal, decimal savedTax,
        IEnumerable<CommercialInvoicePayment> payments)
    {
        var result = new List<Receipt>();
        decimal cumulative = 0, previousTax = 0;
        foreach (var payment in payments.OrderBy(p => p.PaymentDate).ThenBy(p => p.Id))
        {
            cumulative += payment.Amount;
            // Cumulative rounding guarantees partial receipts and reversals reconcile to the
            // exact saved TaxAmount when fully settled; an overpayment creates no extra tax.
            var taxToDate = invoiceTotal <= 0 ? 0 : decimal.Round(
                Math.Clamp(cumulative / invoiceTotal, 0, 1) * savedTax, 2, MidpointRounding.AwayFromZero);
            result.Add(new Receipt(payment.PaymentDate.Date, payment.Amount,
                taxToDate - previousTax, payment.ProcessingFee));
            previousTax = taxToDate;
        }
        return result;
    }
}

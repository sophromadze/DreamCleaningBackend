namespace DreamCleaningBackend.DTOs
{
    /// <summary>Company → Order Sources: completed orders by first-touch source.</summary>
    public class OrderSourcesReportDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        /// <summary>day, week or month - the bucket size of <see cref="Periods"/>.</summary>
        public string Granularity { get; set; } = "day";
        public int TotalOrders { get; set; }
        public decimal TotalRevenue { get; set; }
        /// <summary>Every source, in display order, zero rows included.</summary>
        public List<OrderSourceRowDto> Sources { get; set; } = new();
        /// <summary>Every period of the window, empty ones included, oldest first.</summary>
        public List<OrderSourcePeriodDto> Periods { get; set; } = new();
    }

    public class OrderSourceRowDto
    {
        /// <summary>Stable key (see OrderSourceClassifier): paid, organic, direct, referral,
        /// social, email, ai, admin, unknown.</summary>
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public int Orders { get; set; }
        public decimal Revenue { get; set; }
        /// <summary>Percent of all orders in the window, one decimal.</summary>
        public decimal OrderShare { get; set; }
        /// <summary>Percent of all revenue in the window, one decimal.</summary>
        public decimal RevenueShare { get; set; }
        public decimal AverageOrderValue { get; set; }
    }

    public class OrderSourcePeriodDto
    {
        public DateTime PeriodStart { get; set; }
        /// <summary>Orders per source key.</summary>
        public Dictionary<string, int> Orders { get; set; } = new();
        /// <summary>Revenue per source key.</summary>
        public Dictionary<string, decimal> Revenue { get; set; } = new();
    }
}

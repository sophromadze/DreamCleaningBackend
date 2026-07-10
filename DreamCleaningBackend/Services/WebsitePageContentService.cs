using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Fetches and caches the text content of specific website pages so the chat
    /// agent's policy/inclusion answers self-update with the site — no hardcoded
    /// text, no backend redeploy. Backs the get_page_content tool.
    ///
    /// Configuration:
    ///  - WebsiteContent:BaseUrl   (default https://dreamcleaningnyc.com). On the VPS
    ///    set this to http://localhost:4000 (the PM2 SSR server) — fetching the public
    ///    domain from inside the box hits the known Cloudflare loopback issue
    ///    (CLAUDE.md quirk #1), and localhost serves the same prerendered HTML.
    ///  - WebsiteContent:CacheHours (default 12) — content freshness window.
    ///
    /// Cache is a never-evicting in-memory dictionary: a stale entry triggers a
    /// refetch, but if the fetch fails the last known content is served (with a
    /// warning logged) instead of failing the tool call.
    ///
    /// The pricing_and_discounts topic is a HYBRID: the fetched page provides the
    /// program descriptions, but the actual discount figures (first-time %, recurring
    /// plan %s, seasonal specials) are composed fresh from the DB on every call —
    /// the page loads those numbers browser-side only, so they never appear in
    /// server-fetched HTML, and the DB is the source of truth the page reads anyway.
    /// </summary>
    public interface IWebsitePageContentService
    {
        IReadOnlyCollection<string> Topics { get; }

        /// <summary>Cached (or freshly fetched) extracted page text; null for an
        /// unknown topic or when no content has ever been retrievable.</summary>
        Task<string?> GetContentAsync(string topic, CancellationToken cancellationToken = default);

        /// <summary>Refreshes every topic (used by the pre-warming hosted service).</summary>
        Task RefreshAllAsync(CancellationToken cancellationToken = default);
    }

    public class WebsitePageContentService : IWebsitePageContentService
    {
        public const string PricingTopic = "pricing_and_discounts";

        /// <summary>
        /// Dynamic registry: topic key → site-relative path. The base URL comes from
        /// config so the same registry works against the public domain (dev) or
        /// localhost SSR (prod). Adding a new service page is ONE line here — the tool
        /// schema doesn't hardcode an enum; it validates against these keys at runtime.
        ///
        /// Service-page keys are the URL slug with dashes → underscores (e.g.
        /// /services/filthy-cleaning → filthy_cleaning) so the model can infer the key
        /// from a service name. Borough/location pages (brooklyn/manhattan/queens) are
        /// deliberately EXCLUDED — they're location landings, not services.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> TopicPaths = new Dictionary<string, string>
        {
            // Residential family (residential_cleaning is priceable via the catalog;
            // house/condo/airbnb are residential-style marketing/SEO variants).
            ["residential_cleaning"] = "/services/residential-cleaning",
            ["house_cleaning"] = "/services/house-cleaning",
            ["condo_cleaning"] = "/services/condo-cleaning",
            ["airbnb_cleaning"] = "/services/airbnb-cleaning",
            ["deep_cleaning"] = "/services/deep-cleaning",
            ["move_in_out_cleaning"] = "/services/move-in-out-cleaning",
            ["kitchen_cleaning"] = "/services/residential-cleaning/kitchen",
            ["bathroom_cleaning"] = "/services/residential-cleaning/bathroom",
            // Commercial
            ["office_cleaning"] = "/services/office-cleaning",
            ["post_construction_cleaning"] = "/services/post-construction-cleaning",
            // Specialized / custom-quote (mostly NOT in get_service_catalog — the pages
            // themselves carry the rate structure, e.g. filthy_cleaning = $100/hr/cleaner).
            ["custom_cleaning"] = "/services/custom-cleaning",
            ["heavy_condition_cleaning"] = "/services/heavy-condition-cleaning",
            ["filthy_cleaning"] = "/services/filthy-cleaning",
            ["post_renovation_cleaning"] = "/services/post-renovation-cleaning",
            ["laundry_and_dishwashing"] = "/services/laundry-and-dishwashing",
            // Non-service topics
            // Room-by-room Standard vs Deep ✓/✗ tables + the Move-In/Out checklist with
            // requirements/exclusions. Requires the frontend build where all four room
            // tabs render into the DOM (CSS-hidden) — older builds only prerender Kitchen.
            ["cleaning_checklist"] = "/cleaning-checklist",
            // Program descriptions from the page; live figures appended from the DB.
            [PricingTopic] = "/pricing-and-discounts",
            // NOT /booking — that route is RenderMode.Client (empty shell server-side).
            // The full cancellation policy lives on the prerendered terms page.
            ["cancellation_policy"] = "/terms-and-conditions"
        };

        /// <summary>Back-compat: the three original topic keys, mapped to their new
        /// slug-based canonical keys so any older prompt reference or in-flight call
        /// still resolves.</summary>
        private static readonly IReadOnlyDictionary<string, string> TopicAliases = new Dictionary<string, string>
        {
            ["regular_cleaning_overview"] = "residential_cleaning",
            ["deep_cleaning_overview"] = "deep_cleaning",
            ["move_in_out_overview"] = "move_in_out_cleaning"
        };

        private static string ResolveTopic(string topic) =>
            TopicAliases.TryGetValue(topic, out var canonical) ? canonical : topic;

        /// <summary>Max characters returned per topic. When a page exceeds this,
        /// sections whose headings match these keywords are kept first. Sized so the
        /// cleaning-checklist page fits WHOLE (all four room tables + move-in/out +
        /// extras): the prompt's anti-inference rule treats absence from the checklist
        /// as "not included", so trimming sections would create false negatives.</summary>
        private const int MaxContentChars = 10000;
        private static readonly Regex PriorityHeadingRegex = new(
            @"includ|cancel|resched|policy|what.?s|fee|suppl|bring|guarantee|requirement|kitchen|bathroom|living|bedroom|discount|price|pricing|reward|referral",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Page chrome and content we never want: navigation, scripts, reviews/testimonials.
        // .mobile-comparison duplicates the checklist tables in a mobile layout — strip it.
        private const string StripSelectors =
            "script, style, noscript, svg, iframe, nav, footer, header, form, button, " +
            "app-header, app-footer, app-live-chat, .mobile-comparison, " +
            "[class*='review'], [class*='testimonial'], [id*='review'], [id*='testimonial']";

        // Elements the text walk visits, in document order. tr enables the checklist
        // ✓/✗ tables (their cells carry sr-only "Included"/"Not included" text);
        // .price-card__price captures the flat/hourly starting prices, which the
        // pricing page renders in divs rather than paragraphs.
        private const string ContentSelectors = "h1, h2, h3, h4, h5, h6, p, li, tr, div.price-card__price";

        private sealed record CacheEntry(string Content, DateTime FetchedAtUtc);

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly TimeSpan _freshFor;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<WebsitePageContentService> _logger;

        public IReadOnlyCollection<string> Topics => TopicPaths.Keys.ToList();

        public WebsitePageContentService(
            IConfiguration config,
            IServiceScopeFactory scopeFactory,
            ILogger<WebsitePageContentService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _baseUrl = (config["WebsiteContent:BaseUrl"] ?? "https://dreamcleaningnyc.com").TrimEnd('/');
            _freshFor = TimeSpan.FromHours(config.GetValue<double>("WebsiteContent:CacheHours", 12));

            // Force IPv4 — IPv6 is disabled on the production VPS (CLAUDE.md quirk #2).
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new System.Net.Sockets.Socket(
                        System.Net.Sockets.AddressFamily.InterNetwork,
                        System.Net.Sockets.SocketType.Stream,
                        System.Net.Sockets.ProtocolType.Tcp);
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DreamCleaning-ChatAgent/1.0");
        }

        public async Task<string?> GetContentAsync(string topic, CancellationToken cancellationToken = default)
        {
            topic = ResolveTopic(topic);
            if (!TopicPaths.ContainsKey(topic))
                return null;

            var pageText = await GetPageTextAsync(topic, cancellationToken);

            // Hybrid topic: the discount FIGURES live in the DB (the page only loads
            // them client-side) — compose them fresh on every call so they're always
            // current, regardless of the page-cache age.
            if (topic == PricingTopic)
            {
                var liveFigures = await BuildLiveDiscountFiguresAsync(cancellationToken);
                if (pageText == null && liveFigures.Length == 0)
                    return null;
                return ((pageText ?? string.Empty) + "\n\n" + liveFigures).Trim();
            }

            return pageText;
        }

        public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
        {
            foreach (var topic in TopicPaths.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await TryFetchTopicAsync(topic, cancellationToken);
            }
        }

        // ===== Page fetch + cache =====

        private async Task<string?> GetPageTextAsync(string topic, CancellationToken cancellationToken)
        {
            if (_cache.TryGetValue(topic, out var entry) && DateTime.UtcNow - entry.FetchedAtUtc < _freshFor)
                return entry.Content;

            var fresh = await TryFetchTopicAsync(topic, cancellationToken);
            if (fresh != null)
                return fresh;

            // Fetch failed — serve the last known content (even if stale) over failing.
            if (_cache.TryGetValue(topic, out entry))
            {
                _logger.LogWarning("WebsiteContent: serving stale content for '{Topic}' (fetched {FetchedAt:u}) after a failed refresh.",
                    topic, entry.FetchedAtUtc);
                return entry.Content;
            }
            return null;
        }

        private async Task<string?> TryFetchTopicAsync(string topic, CancellationToken cancellationToken)
        {
            var url = _baseUrl + TopicPaths[topic];
            try
            {
                var html = await _http.GetStringAsync(url, cancellationToken);
                var content = ExtractMainContent(html);
                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogWarning("WebsiteContent: extraction produced no text for '{Topic}' ({Url})", topic, url);
                    return null;
                }
                _cache[topic] = new CacheEntry(content, DateTime.UtcNow);
                _logger.LogInformation("WebsiteContent: refreshed '{Topic}' ({Length} chars)", topic, content.Length);
                return content;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "WebsiteContent: failed to fetch '{Topic}' from {Url}", topic, url);
                return null;
            }
        }

        // ===== Live discount figures (pricing_and_discounts hybrid) =====

        /// <summary>Current first-time / recurring / seasonal discount figures straight
        /// from the DB — the same tables the pricing page reads client-side. Returns
        /// empty string on failure (the topic then degrades to page text only).</summary>
        private async Task<string> BuildLiveDiscountFiguresAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var now = DateTime.UtcNow;

                var offers = await db.SpecialOffers
                    .AsNoTracking()
                    .Where(o => o.IsActive
                        && (o.ValidFrom == null || o.ValidFrom <= now)
                        && (o.ValidTo == null || o.ValidTo >= now))
                    .OrderBy(o => o.DisplayOrder)
                    .ToListAsync(cancellationToken);

                // Same first-time detection the pricing page uses.
                var firstTime = offers.FirstOrDefault(o =>
                    o.RequiresFirstTimeCustomer ||
                    o.Type == OfferType.FirstTime ||
                    (o.Name != null && (o.Name.Contains("first time", StringComparison.OrdinalIgnoreCase) ||
                                        o.Name.Contains("first-time", StringComparison.OrdinalIgnoreCase))));
                var seasonal = offers
                    .Where(o => o != firstTime && !o.RequiresFirstTimeCustomer && o.Type != OfferType.FirstTime)
                    .ToList();

                var plans = await db.Subscriptions
                    .AsNoTracking()
                    .Where(s => s.IsActive && s.DiscountPercentage > 0)
                    .OrderBy(s => s.SubscriptionDays)
                    .ToListAsync(cancellationToken);

                var sb = new StringBuilder();
                sb.AppendLine("## CURRENT DISCOUNT FIGURES (live from our system right now — these exact numbers may be quoted)");

                sb.AppendLine(firstTime != null
                    ? $"- First-time customer discount: {OfferLabel(firstTime)} off the first cleaning (applied automatically at checkout)."
                    : "- First-time customer discount: none currently active.");

                if (plans.Count > 0)
                {
                    sb.AppendLine("- Recurring plan discounts (applied to every visit):");
                    foreach (var plan in plans)
                        sb.AppendLine($"  - {plan.Name}: {plan.DiscountPercentage:0.##}% off");
                }
                else
                {
                    sb.AppendLine("- Recurring plan discounts: none currently active.");
                }

                if (seasonal.Count > 0)
                {
                    sb.AppendLine("- Active seasonal/special offers:");
                    foreach (var offer in seasonal)
                    {
                        var until = offer.ValidTo != null ? $" (valid until {offer.ValidTo:MMM dd, yyyy})" : string.Empty;
                        var min = offer.MinimumOrderAmount != null ? $", minimum order ${offer.MinimumOrderAmount:0.##}" : string.Empty;
                        sb.AppendLine($"  - {offer.Name}: {OfferLabel(offer)} off{until}{min}.");
                    }
                }
                else
                {
                    sb.AppendLine("- Seasonal/special offers: none currently active.");
                }

                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebsiteContent: failed to compose live discount figures from the DB");
                return string.Empty;
            }
        }

        private static string OfferLabel(SpecialOffer offer) =>
            offer.IsPercentage ? $"{offer.DiscountValue:0.##}%" : $"${offer.DiscountValue:0.##}";

        // ===== Extraction =====

        /// <summary>Headings, paragraphs, list items, table rows and price figures from
        /// the main content area, with chrome and review sections stripped. Table rows
        /// render as "Task — Standard Cleaning: Included; Deep Cleaning: NOT included"
        /// (cell text comes from the table's sr-only accessibility spans). Over-budget
        /// pages keep the sections whose headings look policy/inclusion-related first.</summary>
        private static string ExtractMainContent(string html)
        {
            var parser = new HtmlParser();
            var document = parser.ParseDocument(html);

            foreach (var element in document.QuerySelectorAll(StripSelectors).ToList())
                element.Remove();

            var container = (IElement?)document.QuerySelector("main") ?? document.Body;
            if (container == null)
                return string.Empty;

            // Walk content nodes in document order, grouped into sections at each
            // heading so long pages can be trimmed per-section.
            var sections = new List<(string Heading, StringBuilder Text)> { (string.Empty, new StringBuilder()) };
            var tableColumns = new List<string>();

            foreach (var node in container.QuerySelectorAll(ContentSelectors))
            {
                switch (node.NodeName)
                {
                    case "H1" or "H2" or "H3" or "H4" or "H5" or "H6":
                        var heading = NormalizeWhitespace(node.TextContent);
                        if (heading.Length == 0) continue;
                        sections.Add((heading, new StringBuilder()));
                        // Explicit '\n' (not AppendLine): Environment.NewLine is "\r\n" on
                        // Windows, which inflated dev extraction lengths past the cap while
                        // Linux prod stayed under — keep both platforms byte-identical.
                        sections[^1].Text.Append("## ").Append(heading).Append('\n');
                        break;

                    case "TR":
                        AppendTableRow(node, tableColumns, sections[^1].Text);
                        break;

                    default: // P, LI, price divs
                        // Skip nodes nested inside an LI — the LI's own line already
                        // includes their text (avoids duplicated content).
                        if (HasAncestor(node, "LI")) continue;
                        var text = NormalizeWhitespace(node.TextContent);
                        if (text.Length == 0) continue;
                        sections[^1].Text.Append(node.NodeName == "LI" ? "- " + text : text).Append('\n');
                        break;
                }
            }

            var rendered = sections
                .Select(s => (s.Heading, Text: s.Text.ToString().Trim()))
                .Where(s => s.Text.Length > 0)
                .ToList();

            var total = rendered.Sum(s => s.Text.Length);
            IEnumerable<string> chosen;
            if (total <= MaxContentChars)
            {
                chosen = rendered.Select(s => s.Text);
            }
            else
            {
                // Priority sections first (in page order), then the rest until the budget runs out.
                var picked = new List<(string Heading, string Text)>();
                var budget = MaxContentChars;
                foreach (var section in rendered.Where(s => PriorityHeadingRegex.IsMatch(s.Heading))
                         .Concat(rendered.Where(s => !PriorityHeadingRegex.IsMatch(s.Heading))))
                {
                    if (budget - section.Text.Length < 0 && picked.Count > 0)
                        continue;
                    picked.Add(section);
                    budget -= section.Text.Length;
                    if (budget <= 0)
                        break;
                }
                // Restore document order for readability.
                chosen = rendered.Where(picked.Contains).Select(s => s.Text);
            }

            var result = string.Join("\n\n", chosen);
            return result.Length <= MaxContentChars ? result : result[..MaxContentChars];
        }

        /// <summary>Header rows (th) set the column names for the current table; data
        /// rows (td) render one line: label — Col1: value; Col2: value. "Not included"
        /// is upcased to "NOT included" so exclusions stand out to the model.</summary>
        private static void AppendTableRow(IElement row, List<string> tableColumns, StringBuilder target)
        {
            var headers = row.QuerySelectorAll("th").Select(c => NormalizeWhitespace(c.TextContent)).ToList();
            if (headers.Count > 1)
            {
                tableColumns.Clear();
                tableColumns.AddRange(headers);
                target.Append("### ").Append(headers[0]).Append('\n'); // e.g. the room name
                return;
            }

            var cells = row.QuerySelectorAll("td").Select(c => NormalizeWhitespace(c.TextContent)).ToList();
            if (cells.Count < 2 || cells[0].Length == 0)
                return;

            var parts = new List<string>();
            for (var i = 1; i < cells.Count; i++)
            {
                var column = i < tableColumns.Count ? tableColumns[i] : $"Column {i}";
                var value = cells[i].Replace("Not included", "NOT included", StringComparison.OrdinalIgnoreCase);
                parts.Add($"{column}: {value}");
            }
            target.Append("- ").Append(cells[0]).Append(" — ").Append(string.Join("; ", parts)).Append('\n');
        }

        private static bool HasAncestor(IElement node, string nodeName)
        {
            for (var parent = node.ParentElement; parent != null; parent = parent.ParentElement)
                if (parent.NodeName == nodeName)
                    return true;
            return false;
        }

        private static string NormalizeWhitespace(string text) =>
            Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Pre-warms all website-content topics at startup and refreshes them every
    /// WebsiteContent:CacheHours, so live chat tool calls are near-always cache hits.
    /// </summary>
    public class WebsiteContentRefreshService : BackgroundService
    {
        private readonly IWebsitePageContentService _contentService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<WebsiteContentRefreshService> _logger;

        public WebsiteContentRefreshService(
            IWebsitePageContentService contentService,
            IConfiguration configuration,
            ILogger<WebsiteContentRefreshService> logger)
        {
            _contentService = contentService;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Short delay so startup (incl. migrations) settles first.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

            var interval = TimeSpan.FromHours(_configuration.GetValue<double>("WebsiteContent:CacheHours", 12));

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _contentService.RefreshAllAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Per-topic failures are already handled inside RefreshAllAsync;
                    // this guards the loop itself.
                    _logger.LogError(ex, "WebsiteContentRefreshService: refresh cycle failed");
                }

                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}

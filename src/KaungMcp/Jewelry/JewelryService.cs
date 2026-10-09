using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KaungMcp.Data;
using KaungMcp.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace KaungMcp.Jewelry;

public sealed class JewelryService(
    IDbContextFactory<KaungMcpDbContext> database,
    IJewelryAdapter adapter,
    IOptions<JewelryOptions> options,
    TimeProvider clock,
    ILogger<JewelryService> logger,
    IServiceProvider? serviceProvider = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _initialization = new(1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sourceLocks = new();
    private bool _initialized;
    private JewelryOptions Settings => options.Value;
    public bool Enabled(JewelrySourceDefinition source) => Settings.Enabled && source.AdapterAvailable && source.Verification == "verified_sample" &&
        (!Settings.Sources.TryGetValue(source.Id, out var enabled) || enabled);
    public int Interval(JewelrySourceDefinition source) => source.MinimumIntervalMinutes;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _initialization.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            await using var db = await database.CreateDbContextAsync(cancellationToken);
            foreach (var source in JewelryCatalog.Sources)
            {
                var entity = await db.JewelrySources.FindAsync([source.Id], cancellationToken);
                if (entity is null)
                {
                    entity = new JewelrySourceEntity { Id = source.Id };
                    db.JewelrySources.Add(entity);
                    db.JewelryCollectionStates.Add(new JewelryCollectionState { SourceId = source.Id });
                }
                entity.DefinitionJson = JsonSerializer.Serialize(source, Json);
                entity.Enabled = Enabled(source);
            }
            await db.SaveChangesAsync(cancellationToken);
            _initialized = true;
        }
        finally { _initialization.Release(); }
    }

    public async Task CollectDueAsync(IEnumerable<string>? sourceIds, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var selected = sourceIds?.ToHashSet(StringComparer.Ordinal);
        await Task.WhenAll(JewelryCatalog.Sources.Where(s => Enabled(s) && (selected is null || selected.Contains(s.Id)))
            .Select(s => CollectAsync(s, false, false, cancellationToken)));
    }

    public async Task<JewelryCollectionResult[]> CollectScheduledAsync(IEnumerable<string> sourceIds, bool force,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var ids = sourceIds.ToArray();
        return await Task.WhenAll(ids.Select(async id =>
        {
            var source = JewelryCatalog.Sources.SingleOrDefault(s => s.Id == id);
            return source is null || !Enabled(source)
                ? new JewelryCollectionResult(id, "skipped", 0, "Source is disabled or not verified.")
                : await CollectAsync(source, true, force, cancellationToken);
        }));
    }

    private async Task<JewelryCollectionResult> CollectAsync(JewelrySourceDefinition source,
        bool scheduled, bool force, CancellationToken cancellationToken)
    {
        var gate = _sourceLocks.GetOrAdd(source.Id, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await database.CreateDbContextAsync(cancellationToken);
            var state = await db.JewelryCollectionStates.SingleAsync(s => s.SourceId == source.Id, cancellationToken);
            var now = clock.GetUtcNow();
            if (!force && state.NextAttemptTicks > now.UtcTicks &&
                (!scheduled || state.ConsecutiveFailures > 0))
                return new JewelryCollectionResult(source.Id, "skipped", 0, "Source retry backoff is active.");
            var attempt = new JewelryCollectionAttempt { SourceId = source.Id, StartedTicks = now.UtcTicks };
            IReadOnlyList<JewelryQuote> quotes = [];
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(Settings.TimeoutSeconds));
                quotes = await adapter.CollectAsync(source, deadline.Token);
                ValidateBatch(source.Id, quotes, now);
                state.Status = quotes.Count == 0 ? "no_data" : "ok";
                state.LastError = quotes.Count == 0 ? "Source returned no usable quotes; no zero or replacement price stored." : null;
                state.ConsecutiveFailures = 0;
                if (quotes.Count > 0) state.LastSuccessTicks = now.UtcTicks;
                attempt.Success = quotes.Count > 0;
                attempt.QuoteCount = quotes.Count;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // External data errors must not take down other sources or remove previously stored quotes.
                quotes = [];
                state.Status = "error";
                state.ConsecutiveFailures = Math.Min(20, state.ConsecutiveFailures + 1);
                state.LastError = exception is OperationCanceledException ? "Source collection timed out." :
                    exception.Message[..Math.Min(exception.Message.Length, 400)];
                logger.LogWarning("Jewelry source {SourceId}: {Error}", source.Id, state.LastError);
            }
            var completed = clock.GetUtcNow();
            state.LastAttemptTicks = now.UtcTicks;
            state.NextAttemptTicks = scheduled && state.ConsecutiveFailures == 0 ? null :
                completed.AddMinutes(Math.Min(1440,
                    Interval(source) * Math.Pow(2, Math.Min(state.ConsecutiveFailures, 6)))).UtcTicks;
            attempt.CompletedTicks = completed.UtcTicks;
            attempt.Error = state.LastError;
            db.JewelryCollectionAttempts.Add(attempt);
            foreach (var raw in quotes)
            {
                var quote = raw with { Id = Guid.NewGuid(), CollectedAt = completed };
                db.JewelryQuotes.Add(new JewelryQuoteEntity
                {
                    Id = quote.Id, SourceId = source.Id, InstrumentId = quote.InstrumentId,
                    SeriesKey = quote.SeriesKey, SourceDate = quote.SourceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    CollectedTicks = completed.UtcTicks, PayloadJson = JsonSerializer.Serialize(quote, Json)
                });
            }
            await db.SaveChangesAsync(cancellationToken);
            return new JewelryCollectionResult(source.Id,
                attempt.Success ? "succeeded" : state.Status == "no_data" ? "failed" : "failed",
                attempt.QuoteCount, state.LastError);
        }
        finally { gate.Release(); }
    }

    public static void ValidateBatch(string sourceId, IReadOnlyList<JewelryQuote> quotes, DateTimeOffset now)
    {
        if (quotes.Count > 1000) throw new FormatException("Unexpected source batch size.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var quote in quotes)
        {
            var instrument = JewelryCatalog.Instruments.SingleOrDefault(i => i.Id == quote.InstrumentId && i.SourceId == sourceId);
            if (instrument is null || quote.SourceId != sourceId || instrument.Kind != quote.Kind ||
                instrument.Region != quote.Region || !instrument.Categories.Contains(quote.Category))
                throw new FormatException("Quote identity does not match the source catalog.");
            if (quote.Price <= 0 || quote.Currency is not ("CNY" or "USD") || quote.Unit is not ("gram" or "troy_ounce"))
                throw new FormatException("Invalid quote amount, currency or unit.");
            if (quote.Kind == "futures_family" || quote.Kind == "futures" &&
                (quote.ContractMonth is null || !Regex.IsMatch(quote.ContractMonth, @"^\d{4}-(0[1-9]|1[0-2])$")))
                throw new FormatException("Futures quotes require a concrete delivery month, never a continuous/family identifier.");
            var today = DateOnly.FromDateTime(quote.Region == "CN" ? now.ToOffset(TimeSpan.FromHours(8)).DateTime : now.UtcDateTime);
            if (quote.SourceDate > today || quote.SourceTimestamp > now.AddMinutes(5))
                throw new FormatException("Source returned a future date/timestamp.");
            if (!keys.Add(quote.SeriesKey + "|" + quote.SourceDate))
                throw new FormatException("Duplicate price series for the same source date.");
        }
    }

    public async Task<JewelryQuoteResponse> GetQuotesAsync(bool brands, string[]? ids, string date,
        string? category, string region, CancellationToken cancellationToken)
    {
        var day = ParseRequestedDate(date);
        if (region != "CN" && brands) throw new McpException("Only mainland China region CN is supported for brands.");
        if (category is not null && category is not ("jewelry" or "bar" or "recycling"))
            throw new McpException("category must be jewelry, bar or recycling.");
        var instruments = SelectInstruments(brands, ids);
        await InitializeAsync(cancellationToken);
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        var selectedSources = instruments.Select(i => i.SourceId).Distinct().ToArray();
        // The persisted next-attempt time is the cache TTL. Fresh caches do not access the network;
        // expired caches receive a bounded refresh, and failed refreshes preserve prior observations.
        if (serviceProvider?.GetService(typeof(SchedulerService)) is SchedulerService scheduler)
            await scheduler.CollectOnQueryAsync(selectedSources, cancellationToken);
        else await CollectDueAsync(selectedSources, cancellationToken);
        var schedules = serviceProvider?.GetService(typeof(SchedulerService)) is SchedulerService scheduleService
            ? await scheduleService.JewelrySchedulesAsync(cancellationToken)
            : new Dictionary<string, ScheduledTask>(StringComparer.Ordinal);
        var instrumentIds = instruments.Select(i => i.Id).ToArray();
        var query = db.JewelryQuotes.AsNoTracking().Where(q => instrumentIds.Contains(q.InstrumentId));
        var latest = await Latest(query, db).ToListAsync(cancellationToken);
        var target = day is null ? latest : await Latest(query.Where(q => q.SourceDate == day), db, day).ToListAsync(cancellationToken);
        var states = await db.JewelryCollectionStates.AsNoTracking().ToDictionaryAsync(s => s.SourceId, cancellationToken);
        JewelryQuoteView[] Views(IEnumerable<JewelryQuoteEntity> entities, string id) => entities
            .Where(e => e.InstrumentId == id).Select(Decode).Where(q => category is null || q.Category == category)
            .OrderBy(q => q.Category).ThenBy(q => q.PriceType)
            .Select(q => View(q, schedules.GetValueOrDefault(q.SourceId))).ToArray();
        var items = instruments.Select(i =>
        {
            var values = Views(target, i.Id);
            var recent = day is null ? [] : Views(latest, i.Id);
            var source = JewelryCatalog.Sources.Single(s => s.Id == i.SourceId);
            var state = states[i.SourceId];
            var unscheduled = Enabled(source) && !schedules.ContainsKey(i.SourceId);
            var cachedFallback = values.Length > 0 && (state.Status is "error" or "no_data" || !Enabled(source) || unscheduled);
            var status = cachedFallback ? "cached_fallback" : values.Length > 0 ? "available" : !Enabled(source) ? "unavailable" : day is not null ? "date_missing" : "no_data";
            var reason = cachedFallback ? "Returning stored observations; " +
                (!Enabled(source) ? "source is disabled. " + source.Evidence : unscheduled ? "scheduled collection is disabled." : state.LastError) : values.Length > 0 ? null : !Enabled(source) ?
                (source.AdapterAvailable && source.Verification == "verified_sample" ? "Source disabled by Jewelry configuration. " : "") + source.Evidence : state.LastError ??
                (day is not null ? "No observed quote with this source date/category. LatestAvailable is not a substitute." : "No observed quote for this instrument/category.");
            return new JewelryQuoteItem(i.Id, status, values, recent, reason);
        }).ToArray();
        return new JewelryQuoteResponse(day ?? "latest", items);
    }

    private static IQueryable<JewelryQuoteEntity> Latest(IQueryable<JewelryQuoteEntity> query,
        KaungMcpDbContext db, string? date = null) => query.Where(q => !db.JewelryQuotes.Any(n =>
            n.SeriesKey == q.SeriesKey && (date == null || n.SourceDate == date) &&
            (n.SourceDate != null && q.SourceDate == null ||
             n.SourceDate != null && q.SourceDate != null && string.Compare(n.SourceDate, q.SourceDate) > 0 ||
             n.SourceDate == q.SourceDate && n.CollectedTicks > q.CollectedTicks)));

    public async Task<JewelryHistoryResponse> HistoryAsync(string instrumentId, string startDate, string endDate,
        string dateBasis, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!JewelryCatalog.Instruments.Any(i => i.Id == instrumentId)) throw new McpException("Unknown instrumentId. Use list_instruments.");
        var start = ParseDate(startDate); var end = ParseDate(endDate);
        if (start > end || end.DayNumber - start.DayNumber > 366 || offset < 0 || limit is < 1 or > 500)
            throw new McpException("History requires start <= end, at most 367 days, offset >= 0 and limit 1..500.");
        if (dateBasis is not ("source_date" or "collected_date")) throw new McpException("dateBasis must be source_date or collected_date (UTC).");
        await InitializeAsync(cancellationToken);
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        var query = db.JewelryQuotes.AsNoTracking().Where(q => q.InstrumentId == instrumentId);
        if (dateBasis == "source_date") query = query.Where(q => q.SourceDate != null &&
            string.Compare(q.SourceDate, startDate) >= 0 && string.Compare(q.SourceDate, endDate) <= 0);
        else
        {
            var lower = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).UtcTicks;
            var upper = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).UtcTicks;
            query = query.Where(q => q.CollectedTicks >= lower && q.CollectedTicks < upper);
        }
        var rows = await query.OrderBy(q => q.CollectedTicks).ThenBy(q => q.Id).Skip(offset).Take(limit + 1).ToListAsync(cancellationToken);
        return new JewelryHistoryResponse(instrumentId, startDate, endDate, dateBasis,
            rows.Take(limit).Select(Decode).Select(q => View(q)).ToArray(), rows.Count > limit ? offset + limit : null,
            "Stored observations only, not a complete archive. Repeated snapshots are retained; no gap filling or synthetic OHLC. Source-date queries omit unknown source dates; collection dates use UTC.");
    }

    public async Task<JewelrySourceStatus[]> StatusAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var schedules = serviceProvider?.GetService(typeof(SchedulerService)) is SchedulerService scheduler
            ? await scheduler.JewelrySchedulesAsync(cancellationToken)
            : new Dictionary<string, ScheduledTask>(StringComparer.Ordinal);
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        var states = await db.JewelryCollectionStates.AsNoTracking().ToDictionaryAsync(s => s.SourceId, cancellationToken);
        var cutoff = clock.GetUtcNow().AddDays(-7).UtcTicks;
        var attempts = await db.JewelryCollectionAttempts.AsNoTracking().Where(a => a.StartedTicks >= cutoff).ToListAsync(cancellationToken);
        return JewelryCatalog.Sources.Select(source =>
        {
            var state = states[source.Id];
            var records = attempts.Where(a => a.SourceId == source.Id).ToArray();
            var planned = schedules.TryGetValue(source.Id, out var schedule) ? schedule.NextRunTicks : null;
            var next = state.NextAttemptTicks is null ? planned : planned is null ? state.NextAttemptTicks
                : Math.Max(state.NextAttemptTicks.Value, planned.Value);
            return new JewelrySourceStatus(source, Enabled(source), Enabled(source) ? state.Status : "disabled",
                Timestamp(state.LastAttemptTicks), Timestamp(state.LastSuccessTicks), Timestamp(next),
                state.ConsecutiveFailures, state.LastError, records.Length, records.Count(a => a.Success),
                records.Select(a => new DateTime(a.StartedTicks, DateTimeKind.Utc).Date).Distinct().Count() >= 7
                    ? "seven_calendar_dates_recorded_review_required" : "seven_day_observation_pending");
        }).ToArray();
    }

    public async Task<JewelryComparisonResponse> CompareAsync(Guid[] quoteIds, string mode, CancellationToken cancellationToken)
    {
        if (quoteIds.Length is < 2 or > 20 || quoteIds.Distinct().Count() != quoteIds.Length)
            throw new McpException("Provide 2..20 distinct quoteIds returned by quote/history tools.");
        if (mode is not ("rank" or "reference")) throw new McpException("mode must be rank or reference.");
        await InitializeAsync(cancellationToken);
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        var stored = await db.JewelryQuotes.AsNoTracking().Where(q => quoteIds.Contains(q.Id)).ToListAsync(cancellationToken);
        if (stored.Count != quoteIds.Length) throw new McpException("Some quote IDs do not exist.");
        var quotes = quoteIds.Select(id => Decode(stored.Single(q => q.Id == id))).ToArray();
        return Compare(quotes, mode);
    }

    public JewelryComparisonResponse Compare(JewelryQuote[] quotes, string mode)
    {
        if (quotes.Length is < 2 or > 20 || mode is not ("rank" or "reference"))
            throw new McpException("Provide 2..20 quotes and mode rank or reference.");
        var first = quotes[0];
        if (quotes.Any(q => q.Currency != first.Currency || q.Unit != first.Unit))
            throw new McpException("Comparison requires the same currency and unit. Use convert_price separately with explicit FX provenance.");
        if (first.SourceDate is null || quotes.Any(q => q.SourceDate != first.SourceDate))
            throw new McpException("Comparison requires known, matching source dates.");
        if (mode == "rank")
        {
            if (quotes.Any(q => q.Kind != "brand" || q.Category != first.Category || q.Region != first.Region ||
                q.Purity is null || q.Purity != first.Purity || q.PriceType != first.PriceType || q.ProductVariant != first.ProductVariant ||
                q.LaborFeeIncluded is null || q.LaborFeeIncluded != first.LaborFeeIncluded || q.Applicability != first.Applicability || q.DateBasis != first.DateBasis))
                throw new McpException("Ranking requires brand quotes with matching category, region, known purity, price type, known labor-fee basis and applicability. Use reference mode for a labelled numerical difference.");
            if (quotes.Select(q => q.InstrumentId).Distinct().Count() != quotes.Length)
                throw new McpException("Ranking requires one quote per brand.");
            quotes = quotes.OrderBy(q => q.Price).ToArray();
            first = quotes[0];
        }
        var warnings = new List<string>();
        if (quotes.Select(q => q.DateBasis).Distinct().Count() > 1) warnings.Add("Date bases differ (e.g. exchange trade date vs brand effective date); numerical reference only.");
        if (quotes.Any(q => View(q).DataState != "current")) warnings.Add("Some quotes are older, date-only, or stale. See quote timestamps; this is not a live ranking.");
        if (quotes.Select(q => q.SourceTimestamp).Distinct().Count() > 1) warnings.Add("Source timestamps are not synchronized; same date does not mean same moment.");
        return new JewelryComparisonResponse(mode, quotes.Select(q => new JewelryComparisonRow(q.Id, q.Price,
            q.Price - first.Price, decimal.Round((q.Price / first.Price - 1) * 100, 4, MidpointRounding.AwayFromZero))).ToArray(),
            warnings.ToArray(), mode == "rank" ? "Ascending comparable brand reference prices; differences from the lowest price, not transaction offers."
                : "Numerical differences from the first supplied quote only; no ranking, profit, retail margin or arbitrage interpretation.");
    }

    public JewelryQuoteView View(JewelryQuote quote, ScheduledTask? schedule = null)
    {
        var source = JewelryCatalog.Sources.Single(s => s.Id == quote.SourceId);
        var now = clock.GetUtcNow();
        var stale = schedule is null ? now - quote.CollectedAt > TimeSpan.FromMinutes(Interval(source) * 2)
            : now > SchedulerService.Next(schedule, quote.CollectedAt).AddMinutes(1);
        var today = DateOnly.FromDateTime(quote.Region == "CN" ? now.ToOffset(TimeSpan.FromHours(8)).DateTime : now.UtcDateTime);
        var sourceStale = quote.SourceTimestamp is not null && (schedule is null
            ? now - quote.SourceTimestamp > TimeSpan.FromMinutes(Interval(source) * 2)
            : now > SchedulerService.Next(schedule, quote.SourceTimestamp.Value).AddMinutes(1));
        var state = stale ? "stale_cache" : quote.SourceDate is null ? "unknown_time" : quote.SourceDate < today ? "prior_date" :
            quote.SourceTimestamp is null ? "date_only" : sourceStale ? "source_stale" : "current";
        return new JewelryQuoteView(quote, state, stale);
    }

    public static JewelryConversion Convert(decimal price, string fromCurrency, string fromUnit, string toCurrency,
        string toUnit, decimal? exchangeRate, string? exchangeRateSource, DateTimeOffset? exchangeRateTimestamp, int decimals)
    {
        if (price <= 0 || decimals is < 0 or > 8 || !Regex.IsMatch(fromCurrency, "^[A-Z]{3}$") || !Regex.IsMatch(toCurrency, "^[A-Z]{3}$"))
            throw new McpException("Positive price, uppercase three-letter currency codes and decimals 0..8 required.");
        decimal Grams(string unit) => unit switch { "gram" => 1m, "troy_ounce" => 31.1034768m, _ => throw new McpException("Unit must be gram or troy_ounce, not ordinary ounce.") };
        if (fromCurrency != toCurrency && (exchangeRate is null or <= 0 || string.IsNullOrWhiteSpace(exchangeRateSource) || exchangeRateTimestamp is null))
            throw new McpException("Cross-currency conversion requires exchangeRate, exchangeRateSource and an offset-bearing exchangeRateTimestamp. Rate is target currency per one source currency.");
        if (fromCurrency == toCurrency && exchangeRate is not null and not 1m)
            throw new McpException("Same-currency exchangeRate must be 1 or omitted.");
        var rate = exchangeRate ?? 1m;
        try
        {
            var result = decimal.Round(price / Grams(fromUnit) * Grams(toUnit) * rate, decimals, MidpointRounding.AwayFromZero);
            return new JewelryConversion(price, fromCurrency, fromUnit, result, toCurrency, toUnit, rate,
                exchangeRateSource, exchangeRateTimestamp,
                "price / gramsPerFromUnit * gramsPerToUnit * exchangeRate; 1 troy_ounce = 31.1034768 gram; FX = target/source; round AwayFromZero");
        }
        catch (OverflowException) { throw new McpException("Conversion exceeds decimal range."); }
    }

    private string? ParseRequestedDate(string date) => date == "latest" ? null : date == "today"
        ? DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : ParseDate(date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly ParseDate(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) && date.Year is >= 1900 and < 9999 ? date : throw new McpException("Date must be yyyy-MM-dd, year 1900..9998.");
    private static JewelryInstrument[] SelectInstruments(bool brands, string[]? ids)
    {
        var all = JewelryCatalog.Instruments.Where(i => (i.Kind == "brand") == brands).ToArray();
        if (ids is null || ids.Length == 0) return all;
        if (ids.Length > 20 || ids.Any(id => !all.Any(i => i.Id == id))) throw new McpException("Unknown or wrong-kind instrument ID; use list_instruments. Maximum 20 IDs.");
        return all.Where(i => ids.Contains(i.Id)).ToArray();
    }
    private static JewelryQuote Decode(JewelryQuoteEntity entity) => JsonSerializer.Deserialize<JewelryQuote>(entity.PayloadJson, Json)!;
    private static DateTimeOffset? Timestamp(long? ticks) => ticks is null ? null : new DateTimeOffset(ticks.Value, TimeSpan.Zero);
}

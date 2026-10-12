using Microsoft.EntityFrameworkCore;

namespace KaungMcp.Jewelry;

public sealed partial class JewelryService
{
    private readonly SemaphoreSlim brandCollection = new(1, 1);

    public async Task<JewelryCollectionResult[]> CollectBrandsAsync(bool force, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await brandCollection.WaitAsync(ct);
        try
        {
            var results = new List<JewelryCollectionResult>();
            var official = JewelryCatalog.Sources.Where(s => s.MinimumIntervalMinutes == 60 &&
                !JewelryBrands.IsFallback(s.Id) && Enabled(s)).ToArray();
            results.AddRange(await Task.WhenAll(official.Select(s => CollectAsync(s, true, force, ct, respectInterval: true))));
            var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).DateTime);
            var date = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            await using var db = await database.CreateDbContextAsync(ct);
            var existing = await db.JewelryQuotes.AsNoTracking().Where(q => q.SourceDate == date &&
                q.InstrumentId.StartsWith("BRAND:")).ToListAsync(ct);
            // Only official rows establish coverage before trying the ordered fallback chain.
            var covered = existing.Select(Decode).Where(q => !JewelryBrands.IsFallback(q.SourceId))
                .Select(q => Coverage(q)).ToHashSet(StringComparer.Ordinal);
            foreach (var id in JewelryBrands.FallbackSources)
            {
                var source = JewelryCatalog.Sources.Single(s => s.Id == id);
                if (!Enabled(source)) continue;
                if (JewelryBrands.All.All(b => new[] { "jewelry", "bar", "recycling" }
                    .All(c => covered.Contains(b.InstrumentId + "|" + c)))) break;
                // Previous adopted rows are reusable without another network request. Lower-priority
                // rows never preempt newly available higher-priority data.
                var previous = existing.Select(Decode).Where(q => q.SourceId == id).ToArray();
                var result = await CollectAsync(source, true, force, ct,
                    q => q.SourceDate == today && !covered.Contains(Coverage(q)), respectInterval: true);
                results.Add(result);
                var adopted = await db.JewelryQuotes.AsNoTracking().Where(q => q.SourceDate == date && q.SourceId == id).ToListAsync(ct);
                foreach (var quote in previous.Concat(adopted.Select(Decode))) covered.Add(Coverage(quote));
            }
            return results.ToArray();
        }
        finally { brandCollection.Release(); }
    }

    private static string Coverage(JewelryQuote quote) => quote.InstrumentId + "|" + quote.Category;
}

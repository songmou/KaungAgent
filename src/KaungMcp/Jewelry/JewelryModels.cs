namespace KaungMcp.Jewelry;

public sealed record JewelryInstrument(string Id, string Name, string Kind, string SourceId,
    string Region, string[] Categories, string? ContractMonth = null);

public sealed record JewelrySourceDefinition(string Id, string Name, string Url, string? Endpoint,
    string Verification, string Evidence, int MinimumIntervalMinutes, bool AdapterAvailable);

public sealed class JewelryOptions
{
    public bool Enabled { get; set; } = true;
    public bool BackgroundCollection { get; set; } = true;
    public int MarketIntervalMinutes { get; set; } = 15;
    public int BrandIntervalMinutes { get; set; } = 60;
    public int TimeoutSeconds { get; set; } = 20;
    public Dictionary<string, bool> Sources { get; set; } = new(StringComparer.Ordinal);
}

public sealed record JewelryQuote
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string InstrumentId { get; init; }
    public required string SourceId { get; init; }
    public required string SourceUrl { get; init; }
    public required string Kind { get; init; }
    public string Region { get; init; } = "CN";
    public string Category { get; init; } = "market";
    public string? ProductVariant { get; init; }
    public string? Purity { get; init; }
    public string? RawProductName { get; init; }
    public required string PriceType { get; init; }
    public required decimal Price { get; init; }
    public string Currency { get; init; } = "CNY";
    public string Unit { get; init; } = "gram";
    public string? ContractMonth { get; init; }
    public DateOnly? SourceDate { get; init; }
    public string DateBasis { get; init; } = "source_effective_date";
    public DateTimeOffset? SourceTimestamp { get; init; }
    public DateTimeOffset CollectedAt { get; init; }
    public string HistoryKind { get; init; } = "collected_snapshot";
    public string MarketSession { get; init; } = "unknown";
    public string? LaborFeeIncluded { get; init; }
    public string Applicability { get; init; } = "unknown";
    public int? ProviderDelayMinutes { get; init; }
    public decimal? Change { get; init; }
    public decimal? ChangePercent { get; init; }
    public string? ChangeBasis { get; init; }
    public string[] Warnings { get; init; } = [];
    public string SeriesKey => string.Join('|', SourceId, InstrumentId, Category, ProductVariant, PriceType, Currency, Unit, ContractMonth);
}

public sealed record JewelryQuoteView(JewelryQuote Quote, string DataState, bool CacheStale);
public sealed record JewelryQuoteItem(string InstrumentId, string Status, JewelryQuoteView[] Quotes,
    JewelryQuoteView[] LatestAvailable, string? Reason);
public sealed record JewelryQuoteResponse(string RequestedDate, JewelryQuoteItem[] Items);
public sealed record JewelryCatalogResponse(JewelryInstrument[] Instruments, JewelrySourceStatus[] Sources, string Notice);
public sealed record JewelryHistoryResponse(string InstrumentId, string StartDate, string EndDate,
    string DateBasis, JewelryQuoteView[] Items, int? NextOffset, string Notice);
public sealed record JewelrySourceStatus(JewelrySourceDefinition Source, bool Enabled, string RuntimeStatus,
    DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, DateTimeOffset? NextAttemptAt,
    int ConsecutiveFailures, string? LastError, int Attempts, int SuccessfulAttempts,
    string ObservationStatus);
public sealed record JewelryComparisonRow(Guid QuoteId, decimal Price, decimal Difference, decimal DifferencePercent);
public sealed record JewelryComparisonResponse(string Mode, JewelryComparisonRow[] Rows,
    string[] Warnings, string Meaning);
public sealed record JewelryConversion(decimal OriginalPrice, string FromCurrency, string FromUnit,
    decimal ConvertedPrice, string ToCurrency, string ToUnit, decimal ExchangeRate,
    string? ExchangeRateSource, DateTimeOffset? ExchangeRateTimestamp, string Formula);

public sealed class JewelrySourceEntity
{
    public string Id { get; set; } = "";
    public string DefinitionJson { get; set; } = "";
    public bool Enabled { get; set; }
}
public sealed class JewelryCollectionState
{
    public string SourceId { get; set; } = "";
    public long? LastAttemptTicks { get; set; }
    public long? LastSuccessTicks { get; set; }
    public long? NextAttemptTicks { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string Status { get; set; } = "not_collected";
    public string? LastError { get; set; }
}
public sealed class JewelryCollectionAttempt
{
    public long Id { get; set; }
    public string SourceId { get; set; } = "";
    public long StartedTicks { get; set; }
    public long CompletedTicks { get; set; }
    public bool Success { get; set; }
    public int QuoteCount { get; set; }
    public string? Error { get; set; }
}
public sealed record JewelryCollectionResult(string SourceId, string Status, int QuoteCount, string? Error);
public sealed class JewelryQuoteEntity
{
    public Guid Id { get; set; }
    public string SourceId { get; set; } = "";
    public string InstrumentId { get; set; } = "";
    public string SeriesKey { get; set; } = "";
    public string? SourceDate { get; set; }
    public long CollectedTicks { get; set; }
    public string PayloadJson { get; set; } = "";
}

namespace SignalLedger.Domain.MarketData;

public enum AssetClass { Index, Etf }
public enum PriceSeriesMeaning { Unadjusted, Adjusted, TotalReturn }

public sealed class Instrument
{
    public required string Symbol { get; init; }
    public required string DisplayName { get; set; }
    public AssetClass AssetClass { get; set; }
    public required string Currency { get; set; }
    public string ExchangeTimeZone { get; set; } = "America/New_York";
    public PriceSeriesMeaning PriceSeriesMeaning { get; set; }
    public bool SourceAvailable { get; set; }
    public DateOnly? FirstDate { get; set; }
    public DateOnly? LastDate { get; set; }
}

public sealed class DailyBar
{
    public long Id { get; init; }
    public required string Symbol { get; init; }
    public DateOnly SessionDate { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal? AdjustedClose { get; init; }
    public long? Volume { get; init; }
    public required string Currency { get; init; }
    public required string Source { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public bool IsFinal { get; init; }
}

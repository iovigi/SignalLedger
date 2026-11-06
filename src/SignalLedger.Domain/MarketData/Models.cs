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
    public long Id { get; set; }
    public required string Symbol { get; set; }
    public DateOnly SessionDate { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal? AdjustedClose { get; set; }
    public long? Volume { get; set; }
    public required string Currency { get; set; }
    public required string Source { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public bool IsFinal { get; set; }
}

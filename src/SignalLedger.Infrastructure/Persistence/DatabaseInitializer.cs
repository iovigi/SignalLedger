using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.MarketData;

namespace SignalLedger.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(SignalLedgerDbContext dbContext, bool seedSyntheticDemo = false, CancellationToken cancellationToken = default)
    {
        await dbContext.Database.MigrateAsync(cancellationToken);
        if (!await dbContext.Instruments.AnyAsync(cancellationToken))
        {
            dbContext.Instruments.AddRange(
                Index("^NDX", "NASDAQ-100 Price Index"),
                Index("^IXIC", "NASDAQ Composite Price Index"),
                Index("^GSPC", "S&P 500 Price Index"),
                new Instrument { Symbol = "QQQ", DisplayName = "Invesco QQQ ETF", AssetClass = AssetClass.Etf, Currency = "USD", PriceSeriesMeaning = PriceSeriesMeaning.Unadjusted },
                new Instrument { Symbol = "SYNTH", DisplayName = "Synthetic Demo Series", AssetClass = AssetClass.Etf, Currency = "USD", PriceSeriesMeaning = PriceSeriesMeaning.Unadjusted, SourceAvailable = true });
        }

        if (seedSyntheticDemo && !await dbContext.DailyBars.AnyAsync(x => x.Symbol == "SYNTH", cancellationToken))
        {
            var date = new DateOnly(2016, 1, 4);
            for (var i = 0; i < 2_600; date = date.AddDays(1))
            {
                if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                var close = decimal.Round(100m + (i * 0.035m) + ((decimal)Math.Sin(i / 18d) * 8m), 4);
                var open = decimal.Round(close + ((decimal)Math.Sin(i / 7d) * 0.8m), 4);
                dbContext.DailyBars.Add(new DailyBar
                {
                    Symbol = "SYNTH", SessionDate = date, Open = open, High = Math.Max(open, close) + 1m,
                    Low = Math.Min(open, close) - 1m, Close = close, Volume = 1_000_000 + i * 100,
                    Currency = "USD", Source = "Synthetic", UpdatedAtUtc = DateTimeOffset.UnixEpoch, IsFinal = true
                });
                i++;
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static Instrument Index(string symbol, string name) => new()
    {
        Symbol = symbol,
        DisplayName = name,
        AssetClass = AssetClass.Index,
        Currency = "USD",
        PriceSeriesMeaning = PriceSeriesMeaning.Unadjusted
    };
}

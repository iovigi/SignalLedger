using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.MarketData;

namespace SignalLedger.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(SignalLedgerDbContext dbContext, CancellationToken cancellationToken = default)
    {
        await dbContext.Database.MigrateAsync(cancellationToken);
        if (await dbContext.Instruments.AnyAsync(cancellationToken)) return;
        dbContext.Instruments.AddRange(
            Index("^NDX", "NASDAQ-100 Price Index"),
            Index("^IXIC", "NASDAQ Composite Price Index"),
            Index("^GSPC", "S&P 500 Price Index"),
            new Instrument { Symbol = "QQQ", DisplayName = "Invesco QQQ ETF", AssetClass = AssetClass.Etf, Currency = "USD", PriceSeriesMeaning = PriceSeriesMeaning.Unadjusted });
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

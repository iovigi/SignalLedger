using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.Runs;
using SignalLedger.Infrastructure.Persistence;

namespace SignalLedger.Infrastructure.Providers;

public sealed record MarketDataSyncResult(Guid RunId, int Added, int Updated, DateOnly? FirstDate, DateOnly? LastDate);

public sealed class MarketDataSyncService(SignalLedgerDbContext dbContext, IMarketDataProvider provider, TimeProvider timeProvider)
{
    public async Task<MarketDataSyncResult> SyncAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        var run = new ImportRun { Source = "YahooChart", Symbol = normalized, RequestedFrom = from, RequestedTo = to, StartedAtUtc = timeProvider.GetUtcNow(), Status = RunStatus.Running };
        dbContext.ImportRuns.Add(run); await dbContext.SaveChangesAsync(cancellationToken);
        try
        {
            var latest = await dbContext.DailyBars.Where(x => x.Symbol == normalized && x.Source == "YahooChart").MaxAsync(x => (DateOnly?)x.SessionDate, cancellationToken);
            var incrementalFrom = latest is { } date && date >= from ? date.AddDays(1) : from;
            var bars = incrementalFrom > to ? [] : await provider.GetDailyBarsAsync(normalized, incrementalFrom, to, cancellationToken);
            var dates = bars.Select(x => x.SessionDate).ToArray();
            var existing = await dbContext.DailyBars.Where(x => x.Symbol == normalized && x.Source == "YahooChart" && dates.Contains(x.SessionDate)).ToDictionaryAsync(x => x.SessionDate, cancellationToken);
            var added = 0; var updated = 0;
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            foreach (var bar in bars)
            {
                if (existing.TryGetValue(bar.SessionDate, out var current)) { current.Open = bar.Open; current.High = bar.High; current.Low = bar.Low; current.Close = bar.Close; current.AdjustedClose = bar.AdjustedClose; current.Volume = bar.Volume; current.UpdatedAtUtc = bar.UpdatedAtUtc; updated++; }
                else { dbContext.DailyBars.Add(bar); added++; }
            }
            run.RecordsAdded = added; run.RecordsUpdated = updated; run.ActualFrom = bars.Count == 0 ? null : bars.Min(x => x.SessionDate); run.ActualTo = bars.Count == 0 ? null : bars.Max(x => x.SessionDate); run.CompletedAtUtc = timeProvider.GetUtcNow(); run.Status = RunStatus.Completed;
            await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return new(run.Id, added, updated, run.ActualFrom, run.ActualTo);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            dbContext.ChangeTracker.Clear(); var stored = await dbContext.ImportRuns.SingleAsync(x => x.Id == run.Id, cancellationToken); stored.Status = RunStatus.Failed; stored.Error = exception.Message; stored.CompletedAtUtc = timeProvider.GetUtcNow(); await dbContext.SaveChangesAsync(cancellationToken); throw;
        }
    }
}

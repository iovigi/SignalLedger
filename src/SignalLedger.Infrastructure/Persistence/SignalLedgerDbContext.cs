using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.MarketData;
using SignalLedger.Domain.Runs;

namespace SignalLedger.Infrastructure.Persistence;

public sealed class SignalLedgerDbContext(DbContextOptions<SignalLedgerDbContext> options) : DbContext(options)
{
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<DailyBar> DailyBars => Set<DailyBar>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<BacktestRun> BacktestRuns => Set<BacktestRun>();
    public DbSet<StoredTrade> Trades => Set<StoredTrade>();
    public DbSet<StoredEquityPoint> EquityPoints => Set<StoredEquityPoint>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Instrument>(e => { e.HasKey(x => x.Symbol); e.Property(x => x.Symbol).HasMaxLength(16); e.Property(x => x.DisplayName).HasMaxLength(160); e.Property(x => x.Currency).HasMaxLength(8); e.Property(x => x.ExchangeTimeZone).HasMaxLength(80); });
        modelBuilder.Entity<DailyBar>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Symbol, x.SessionDate, x.Source }).IsUnique();
            e.Property(x => x.Symbol).HasMaxLength(16); e.Property(x => x.Currency).HasMaxLength(8); e.Property(x => x.Source).HasMaxLength(40);
            foreach (var name in new[] { nameof(DailyBar.Open), nameof(DailyBar.High), nameof(DailyBar.Low), nameof(DailyBar.Close), nameof(DailyBar.AdjustedClose) }) e.Property(name).HasPrecision(20, 8);
        });
        modelBuilder.Entity<ImportRun>(e => { e.HasKey(x => x.Id); e.Property(x => x.Source).HasMaxLength(40); e.Property(x => x.Symbol).HasMaxLength(16); });
        modelBuilder.Entity<BacktestRun>(e =>
        {
            e.HasKey(x => x.Id); e.HasIndex(x => new { x.DataFingerprint, x.ParametersJson }); e.Property(x => x.InitialCapital).HasPrecision(20, 4);
            e.Property(x => x.Symbol).HasMaxLength(16); e.Property(x => x.Source).HasMaxLength(40); e.Property(x => x.DataFingerprint).HasMaxLength(128); e.Property(x => x.EngineVersion).HasMaxLength(32); e.Property(x => x.StrategyName).HasMaxLength(40);
        });
        modelBuilder.Entity<StoredTrade>(e =>
        {
            e.HasKey(x => x.Id); e.HasIndex(x => new { x.BacktestRunId, x.Sequence }).IsUnique();
            e.Property(x => x.Side).HasMaxLength(8); e.Property(x => x.Reason).HasMaxLength(200);
            foreach (var name in new[] { nameof(StoredTrade.ReferencePrice), nameof(StoredTrade.EffectivePrice), nameof(StoredTrade.Units), nameof(StoredTrade.Notional), nameof(StoredTrade.Commission), nameof(StoredTrade.CashAfter), nameof(StoredTrade.EquityAfter) }) e.Property(name).HasPrecision(28, 10);
        });
        modelBuilder.Entity<StoredEquityPoint>(e =>
        {
            e.HasKey(x => x.Id); e.HasIndex(x => new { x.BacktestRunId, x.Series, x.SessionDate }).IsUnique(); e.Property(x => x.Series).HasMaxLength(16);
            foreach (var name in new[] { nameof(StoredEquityPoint.Equity), nameof(StoredEquityPoint.Cash), nameof(StoredEquityPoint.Units), nameof(StoredEquityPoint.Close) }) e.Property(name).HasPrecision(28, 10);
        });
    }
}

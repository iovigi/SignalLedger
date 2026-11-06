namespace SignalLedger.Domain.Backtesting;

public sealed record AnnualReturn(int Year, double Return);

public sealed record PerformanceSummary(
    decimal FinalEquity, double TotalReturn, double? Cagr, double MaxDrawdown,
    double? AnnualizedVolatility, double? SharpeRatio,
    int Transactions, int RoundTrips, decimal TotalCommission, decimal EstimatedSlippageCost,
    int TradingDays, int DaysInvested, double PercentTimeInvested,
    IReadOnlyList<AnnualReturn> AnnualReturns, AnnualReturn? WorstCalendarYear,
    IReadOnlyList<string> Warnings);

public static class PerformanceMetrics
{
    private const double DaysPerYear = 365.2425;
    private const double SessionsPerYear = 252d;

    public static PerformanceSummary Calculate(LedgerResult ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var warnings = new List<string>();
        var totalReturn = ledger.InitialCapital == 0 ? 0 : (double)(ledger.FinalEquity / ledger.InitialCapital - 1);
        double? cagr = null;
        if (ledger.Equity.Count >= 2)
        {
            var elapsedYears = (ledger.Equity[^1].Date.DayNumber - ledger.Equity[0].Date.DayNumber) / DaysPerYear;
            if (elapsedYears > 0 && ledger.FinalEquity > 0) cagr = Math.Pow((double)(ledger.FinalEquity / ledger.InitialCapital), 1d / elapsedYears) - 1d;
        }
        if (cagr is null) warnings.Add("CAGR requires at least two distinct session dates and positive ending equity.");

        decimal peak = ledger.InitialCapital; var maxDrawdown = 0d;
        foreach (var point in ledger.Equity)
        {
            if (point.Equity > peak) peak = point.Equity;
            if (peak > 0) maxDrawdown = Math.Min(maxDrawdown, (double)(point.Equity / peak - 1));
        }

        var dailyReturns = new List<double>(); var previous = ledger.InitialCapital;
        foreach (var point in ledger.Equity)
        {
            if (previous > 0) dailyReturns.Add((double)(point.Equity / previous - 1));
            previous = point.Equity;
        }
        double? volatility = null; double? sharpe = null;
        if (dailyReturns.Count >= 2)
        {
            var mean = dailyReturns.Average();
            var variance = dailyReturns.Sum(x => Math.Pow(x - mean, 2)) / (dailyReturns.Count - 1);
            var dailyDeviation = Math.Sqrt(variance);
            volatility = dailyDeviation * Math.Sqrt(SessionsPerYear);
            if (dailyDeviation > 0) sharpe = mean / dailyDeviation * Math.Sqrt(SessionsPerYear);
            else warnings.Add("Sharpe ratio is unavailable because return volatility is zero.");
        }
        else warnings.Add("Volatility and Sharpe ratio require at least two daily returns.");

        var annual = new List<AnnualReturn>();
        previous = ledger.InitialCapital;
        foreach (var year in ledger.Equity.GroupBy(x => x.Date.Year).OrderBy(x => x.Key))
        {
            var ending = year.Last().Equity;
            annual.Add(new(year.Key, previous > 0 ? (double)(ending / previous - 1) : 0));
            previous = ending;
        }
        var invested = ledger.Equity.Count(x => x.Invested);
        return new(
            ledger.FinalEquity, totalReturn, cagr, maxDrawdown, volatility, sharpe,
            ledger.Trades.Count, ledger.Trades.Count(x => x.Side == TradeSide.Sell), ledger.TotalCommission, ledger.EstimatedSlippageCost,
            ledger.Equity.Count, invested, ledger.Equity.Count == 0 ? 0 : (double)invested / ledger.Equity.Count,
            annual, annual.Count == 0 ? null : annual.MinBy(x => x.Return), warnings);
    }
}

using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SignalLedger.Domain.MarketData;
using SignalLedger.Domain.Runs;
using SignalLedger.Infrastructure.Persistence;

namespace SignalLedger.Infrastructure.Imports;

public sealed record CsvFieldMap(
    string Date = "Date",
    string Open = "Open",
    string High = "High",
    string Low = "Low",
    string Close = "Close",
    string AdjustedClose = "Adj Close",
    string Volume = "Volume");

public sealed record CsvImportRequest(
    string Symbol,
    string Source,
    string Currency,
    CsvFieldMap? Fields = null,
    int MaximumRows = 20_000);

public sealed record CsvImportError(int Row, string? Field, string Message, string? Value);

public sealed record CsvImportResult(
    Guid ImportRunId,
    int Added,
    int Updated,
    int Rejected,
    IReadOnlyList<CsvImportError> Errors,
    DateOnly? FirstDate,
    DateOnly? LastDate);

public sealed class CsvImportService(SignalLedgerDbContext dbContext, TimeProvider timeProvider)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public async Task<CsvImportResult> ImportAsync(
        TextReader reader,
        CsvImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ValidateRequest(request);
        var started = timeProvider.GetUtcNow();
        var run = new ImportRun
        {
            Source = request.Source.Trim(),
            Symbol = request.Symbol.Trim().ToUpperInvariant(),
            StartedAtUtc = started,
            Status = RunStatus.Running
        };
        dbContext.ImportRuns.Add(run);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var parsed = await ParseAsync(reader, request, cancellationToken);
            var existing = await dbContext.DailyBars
                .Where(x => x.Symbol == run.Symbol && x.Source == run.Source && parsed.Bars.Select(b => b.SessionDate).Contains(x.SessionDate))
                .ToDictionaryAsync(x => x.SessionDate, cancellationToken);

            var added = 0;
            var updated = 0;
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            foreach (var bar in parsed.Bars)
            {
                if (existing.TryGetValue(bar.SessionDate, out var current))
                {
                    current.Open = bar.Open;
                    current.High = bar.High;
                    current.Low = bar.Low;
                    current.Close = bar.Close;
                    current.AdjustedClose = bar.AdjustedClose;
                    current.Volume = bar.Volume;
                    current.Currency = bar.Currency;
                    current.UpdatedAtUtc = bar.UpdatedAtUtc;
                    current.IsFinal = bar.IsFinal;
                    updated++;
                }
                else
                {
                    dbContext.DailyBars.Add(bar);
                    added++;
                }
            }

            run.RecordsAdded = added;
            run.RecordsUpdated = updated;
            run.RecordsRejected = parsed.RejectedRows;
            run.ActualFrom = parsed.Bars.Count == 0 ? null : parsed.Bars.Min(x => x.SessionDate);
            run.ActualTo = parsed.Bars.Count == 0 ? null : parsed.Bars.Max(x => x.SessionDate);
            run.CompletedAtUtc = timeProvider.GetUtcNow();
            run.Status = RunStatus.Completed;
            run.Warning = parsed.RejectedRows == 0 ? null : $"{parsed.RejectedRows} row(s) rejected; inspect the error list.";
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(run.Id, added, updated, parsed.RejectedRows, parsed.Errors, run.ActualFrom, run.ActualTo);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            dbContext.ChangeTracker.Clear();
            var persistedRun = await dbContext.ImportRuns.SingleAsync(x => x.Id == run.Id, cancellationToken);
            persistedRun.Status = RunStatus.Failed;
            persistedRun.Error = exception.Message;
            persistedRun.CompletedAtUtc = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<ParsedCsv> ParseAsync(TextReader reader, CsvImportRequest request, CancellationToken cancellationToken)
    {
        var headerLine = await reader.ReadLineAsync(cancellationToken) ?? throw new CsvImportException("CSV file is empty.");
        var headers = ParseLine(headerLine);
        var fields = request.Fields ?? new();
        var indexes = RequiredIndexes(headers, fields);
        var bars = new List<DailyBar>();
        var errors = new List<CsvImportError>();
        var rejectedRows = 0;
        var dates = new HashSet<DateOnly>();
        var row = 1;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            row++;
            if (row - 1 > request.MaximumRows) throw new CsvImportException($"CSV exceeds the maximum of {request.MaximumRows} data rows.");
            if (string.IsNullOrWhiteSpace(line)) { errors.Add(new(row, null, "Blank rows are not allowed.", null)); rejectedRows++; continue; }
            IReadOnlyList<string> values;
            try { values = ParseLine(line); }
            catch (CsvImportException ex) { errors.Add(new(row, null, ex.Message, line)); rejectedRows++; continue; }
            if (values.Count != headers.Count) { errors.Add(new(row, null, $"Expected {headers.Count} columns but found {values.Count}.", line)); rejectedRows++; continue; }

            var rowErrors = new List<CsvImportError>();
            var date = ParseDate(values[indexes.Date], row, fields.Date, rowErrors);
            var open = ParseDecimal(values[indexes.Open], row, fields.Open, rowErrors);
            var high = ParseDecimal(values[indexes.High], row, fields.High, rowErrors);
            var low = ParseDecimal(values[indexes.Low], row, fields.Low, rowErrors);
            var close = ParseDecimal(values[indexes.Close], row, fields.Close, rowErrors);
            var adjusted = indexes.AdjustedClose is null || string.IsNullOrWhiteSpace(values[indexes.AdjustedClose.Value]) ? null : ParseDecimal(values[indexes.AdjustedClose.Value], row, fields.AdjustedClose, rowErrors);
            var volume = indexes.Volume is null || string.IsNullOrWhiteSpace(values[indexes.Volume.Value]) ? null : ParseLong(values[indexes.Volume.Value], row, fields.Volume, rowErrors);
            if (date is not null && !dates.Add(date.Value)) rowErrors.Add(new(row, fields.Date, "Duplicate session date in this file.", values[indexes.Date]));
            if (open <= 0 || high <= 0 || low <= 0 || close <= 0) rowErrors.Add(new(row, "OHLC", "OHLC prices must all be positive.", null));
            if (high < Math.Max(Math.Max(open ?? 0, close ?? 0), low ?? 0)) rowErrors.Add(new(row, fields.High, "High must be greater than or equal to Open, Close, and Low.", values[indexes.High]));
            if (low > Math.Min(Math.Min(open ?? decimal.MaxValue, close ?? decimal.MaxValue), high ?? decimal.MaxValue)) rowErrors.Add(new(row, fields.Low, "Low must be less than or equal to Open, Close, and High.", values[indexes.Low]));
            if (volume < 0) rowErrors.Add(new(row, fields.Volume, "Volume cannot be negative.", volume?.ToString(Invariant)));
            if (rowErrors.Count > 0) { errors.AddRange(rowErrors); rejectedRows++; continue; }

            bars.Add(new DailyBar { Symbol = request.Symbol.Trim().ToUpperInvariant(), SessionDate = date!.Value, Open = open!.Value, High = high!.Value, Low = low!.Value, Close = close!.Value, AdjustedClose = adjusted, Volume = volume, Currency = request.Currency.Trim().ToUpperInvariant(), Source = request.Source.Trim(), UpdatedAtUtc = DateTimeOffset.UtcNow, IsFinal = true });
        }
        return new(bars, errors, rejectedRows);
    }

    private static void ValidateRequest(CsvImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol) || request.Symbol.Length > 16) throw new ArgumentException("Symbol is required and must not exceed 16 characters.");
        if (string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > 40) throw new ArgumentException("Source is required and must not exceed 40 characters.");
        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length > 8) throw new ArgumentException("Currency is required and must not exceed 8 characters.");
        if (request.MaximumRows is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(request), "MaximumRows must be between 1 and 100,000.");
    }

    private static HeaderIndexes RequiredIndexes(IReadOnlyList<string> headers, CsvFieldMap fields)
    {
        int Required(string name) { var i = Find(headers, name); return i ?? throw new CsvImportException($"Required column '{name}' was not found."); }
        return new(Required(fields.Date), Required(fields.Open), Required(fields.High), Required(fields.Low), Required(fields.Close), Find(headers, fields.AdjustedClose), Find(headers, fields.Volume));
    }

    private static int? Find(IReadOnlyList<string> headers, string name) { for (var i = 0; i < headers.Count; i++) if (string.Equals(headers[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i; return null; }
    private static DateOnly? ParseDate(string value, int row, string field, List<CsvImportError> errors) { if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var parsed)) return parsed; errors.Add(new(row, field, "Date must use ISO yyyy-MM-dd format.", value)); return null; }
    private static decimal? ParseDecimal(string value, int row, string field, List<CsvImportError> errors) { if (decimal.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, Invariant, out var parsed)) return parsed; errors.Add(new(row, field, "Value must be an invariant-culture decimal number.", value)); return null; }
    private static long? ParseLong(string value, int row, string field, List<CsvImportError> errors) { if (long.TryParse(value.Trim(), NumberStyles.Integer, Invariant, out var parsed)) return parsed; errors.Add(new(row, field, "Value must be an integer.", value)); return null; }

    private static IReadOnlyList<string> ParseLine(string line)
    {
        var values = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { values.Add(value.ToString()); value.Clear(); }
            else value.Append(c);
        }
        if (quoted) throw new CsvImportException("Unterminated quoted field.");
        values.Add(value.ToString()); return values;
    }

    private sealed record ParsedCsv(List<DailyBar> Bars, List<CsvImportError> Errors, int RejectedRows);
    private sealed record HeaderIndexes(int Date, int Open, int High, int Low, int Close, int? AdjustedClose, int? Volume);
}

public sealed class CsvImportException(string message) : Exception(message);

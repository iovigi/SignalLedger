import { useEffect, useMemo, useState } from 'react'
import { Area, AreaChart, Bar, BarChart, CartesianGrid, Legend, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { cancelExperiment, createExperiment, getExperiment, getInstruments, importCsv, runBacktest } from './api'
import type { BacktestResponse, ExperimentResponse, ImportResult, Instrument, SeriesPoint } from './types'
import './dashboard.css'

type Page = 'dashboard' | 'data' | 'explorer' | 'runs' | 'methodology'
const money = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 2 })
const percent = (value: number | null) => value == null ? '—' : `${(value * 100).toFixed(2)}%`
const compact = (value: number) => new Intl.NumberFormat('en-US', { maximumFractionDigits: 4 }).format(value)
const defaultEndDate = new Date().toISOString().slice(0, 10)
function buildDrawdown(series: SeriesPoint[]) { let peak = 0; return series.map(point => { peak = Math.max(peak, point.strategyEquity); return { date: point.date, drawdown: peak ? point.strategyEquity / peak - 1 : 0 } }) }

function DashboardApp() {
  const [page, setPage] = useState<Page>('dashboard')
  const [instruments, setInstruments] = useState<Instrument[]>([])
  const [loadingCatalog, setLoadingCatalog] = useState(true)
  const [catalogError, setCatalogError] = useState('')
  const [result, setResult] = useState<BacktestResponse | null>(null)
  const [running, setRunning] = useState(false)
  const [runError, setRunError] = useState('')
  const [dark, setDark] = useState(true)

  async function refreshCatalog() {
    setLoadingCatalog(true); setCatalogError('')
    try { setInstruments(await getInstruments()) } catch (error) { setCatalogError((error as Error).message) } finally { setLoadingCatalog(false) }
  }
  // The initial catalog fetch is the external synchronization this effect owns.
  // oxlint-disable-next-line react/set-state-in-effect
  useEffect(() => { void refreshCatalog() }, [])

  async function submit(payload: unknown) {
    setRunning(true); setRunError('')
    try { setResult(await runBacktest(payload)) } catch (error) { setRunError((error as Error).message) } finally { setRunning(false) }
  }
  async function openExplorerRun(payload: unknown) { await submit(payload); setPage('dashboard') }

  return <div className={dark ? 'app theme-dark' : 'app theme-light'}>
    <a className="skip-link" href="#main">Skip to content</a>
    <header className="app-header">
      <button className="brand-button" onClick={() => setPage('dashboard')}><span className="brand-mark">SL</span><span><b>SignalLedger</b><small>Market backtesting lab</small></span></button>
      <nav aria-label="Primary navigation">
        {([['dashboard', 'Dashboard'], ['data', 'Data Manager'], ['explorer', 'Strategy Explorer'], ['runs', 'Runs & Journal'], ['methodology', 'Methodology']] as [Page, string][]).map(([key, label]) => <button key={key} className={page === key ? 'nav-active' : ''} aria-current={page === key ? 'page' : undefined} onClick={() => setPage(key)}>{label}</button>)}
      </nav>
      <button className="icon-button" onClick={() => setDark(value => !value)} aria-label={`Use ${dark ? 'light' : 'dark'} theme`}>{dark ? '☀' : '☾'}</button>
    </header>
    <main id="main">
      {catalogError && <Status kind="error">Could not load data catalog: {catalogError}</Status>}
      {page === 'dashboard' && <Dashboard instruments={instruments} catalogLoading={loadingCatalog} result={result} running={running} error={runError} onRun={submit} onOpenData={() => setPage('data')} />}
      {page === 'data' && <DataManager instruments={instruments} loading={loadingCatalog} onRefresh={refreshCatalog} />}
      {page === 'explorer' && <StrategyExplorer instruments={instruments} onOpenRun={openExplorerRun} />}
      {page === 'runs' && <RunJournal result={result} />}
      {page === 'methodology' && <Methodology />}
    </main>
    <footer>Educational software · Not financial advice · Engine results are hypothetical and reproducible.</footer>
  </div>
}

function Dashboard({ instruments, catalogLoading, result, running, error, onRun, onOpenData }: { instruments: Instrument[]; catalogLoading: boolean; result: BacktestResponse | null; running: boolean; error: string; onRun: (payload: unknown) => Promise<void>; onOpenData: () => void }) {
  const usable = instruments.filter(x => x.sources.length > 0)
  const [symbol, setSymbol] = useState('QQQ'); const [source, setSource] = useState('UserCsv')
  const [from, setFrom] = useState('2020-01-01'); const [to, setTo] = useState(defaultEndDate)
  const [capital, setCapital] = useState(1000); const [strategy, setStrategy] = useState('SMA'); const [period, setPeriod] = useState(30)
  const [fee, setFee] = useState(0); const [slippage, setSlippage] = useState(0)
  const selected = instruments.find(x => x.symbol === symbol)
  const validation = from > to ? 'Start date must not be after end date.' : capital < 1 ? 'Initial capital must be at least 1.' : period < 1 || period > 500 ? 'Period must be between 1 and 500.' : ''
  const drawdown = useMemo(() => buildDrawdown(result?.series ?? []), [result])
  const annual = result?.strategy.annualReturns.map(item => ({ year: item.year, strategy: item.return, benchmark: result.benchmark.annualReturns.find(x => x.year === item.year)?.return ?? 0 })) ?? []
  async function handleSubmit(event: React.FormEvent) { event.preventDefault(); if (validation) return; await onRun({ symbol, source, startDate: from, endDate: to, initialCapital: capital, strategy: { type: strategy, period: strategy === 'BuyAndHold' ? null : period }, execution: { commissionPercentPerSide: fee, slippageBpsPerSide: slippage, liquidateAtEnd: false, cashYieldMode: 'None' } }) }

  return <>
    <section className="page-heading"><div><span className="eyebrow">REPRODUCIBLE RESEARCH</span><h1>Strategy dashboard</h1><p>Compare timing rules with Buy & Hold under identical dates, costs, and next-open execution.</p></div><span className="engine-badge">Engine v{result?.engineVersion ?? '1.0.0'}</span></section>
    <section className="panel controls-panel" aria-labelledby="configuration-title"><h2 id="configuration-title">Backtest configuration</h2>
      {usable.length === 0 && !catalogLoading ? <Empty title="No market data yet" action="Open Data Manager" onAction={onOpenData}>Import the included synthetic CSV or your licensed data to run a backtest.</Empty> :
      <form onSubmit={handleSubmit}>
        <div className="form-grid">
          <Field label="Symbol"><select value={symbol} onChange={e => { const next = e.target.value; setSymbol(next); setSource(instruments.find(x => x.symbol === next)?.sources[0]?.source ?? 'UserCsv') }}>{instruments.map(x => <option key={x.symbol} value={x.symbol}>{x.symbol} — {x.displayName}</option>)}</select></Field>
          <Field label="Source"><select value={source} onChange={e => setSource(e.target.value)}>{(selected?.sources ?? []).map(x => <option key={x.source}>{x.source}</option>)}{!selected?.sources.length && <option>UserCsv</option>}</select></Field>
          <Field label="From"><input type="date" required value={from} onChange={e => setFrom(e.target.value)} /></Field>
          <Field label="To"><input type="date" required value={to} onChange={e => setTo(e.target.value)} /></Field>
          <Field label="Initial capital (USD)"><input type="number" min="1" max="1000000000" step="1" value={capital} onChange={e => setCapital(e.target.valueAsNumber)} /></Field>
          <Field label="Strategy"><select value={strategy} onChange={e => setStrategy(e.target.value)}><option value="SMA">SMA timing</option><option value="EMA">EMA timing</option><option value="BuyAndHold">Buy & Hold only</option></select></Field>
          <Field label="Moving-average sessions"><input type="number" min="1" max="500" disabled={strategy === 'BuyAndHold'} value={period} onChange={e => setPeriod(e.target.valueAsNumber)} /></Field>
          <Field label="Commission per side (%)"><input type="number" min="0" max="10" step="0.05" value={fee} onChange={e => setFee(e.target.valueAsNumber)} /><span className="field-hint">Preset: <button type="button" onClick={() => setFee(.2)}>0.2%</button></span></Field>
          <Field label="Slippage per side (bps)"><input type="number" min="0" max="1000" value={slippage} onChange={e => setSlippage(e.target.valueAsNumber)} /><span className="field-hint">0 bps is optimistic.</span></Field>
          <Field label="Cash yield"><select disabled value="None"><option>None (0%)</option></select><span className="field-hint">Historical T-bill mode requires validated point-in-time data.</span></Field>
        </div>
        {validation && <p className="validation" role="alert">{validation}</p>}
        {selected?.warnings.map(warning => <p className="inline-warning" key={warning}>⚠ {warning}</p>)}
        <button className="primary-button" type="submit" disabled={running || !!validation || catalogLoading}>{running ? 'Running deterministic simulation…' : 'Run comparison'}</button>
      </form>}
    </section>
    {error && <Status kind="error">{error}</Status>}
    {result ? <Results result={result} drawdown={drawdown} annual={annual} /> : usable.length > 0 && <Empty title="Ready to test a hypothesis">Choose assumptions and run a comparison. Results, costs, and every trade will remain auditable.</Empty>}
  </>
}

function Results({ result, drawdown, annual }: { result: BacktestResponse; drawdown: { date: string; drawdown: number }[]; annual: { year: number; strategy: number; benchmark: number }[] }) {
  return <section aria-live="polite" className="results-stack">
    <div className="metric-grid"><Metric label="Strategy final equity" value={money.format(result.strategy.finalEquity)} detail={`B&H ${money.format(result.benchmark.finalEquity)}`} /><Metric label="CAGR" value={percent(result.strategy.cagr)} detail={`B&H ${percent(result.benchmark.cagr)}`} /><Metric label="Maximum drawdown" value={percent(result.strategy.maxDrawdown)} detail={`B&H ${percent(result.benchmark.maxDrawdown)}`} /><Metric label="Transactions" value={String(result.strategy.transactions)} detail={`${result.strategy.roundTrips} completed round trips`} /><Metric label="Time invested" value={percent(result.strategy.percentTimeInvested)} detail={`${result.strategy.daysInvested} of ${result.strategy.tradingDays} sessions`} /></div>
    <div className="comparison-banner"><span>Net performance versus Buy & Hold</span><strong className={result.outperformanceVsBenchmark >= 0 ? 'positive' : 'negative'}>{result.outperformanceVsBenchmark >= 0 ? '+' : ''}{percent(result.outperformanceVsBenchmark)}</strong><small>Same source, dates, capital, fees, slippage, and end convention.</small></div>
    {result.warnings.map(warning => <Status key={warning} kind="warning">{warning}</Status>)}
    <div className="chart-grid">
      <ChartPanel title="Equity curves" subtitle="Common timestamps · absolute USD"><ResponsiveContainer width="100%" height={300}><LineChart data={result.series}><CartesianGrid strokeDasharray="3 3" /><XAxis dataKey="date" minTickGap={40} /><YAxis /><Tooltip formatter={v => money.format(Number(Array.isArray(v) ? v[0] : v ?? 0))} /><Legend /><Line dataKey="strategyEquity" name={result.strategyName} stroke="#55dda7" dot={false} strokeWidth={2} /><Line dataKey="benchmarkEquity" name="Buy & Hold" stroke="#6fa8ff" dot={false} strokeWidth={2} /></LineChart></ResponsiveContainer></ChartPanel>
      <ChartPanel title="Strategy drawdown" subtitle="Peak-to-trough percentage"><ResponsiveContainer width="100%" height={300}><AreaChart data={drawdown}><CartesianGrid strokeDasharray="3 3" /><XAxis dataKey="date" minTickGap={40} /><YAxis tickFormatter={v => `${(v * 100).toFixed(0)}%`} /><Tooltip formatter={v => percent(Number(Array.isArray(v) ? v[0] : v ?? 0))} /><Area dataKey="drawdown" stroke="#ff806d" fill="#ff806d33" /></AreaChart></ResponsiveContainer></ChartPanel>
      <ChartPanel title="Price and indicator" subtitle="Raw close used for signals"><ResponsiveContainer width="100%" height={300}><LineChart data={result.series}><CartesianGrid strokeDasharray="3 3" /><XAxis dataKey="date" minTickGap={40} /><YAxis domain={['auto', 'auto']} /><Tooltip /><Legend /><Line dataKey="close" name="Close" stroke="#f0f4f2" dot={false} /><Line dataKey="indicator" name={result.strategyName} stroke="#f4bc55" dot={false} connectNulls /></LineChart></ResponsiveContainer></ChartPanel>
      <ChartPanel title="Calendar-year returns" subtitle="Partial first/last years remain visible"><ResponsiveContainer width="100%" height={300}><BarChart data={annual}><CartesianGrid strokeDasharray="3 3" /><XAxis dataKey="year" /><YAxis tickFormatter={v => `${(v * 100).toFixed(0)}%`} /><Tooltip formatter={v => percent(Number(Array.isArray(v) ? v[0] : v ?? 0))} /><Legend /><Bar dataKey="strategy" name={result.strategyName} fill="#55dda7" /><Bar dataKey="benchmark" name="Buy & Hold" fill="#6fa8ff" /></BarChart></ResponsiveContainer></ChartPanel>
    </div>
    <AssumptionStrip result={result} />
    <TradeTable result={result} />
  </section>
}

function StrategyExplorer({ instruments, onOpenRun }: { instruments: Instrument[]; onOpenRun: (payload: unknown) => Promise<void> }) {
  const [symbol, setSymbol] = useState('QQQ'); const [source, setSource] = useState('UserCsv')
  const [from, setFrom] = useState('2020-01-01'); const [to, setTo] = useState(defaultEndDate); const [capital, setCapital] = useState(1000)
  const [periods, setPeriods] = useState('5,10,20,30,50,100,150,200'); const [fees, setFees] = useState('0,0.05,0.1,0.2,0.5')
  const [split, setSplit] = useState(false); const [trainingEnd, setTrainingEnd] = useState('2023-12-31'); const [holdoutStart, setHoldoutStart] = useState('2024-01-01')
  const [rolling, setRolling] = useState(false); const [rollingStarts, setRollingStarts] = useState('2020-01-01,2022-01-01'); const [rollingYears, setRollingYears] = useState(3)
  const [job, setJob] = useState<ExperimentResponse | null>(null); const [error, setError] = useState(''); const [view, setView] = useState('Full window'); const [opening, setOpening] = useState(false)
  const selected = instruments.find(x => x.symbol === symbol)
  const pollingJobId = job?.id; const pollingJobStatus = job?.status
  useEffect(() => {
    if (!pollingJobId || !pollingJobStatus || !['Pending', 'Running'].includes(pollingJobStatus)) return
    let active = true; const poll = async () => { try { const next = await getExperiment(pollingJobId); if (active) setJob(next); if (active && ['Pending', 'Running'].includes(next.status)) window.setTimeout(poll, 250) } catch (e) { if (active) setError((e as Error).message) } }
    const timer = window.setTimeout(poll, 250); return () => { active = false; window.clearTimeout(timer) }
  }, [pollingJobId, pollingJobStatus])
  const parsedPeriods = periods.split(',').map(x => Number(x.trim())).filter(Number.isFinite)
  const parsedFees = fees.split(',').map(x => Number(x.trim())).filter(Number.isFinite)
  const labels = useMemo(() => Array.from(new Set((job?.cells ?? []).map(x => x.windowLabel ?? 'Full window'))), [job])
  const visible = (job?.cells ?? []).filter(x => (x.windowLabel ?? 'Full window') === view)
  const rowPeriods = Array.from(new Set(visible.map(x => x.period))).sort((a, b) => a - b); const columns = Array.from(new Set(visible.map(x => x.commissionPercent))).sort((a, b) => a - b)
  async function start(event: React.FormEvent) {
    event.preventDefault(); setError(''); setJob(null); setView(split ? 'Training' : rolling ? `Rolling ${rollingStarts.split(',')[0]?.trim()}` : 'Full window')
    try { setJob(await createExperiment({ symbol, source, startDate: from, endDate: to, initialCapital: capital, periods: parsedPeriods, commissionPercents: parsedFees, slippageBps: 0, rollingStartDates: rolling ? rollingStarts.split(',').map(x => x.trim()).filter(Boolean) : null, rollingWindowYears: rolling ? rollingYears : null, trainingEndDate: split ? trainingEnd : null, holdoutStartDate: split ? holdoutStart : null })) } catch (e) { setError((e as Error).message) }
  }
  async function drill(period: number, fee: number) { setOpening(true); await onOpenRun({ symbol, source, startDate: from, endDate: to, initialCapital: capital, strategy: { type: 'SMA', period }, execution: { commissionPercentPerSide: fee, slippageBpsPerSide: 0, liquidateAtEnd: false, cashYieldMode: 'None' } }); setOpening(false) }
  function exportCsv() { if (!job) return; const csv = ['window,holdout,period,commissionPercent,strategyReturn,benchmarkReturn,netOutperformance', ...job.cells.map(x => `${x.windowLabel ?? 'Full window'},${x.isHoldout},${x.period},${x.commissionPercent},${x.strategyReturn},${x.benchmarkReturn},${x.netOutperformance}`)].join('\n'); const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv' })); const link = document.createElement('a'); link.href = url; link.download = `signalledger-experiment-${job.id}.csv`; link.click(); URL.revokeObjectURL(url) }
  return <><section className="page-heading"><div><span className="eyebrow">ROBUSTNESS, NOT PARAMETER WORSHIP</span><h1>Strategy Explorer</h1><p>Compare bounded SMA grids, rolling windows, and a holdout period under identical assumptions.</p></div></section>
    <section className="panel controls-panel"><h2>Experiment configuration</h2><form onSubmit={start}><div className="form-grid">
      <Field label="Symbol"><select value={symbol} onChange={e => { const next = e.target.value; setSymbol(next); setSource(instruments.find(x => x.symbol === next)?.sources[0]?.source ?? 'UserCsv') }}>{instruments.map(x => <option key={x.symbol}>{x.symbol}</option>)}</select></Field>
      <Field label="Source"><select value={source} onChange={e => setSource(e.target.value)}>{(selected?.sources ?? []).map(x => <option key={x.source}>{x.source}</option>)}{!selected?.sources.length && <option>UserCsv</option>}</select></Field>
      <Field label="From"><input type="date" value={from} onChange={e => setFrom(e.target.value)} /></Field><Field label="To"><input type="date" value={to} onChange={e => setTo(e.target.value)} /></Field><Field label="Capital"><input type="number" min="1" value={capital} onChange={e => setCapital(e.target.valueAsNumber)} /></Field>
      <Field label="SMA periods (comma-separated)"><input value={periods} onChange={e => setPeriods(e.target.value)} /></Field><Field label="Fees % per side"><input value={fees} onChange={e => setFees(e.target.value)} /></Field>
      <Field label="Holdout validation"><span className="check-row"><input type="checkbox" checked={split} onChange={e => setSplit(e.target.checked)} /> Enable split</span></Field><Field label="Training ends"><input type="date" disabled={!split} value={trainingEnd} onChange={e => setTrainingEnd(e.target.value)} /></Field><Field label="Holdout starts"><input type="date" disabled={!split} value={holdoutStart} onChange={e => setHoldoutStart(e.target.value)} /></Field>
      <Field label="Rolling windows"><span className="check-row"><input type="checkbox" checked={rolling} onChange={e => setRolling(e.target.checked)} /> Enable rolling starts</span></Field><Field label="Rolling starts"><input disabled={!rolling} value={rollingStarts} onChange={e => setRollingStarts(e.target.value)} /></Field><Field label="Window years"><input type="number" min="1" max="30" disabled={!rolling} value={rollingYears} onChange={e => setRollingYears(e.target.valueAsNumber)} /></Field>
    </div><button className="primary-button" disabled={job?.status === 'Pending' || job?.status === 'Running'}>Run bounded grid</button></form></section>
    {error && <Status kind="error">{error}</Status>}
    {job && <section className="panel explorer-results"><div className="section-title"><div><h2>Experiment heatmap</h2><p className="muted">{job.completedCells} / {job.totalCells} cells · {job.status}</p></div><div className="button-row">{['Pending', 'Running'].includes(job.status) && <button className="secondary-button" onClick={() => void cancelExperiment(job.id)}>Cancel</button>}<button className="secondary-button" disabled={job.cells.length === 0} onClick={exportCsv}>Export CSV</button></div></div>
      <progress value={job.completedCells} max={job.totalCells} aria-label="Experiment progress" />
      {job.error && <Status kind="error">{job.error}</Status>}
      {job.selectedTrainingPeriod != null && <Status kind="warning">Training-only selection: SMA {job.selectedTrainingPeriod}. Holdout values were not used to select this period.</Status>}
      {labels.length > 1 && <Field label="Displayed window"><select value={labels.includes(view) ? view : labels[0]} onChange={e => setView(e.target.value)}>{labels.map(x => <option key={x}>{x}</option>)}</select></Field>}
      {visible.length > 0 && <div className="heatmap-wrap"><table className="heatmap"><thead><tr><th>SMA ↓ / fee →</th>{columns.map(fee => <th key={fee}>{fee}%</th>)}</tr></thead><tbody>{rowPeriods.map(period => <tr key={period}><th>{period}</th>{columns.map(fee => { const cell = visible.find(x => x.period === period && x.commissionPercent === fee)!; const intensity = Math.min(65, 15 + Math.abs(cell.netOutperformance) * 300); return <td key={fee}><button disabled={opening} style={{ background: cell.netOutperformance >= 0 ? `color-mix(in srgb, var(--accent) ${intensity}%, var(--panel2))` : `color-mix(in srgb, var(--danger) ${intensity}%, var(--panel2))` }} onClick={() => void drill(period, fee)} aria-label={`Open SMA ${period}, fee ${fee} percent, outperformance ${percent(cell.netOutperformance)}`}>{cell.netOutperformance >= 0 ? '+' : ''}{percent(cell.netOutperformance)}</button></td> })}</tr>)}</tbody></table></div>}
      <p className="inline-warning">⚠ Heatmap ranking is descriptive, not evidence of future performance. Prefer stable regions and inspect rolling/holdout results before drawing conclusions.</p>
    </section>}
  </>
}

function DataManager({ instruments, loading, onRefresh }: { instruments: Instrument[]; loading: boolean; onRefresh: () => Promise<void> }) {
  const [symbol, setSymbol] = useState('QQQ'); const [file, setFile] = useState<File | null>(null); const [importing, setImporting] = useState(false); const [result, setResult] = useState<ImportResult | null>(null); const [error, setError] = useState('')
  async function upload(event: React.FormEvent) { event.preventDefault(); if (!file) return; setImporting(true); setError(''); setResult(null); try { const imported = await importCsv(symbol, file); setResult(imported); await onRefresh() } catch (e) { setError((e as Error).message) } finally { setImporting(false) } }
  return <><section className="page-heading"><div><span className="eyebrow">LOCAL, LICENSE-AWARE CACHE</span><h1>Data Manager</h1><p>Import data you own, inspect coverage, and keep provider restrictions visible.</p></div></section>
    <div className="data-layout"><section className="panel"><h2>CSV import</h2><p className="muted">Required headers: Date, Open, High, Low, Close. ISO dates and invariant decimals only; Adj Close and Volume are optional.</p><form onSubmit={upload} className="upload-form"><Field label="Instrument"><select value={symbol} onChange={e => setSymbol(e.target.value)}>{instruments.map(x => <option key={x.symbol}>{x.symbol}</option>)}</select></Field><Field label="CSV file"><input type="file" accept=".csv,text/csv" required onChange={e => setFile(e.target.files?.[0] ?? null)} /></Field><button className="primary-button" disabled={!file || importing}>{importing ? 'Validating and importing…' : 'Import CSV'}</button></form>{error && <Status kind="error">{error}</Status>}{result && <div className="import-result"><strong>Import completed</strong><span>{result.added} added · {result.updated} updated · {result.rejected} rejected</span>{result.errors.length > 0 && <details><summary>View {result.errors.length} validation messages</summary><ul>{result.errors.slice(0, 100).map((x, i) => <li key={`${x.row}-${i}`}>Row {x.row}{x.field ? ` · ${x.field}` : ''}: {x.message}</li>)}</ul></details>}</div>}</section>
      <section className="panel"><div className="section-title"><div><h2>Cached instruments</h2><p className="muted">Missing sessions use the US equity calendar.</p></div><button className="secondary-button" onClick={() => void onRefresh()}>Refresh</button></div>{loading ? <p role="status">Loading coverage…</p> : <div className="instrument-list">{instruments.map(item => <article key={item.symbol}><div><strong>{item.symbol}</strong><span>{item.displayName}</span></div><span className={item.tradable ? 'tag' : 'tag warning-tag'}>{item.tradable ? 'Tradeable proxy' : 'Non-tradable index'}</span>{item.sources.length === 0 ? <p>No cached bars</p> : item.sources.map(source => <p key={source.source}>{source.source}: <b>{source.barCount.toLocaleString()}</b> bars · {source.firstDate} → {source.lastDate} · {source.missingSessions.length} missing sessions</p>)}</article>)}</div>}</section></div>
    <Status kind="warning">Yahoo Chart is unofficial and disabled. Enable sync only after the deployer confirms authorized use. Daily OHLC is not Bid/Ask, and index values are theoretical proxies.</Status></>
}

function RunJournal({ result }: { result: BacktestResponse | null }) { return <><section className="page-heading"><div><span className="eyebrow">AUDIT TRAIL</span><h1>Runs & trade journal</h1><p>Inspect signal dates, next-open fills, quantities, fees, and post-trade balances.</p></div></section>{result ? <><AssumptionStrip result={result} /><TradeTable result={result} /></> : <Empty title="No run in this browser session">Run a dashboard comparison to populate the journal. Persisted API runs remain addressable by their run ID.</Empty>}</> }

function TradeTable({ result }: { result: BacktestResponse }) { return <section className="panel"><div className="section-title"><div><h2>Transaction journal</h2><p className="muted">{result.trades.length} individual transactions · {result.strategy.roundTrips} completed round trips</p></div><code>{result.id.slice(0, 8)}</code></div>{result.trades.length === 0 ? <p>No orders executed; the strategy remained in cash.</p> : <div className="table-scroll"><table><thead><tr><th>#</th><th>Side</th><th>Signal close</th><th>Execution</th><th>Reference</th><th>Effective</th><th>Units</th><th>Commission</th><th>Cash after</th><th>Reason</th></tr></thead><tbody>{result.trades.map((trade, index) => <tr key={`${trade.executionDate}-${index}`}><td>{index + 1}</td><td><span className={`side side-${trade.side.toLowerCase()}`}>{trade.side}</span></td><td>{trade.signalDate}</td><td>{trade.executionDate}</td><td>{money.format(trade.referencePrice)}</td><td>{money.format(trade.effectivePrice)}</td><td>{compact(trade.units)}</td><td>{money.format(trade.commission)}</td><td>{money.format(trade.cashAfter)}</td><td>{trade.reason}</td></tr>)}</tbody></table></div>}</section> }

function AssumptionStrip({ result }: { result: BacktestResponse }) { return <section className="assumption-strip" aria-label="Run assumptions"><div><span>Data fingerprint</span><code title={result.dataFingerprint}>{result.dataFingerprint.slice(0, 14)}…</code></div><div><span>Execution</span><b>Next available open</b></div><div><span>Signal price</span><b>Raw close</b></div><div><span>Cash yield</span><b>0%</b></div><div><span>End position</span><b>Mark to market</b></div></section> }

function Methodology() { return <><section className="page-heading"><div><span className="eyebrow">READ BEFORE INTERPRETING</span><h1>Methodology & limitations</h1><p>SignalLedger is designed to make assumptions difficult to miss.</p></div></section><div className="method-grid"><article className="panel"><h2>No look-ahead</h2><p>A close-based signal executes no earlier than the next available session open. Final-day signals remain unfilled.</p></article><article className="panel"><h2>Price return</h2><p>MVP uses coherent raw OHLC. It does not mix adjusted closes with raw opens and excludes distributions.</p></article><article className="panel"><h2>Index warning</h2><p>^NDX, ^IXIC, and ^GSPC are non-tradable price indices. Their fills are theoretical and are not historical Bid/Ask.</p></article><article className="panel"><h2>Costs and cash</h2><p>Commission and slippage apply on every side. Cash earns 0% until a validated point-in-time T-bill series is available.</p></article><article className="panel"><h2>Not financial advice</h2><p>Historical and simulated results are hypothetical, omit real-world effects, and do not predict future performance.</p></article><article className="panel"><h2>Reproducibility</h2><p>Each run stores an engine version, exact data fingerprint, normalized equity series, and auditable transactions.</p></article></div></> }

function Metric({ label, value, detail }: { label: string; value: string; detail: string }) { return <article className="metric"><span>{label}</span><strong>{value}</strong><small>{detail}</small></article> }
function Field({ label, children }: { label: string; children: React.ReactNode }) { return <label className="field"><span>{label}</span>{children}</label> }
function ChartPanel({ title, subtitle, children }: { title: string; subtitle: string; children: React.ReactNode }) { return <section className="panel chart-panel"><h2>{title}</h2><p className="muted">{subtitle}</p>{children}</section> }
function Status({ kind, children }: { kind: 'error' | 'warning'; children: React.ReactNode }) { return <div className={`status status-${kind}`} role={kind === 'error' ? 'alert' : 'note'}>{children}</div> }
function Empty({ title, children, action, onAction }: { title: string; children: React.ReactNode; action?: string; onAction?: () => void }) { return <section className="empty"><span>⌁</span><h2>{title}</h2><p>{children}</p>{action && <button className="secondary-button" onClick={onAction}>{action}</button>}</section> }
export default DashboardApp

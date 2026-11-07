import { useCallback, useEffect, useState } from 'react'
import { cancelMarketDataJob, getMarketDataStatus, refreshMarketData, startMarketDataJob, updateMarketDataSettings } from './api'
import type { MarketDataJob, MarketDataStatus } from './types'

const when = (value: string | null) => value ? new Date(value).toLocaleString() : '—'
const active = (job: MarketDataJob) => job.status === 'Pending' || job.status === 'Running'

/** Market-data provider management. All provider traffic happens on the server; this page only reads status and queues jobs. */
export function MarketDataPanel({ onDataChanged }: { onDataChanged: () => Promise<void> }) {
  const [status, setStatus] = useState<MarketDataStatus | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [refreshSymbol, setRefreshSymbol] = useState('QQQ')
  const [refreshFrom, setRefreshFrom] = useState('')
  const [refreshTo, setRefreshTo] = useState('')
  const [years, setYears] = useState<number | null>(null)

  const load = useCallback(async () => {
    try { setStatus(await getMarketDataStatus()); setError('') } catch (e) { setError((e as Error).message) }
  }, [])
  // Initial load is the external synchronization this effect owns.
  // oxlint-disable-next-line react/set-state-in-effect
  useEffect(() => { void load() }, [load])

  const running = status?.activeJobs.some(active) ?? false
  useEffect(() => {
    if (!running) return
    let alive = true
    const timer = window.setInterval(async () => {
      if (!alive) return
      try {
        const next = await getMarketDataStatus()
        if (!alive) return
        setStatus(next)
        if (!next.activeJobs.some(active)) await onDataChanged()
      } catch (e) { if (alive) setError((e as Error).message) }
    }, 1500)
    return () => { alive = false; window.clearInterval(timer) }
  }, [running, onDataChanged])

  async function act(action: () => Promise<unknown>, message: string) {
    setBusy(true); setError(''); setNotice('')
    try { await action(); setNotice(message); await load() } catch (e) { setError((e as Error).message) } finally { setBusy(false) }
  }

  if (!status) return <section className="panel market-data-panel" aria-busy="true"><h2>Market data provider</h2>{error ? <p className="status status-error" role="alert">{error}</p> : <p role="status">Loading provider status…</p>}</section>

  const s = status.settings
  const canChange = s.settingsChangesAllowed
  const canDownload = s.downloadOperationsAllowed && s.yahooEnabled
  const downloadReason = !s.downloadOperationsAllowed ? s.downloadLockReason : !s.yahooEnabled ? s.yahooDisabledReason : null
  const providerRows = status.instruments.filter(x => x.providerSupported)

  return <section className="panel market-data-panel" aria-labelledby="provider-title">
    <div className="section-title">
      <div><h2 id="provider-title">Market data provider</h2><p className="muted">Yahoo Chart (unofficial). Downloads run on the server and continue without this page open. Latest completed session: <b>{status.latestCompletedSession}</b>.</p></div>
      <button className="secondary-button" onClick={() => void load()}>Refresh status</button>
    </div>

    <div className="provider-state">
      <span className={s.yahooEnabled ? 'tag positive' : 'tag warning-tag'}>Yahoo {s.yahooEnabled ? 'enabled' : 'disabled'}</span>
      <span className={s.autoSyncEnabled ? 'tag positive' : 'tag'}>Auto-sync {s.autoSyncEnabled ? `every ${s.syncIntervalMinutes} min` : 'off'}</span>
      <span className="tag">History target: {s.historicalYears} years</span>
      {s.readOnlyMode && <span className="tag warning-tag">Read-only server</span>}
    </div>
    {s.yahooDisabledReason && <p className="inline-warning">⚠ {s.yahooDisabledReason}</p>}

    <div className="provider-controls">
      <label className="check-row"><input type="checkbox" checked={s.yahooEnabledRequested} disabled={!canChange || busy || (!s.yahooAuthorizedByServer && !s.yahooEnabledRequested)} onChange={e => void act(() => updateMarketDataSettings({ yahooEnabled: e.target.checked }), e.target.checked ? 'Yahoo provider enabled.' : 'Yahoo provider disabled; running downloads are being cancelled.')} /> Enable Yahoo provider</label>
      <label className="check-row"><input type="checkbox" checked={s.autoSyncRequested} disabled={!canChange || busy} onChange={e => void act(() => updateMarketDataSettings({ autoSyncEnabled: e.target.checked }), 'Automatic synchronization updated.')} /> Automatic synchronization</label>
      <label className="field compact-field"><span>History (years)</span><input type="number" min={1} max={s.maximumYears} value={years ?? s.historicalYears} disabled={!canChange || busy} onChange={e => setYears(e.target.valueAsNumber)} onBlur={() => { if (years != null && years !== s.historicalYears) void act(() => updateMarketDataSettings({ historicalYears: years }), 'Historical window updated.').then(() => setYears(null)) }} /></label>
    </div>
    {!canChange && <p className="muted lock-note">🔒 {s.settingsLockReason}</p>}

    {!status.hasAnyProviderData && <div className="empty onboarding"><span>⌁</span><h2>No historical market data available.</h2><p>Enable a data source and download historical prices, or import a CSV you are licensed to use. Once downloaded, prices are cached locally and only missing sessions are fetched later.</p>
      <button className="primary-button" disabled={!canDownload || busy} onClick={() => void act(() => startMarketDataJob('download'), 'Historical download queued for all instruments.')}>Download Historical Data</button>
      {downloadReason && <p className="muted lock-note">🔒 {downloadReason}</p>}</div>}

    <div className="table-scroll"><table>
      <thead><tr><th>Instrument</th><th>Cached range</th><th>Bars</th><th>Missing</th><th>Last success</th><th>Last attempt</th><th>State</th><th>Actions</th></tr></thead>
      <tbody>{providerRows.map(row => {
        const job = status.activeJobs.find(x => x.symbol === row.symbol && active(x))
        return <tr key={row.symbol}>
          <td><b>{row.symbol}</b><br /><small className="muted-text">{row.displayName}</small></td>
          <td>{row.earliestCachedDate ? `${row.earliestCachedDate} → ${row.latestCachedDate}` : 'Not cached'}</td>
          <td>{row.cachedBars.toLocaleString()}</td>
          <td>{row.missingSessions}{row.unavailableSessions > 0 && <small className="muted-text"> (+{row.unavailableSessions} unavailable)</small>}</td>
          <td>{when(row.lastSuccessfulSyncUtc)}</td>
          <td>{when(row.lastAttemptUtc)}{row.lastError && <><br /><small className="negative" title={row.lastError}>⚠ {row.lastError.slice(0, 80)}</small></>}</td>
          <td>{job ? <span className="tag">{job.status}</span> : row.upToDate ? <span className="tag positive">Up to date</span> : row.cachedBars === 0 ? <span className="tag">Empty</span> : <span className="tag warning-tag">{row.pendingRequests} range(s) missing</span>}</td>
          <td><div className="button-row">
            <button className="secondary-button small" disabled={!canDownload || busy || !!job} onClick={() => void act(() => startMarketDataJob(row.cachedBars === 0 ? 'download' : 'sync', [row.symbol]), `${row.symbol}: synchronization queued.`)}>{row.cachedBars === 0 ? 'Download' : 'Sync missing'}</button>
            <button className="secondary-button small" disabled={!canDownload || busy || !!job || row.cachedBars === 0} onClick={() => void act(() => startMarketDataJob('repair', [row.symbol]), `${row.symbol}: gap repair queued.`)}>Repair gaps</button>
          </div></td>
        </tr>
      })}</tbody>
    </table></div>
    {downloadReason && status.hasAnyProviderData && <p className="muted lock-note">🔒 Download controls unavailable: {downloadReason}</p>}

    <div className="provider-bulk button-row">
      <button className="primary-button" disabled={!canDownload || busy} onClick={() => void act(() => startMarketDataJob('sync'), 'Missing-session synchronization queued.')}>Sync Missing Data</button>
      <button className="secondary-button" disabled={!canDownload || busy} onClick={() => void act(() => startMarketDataJob('repair'), 'Gap repair queued.')}>Repair Historical Gaps</button>
    </div>

    <details className="refresh-box"><summary>Force refresh a date range</summary>
      <p className="muted">Re-downloads and overwrites cached bars only inside the selected range. Ordinary synchronization never re-downloads cached history.</p>
      <form className="form-grid" onSubmit={e => { e.preventDefault(); void act(() => refreshMarketData(refreshSymbol, refreshFrom, refreshTo), `${refreshSymbol}: refresh queued.`) }}>
        <label className="field"><span>Instrument</span><select value={refreshSymbol} onChange={e => setRefreshSymbol(e.target.value)}>{providerRows.map(x => <option key={x.symbol}>{x.symbol}</option>)}</select></label>
        <label className="field"><span>Refresh from</span><input type="date" required value={refreshFrom} onChange={e => setRefreshFrom(e.target.value)} /></label>
        <label className="field"><span>Refresh to</span><input type="date" required value={refreshTo} max={status.latestCompletedSession} onChange={e => setRefreshTo(e.target.value)} /></label>
        <div className="field"><span>&nbsp;</span><button className="secondary-button" disabled={!canDownload || busy || !refreshFrom || !refreshTo}>Queue refresh</button></div>
      </form>
    </details>

    {status.activeJobs.length > 0 && <div className="job-list" aria-live="polite"><h3>Background jobs</h3>{status.activeJobs.map(job => <article key={job.id}>
      <div><b>{job.type}</b> {job.symbol} · {job.status}{job.trigger !== 'Manual' && ` · ${job.trigger.toLowerCase()}`}<br /><small className="muted-text">{job.message}</small></div>
      <progress value={job.status === 'Running' ? job.completedSteps : 0} max={Math.max(1, job.totalSteps)} aria-label={`${job.symbol} ${job.type} progress`} />
      <small>{job.recordsAdded.toLocaleString()} added · {job.providerRequests} requests</small>
      <button className="secondary-button small" disabled={!s.downloadOperationsAllowed || job.cancellationRequested} onClick={() => void act(() => cancelMarketDataJob(job.id), 'Cancellation requested.')}>Cancel</button>
    </article>)}</div>}

    {notice && <p className="status status-warning" role="status">{notice}</p>}
    {error && <p className="status status-error" role="alert">{error}</p>}
  </section>
}

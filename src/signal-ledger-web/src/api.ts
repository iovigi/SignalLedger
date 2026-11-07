import type { BacktestResponse, EnqueueResponse, ExperimentResponse, ImportResult, Instrument, MarketDataJob, MarketDataSettings, MarketDataStatus } from './types'

async function read<T>(response: Response): Promise<T> {
  if (response.ok) return response.json() as Promise<T>
  const problem = await response.json().catch(() => null) as { title?: string; detail?: string } | null
  throw new Error(problem?.detail || problem?.title || `Request failed with HTTP ${response.status}`)
}

export async function getInstruments(signal?: AbortSignal) {
  return read<Instrument[]>(await fetch('/api/instruments', { signal }))
}

export async function runBacktest(payload: unknown, signal?: AbortSignal) {
  return read<BacktestResponse>(await fetch('/api/backtests', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload), signal }))
}

export async function importCsv(symbol: string, file: File, signal?: AbortSignal) {
  const body = new FormData(); body.append('file', file)
  return read<ImportResult>(await fetch(`/api/imports/csv?symbol=${encodeURIComponent(symbol)}`, { method: 'POST', body, signal }))
}

export async function createExperiment(payload: unknown, signal?: AbortSignal) {
  return read<ExperimentResponse>(await fetch('/api/experiments', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload), signal }))
}
export async function getExperiment(id: string, signal?: AbortSignal) { return read<ExperimentResponse>(await fetch(`/api/experiments/${id}`, { signal })) }
export async function cancelExperiment(id: string) { const response = await fetch(`/api/experiments/${id}/cancel`, { method: 'POST' }); if (!response.ok) throw new Error(`Cancel failed with HTTP ${response.status}`) }

// Market data: every provider request is made by the SignalLedger server, never by the browser.
const json = { 'Content-Type': 'application/json' }
export async function getMarketDataStatus(signal?: AbortSignal) { return read<MarketDataStatus>(await fetch('/api/market-data/status', { signal })) }
export async function updateMarketDataSettings(update: Partial<Pick<MarketDataSettings, 'historicalYears' | 'syncIntervalMinutes' | 'recentSessionReconciliationDays'>> & { yahooEnabled?: boolean; autoSyncEnabled?: boolean }) {
  return read<MarketDataSettings>(await fetch('/api/settings/market-data', { method: 'PUT', headers: json, body: JSON.stringify(update) }))
}
export async function startMarketDataJob(kind: 'download' | 'sync' | 'repair', symbols?: string[]) {
  return read<EnqueueResponse>(await fetch(`/api/market-data/${kind}`, { method: 'POST', headers: json, body: JSON.stringify(symbols?.length ? { symbols } : {}) }))
}
export async function refreshMarketData(symbol: string, from: string, to: string) {
  return read<EnqueueResponse>(await fetch('/api/market-data/refresh', { method: 'POST', headers: json, body: JSON.stringify({ symbol, from, to }) }))
}
export async function cancelMarketDataJob(id: string) { return read<MarketDataJob>(await fetch(`/api/market-data/jobs/${id}/cancel`, { method: 'POST' })) }

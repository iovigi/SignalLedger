import type { BacktestResponse, ExperimentResponse, ImportResult, Instrument } from './types'

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

export type AnnualReturn = { year: number; return: number }
export type PerformanceSummary = {
  finalEquity: number; totalReturn: number; cagr: number | null; maxDrawdown: number
  annualizedVolatility: number | null; sharpeRatio: number | null; transactions: number; roundTrips: number
  totalCommission: number; estimatedSlippageCost: number; tradingDays: number; daysInvested: number
  percentTimeInvested: number; annualReturns: AnnualReturn[]; worstCalendarYear: AnnualReturn | null; warnings: string[]
}
export type SeriesPoint = { date: string; close: number; indicator: number | null; strategyEquity: number; benchmarkEquity: number }
export type Trade = { signalDate: string; executionDate: string; side: 'Buy' | 'Sell'; referencePrice: number; slippagePerUnit: number; effectivePrice: number; units: number; notional: number; commission: number; cashAfter: number; equityAfter: number; reason: string }
export type BacktestResponse = { id: string; engineVersion: string; symbol: string; source: string; dataFingerprint: string; strategyName: string; startDate: string; endDate: string; strategy: PerformanceSummary; benchmark: PerformanceSummary; outperformanceVsBenchmark: number; series: SeriesPoint[]; trades: Trade[]; warnings: string[] }
export type SourceCoverage = { source: string; firstDate: string; lastDate: string; barCount: number; missingSessions: string[]; lastUpdatedUtc: string }
export type Instrument = { symbol: string; displayName: string; assetClass: string; currency: string; priceSeriesMeaning: string; tradable: boolean; warnings: string[]; sources: SourceCoverage[] }
export type ImportError = { row: number; field: string | null; message: string; value: string | null }
export type ImportResult = { importRunId: string; added: number; updated: number; rejected: number; errors: ImportError[]; firstDate: string | null; lastDate: string | null }
export type ExperimentCell = { period: number; commissionPercent: number; strategyReturn: number; benchmarkReturn: number; netOutperformance: number; windowLabel: string | null; isHoldout: boolean }
export type ExperimentResponse = { id: string; symbol: string; source: string; status: 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Cancelled'; completedCells: number; totalCells: number; cancellationRequested: boolean; error: string | null; selectedTrainingPeriod: number | null; cells: ExperimentCell[] }
export type JobStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Cancelled'
export type MarketDataSettings = {
  readOnlyMode: boolean; yahooAuthorizedByServer: boolean; yahooEnabledRequested: boolean; yahooEnabled: boolean; yahooDisabledReason: string | null
  autoSyncRequested: boolean; autoSyncEnabled: boolean; syncIntervalMinutes: number; historicalYears: number; recentSessionReconciliationDays: number
  requestTimeoutSeconds: number; maximumYears: number; settingsChangesAllowed: boolean; settingsLockReason: string | null
  downloadOperationsAllowed: boolean; downloadLockReason: string | null; valueSources: Record<string, string>
}
export type MarketDataJob = {
  id: string; type: 'Download' | 'Sync' | 'Repair' | 'Refresh'; trigger: 'Manual' | 'Automatic' | 'Recovery'; symbol: string; source: string
  requestedFrom: string | null; requestedTo: string | null; status: JobStatus; completedSteps: number; totalSteps: number; progress: number
  recordsAdded: number; recordsUpdated: number; providerRequests: number; retryCount: number; cancellationRequested: boolean
  message: string | null; error: string | null; createdAtUtc: string; startedAtUtc: string | null; completedAtUtc: string | null; notBeforeUtc: string | null
}
export type InstrumentSyncStatus = {
  symbol: string; displayName: string; source: string; providerSupported: boolean; earliestCachedDate: string | null; latestCachedDate: string | null
  cachedBars: number; missingSessions: number; unavailableSessions: number; pendingRequests: number; upToDate: boolean
  lastSuccessfulSyncUtc: string | null; lastAttemptUtc: string | null; lastError: string | null
}
export type MarketDataStatus = { settings: MarketDataSettings; latestCompletedSession: string; hasAnyProviderData: boolean; instruments: InstrumentSyncStatus[]; activeJobs: MarketDataJob[] }
export type EnqueueResponse = { jobs: MarketDataJob[]; duplicatesOf: string[] }

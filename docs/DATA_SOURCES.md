# Data sources and licensing

SignalLedger is CSV-first. It runs without an external provider and does not bundle downloaded market prices.

## Supported instruments

| Symbol | Meaning | Tradable? | Default return interpretation |
|---|---|---:|---|
| `^NDX` | NASDAQ-100 price index | No | Theoretical price-index proxy |
| `^IXIC` | NASDAQ Composite price index | No | Theoretical price-index proxy |
| `^GSPC` | S&P 500 price index | No | Theoretical price-index proxy |
| `QQQ` | Invesco QQQ ETF | Yes, subject to real market constraints | Raw-OHLC price return excluding distributions |

Daily Open, High, Low, and Close values are not Bid/Ask quotes. Index levels cannot be bought directly, so transaction fills derived from an index are hypothetical. QQQ is a tradeable proxy available from 1999, but raw-price testing is not a dividend-reinvested total-return test.

## CSV import

The default header mapping is `Date,Open,High,Low,Close,Adj Close,Volume`. `Date` must be ISO `yyyy-MM-dd`; OHLC fields use invariant decimal notation. Adjusted close and volume are optional. Custom header names can be mapped explicitly.

Imports validate every row. Invalid dates, non-positive prices, inconsistent high/low values, negative volume, duplicate dates, and malformed columns are rejected with row/field errors. Valid rows are transactionally upserted and every run is audited. Only import data you own or are licensed to use. Export does not create redistribution rights.

## Optional Yahoo Chart adapter

Yahoo's chart endpoint is unofficial and undocumented. Automated collection may be restricted by current Yahoo terms. SignalLedger does not claim that this endpoint is a supported public API.

The adapter is disabled by default and refuses network access unless both settings are true:

```json
{
  "SignalLedger": {
    "Yahoo": {
      "Enabled": true,
      "AuthorizedUseConfirmed": true
    }
  }
}
```

Setting these flags is a deployer attestation, not a grant of permission. Before enabling them, review current terms and obtain any required authorization, or replace the adapter with a licensed source. Public deployments should leave sync disabled or restrict the endpoint at IIS/network level. CI and tests use local fixtures and never require live Yahoo access.

Do not commit, publish, cache in releases, or redistribute prices downloaded from Yahoo unless you independently hold the necessary rights. The MIT license covers application source only.

## Adjusted data

MVP strategy signals and fills use a coherent raw OHLC series. An adjusted close must not be mixed with raw open fills: splits and dividends can create discontinuities and misleading results. A future adjusted mode must apply a validated adjustment factor to the complete OHLC series and describe dividend handling separately.

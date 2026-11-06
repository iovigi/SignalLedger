import './App.css'

const navItems = ['Dashboard', 'Data Manager', 'Strategy Explorer', 'Runs', 'Methodology']

function App() {
  return (
    <div className="app-shell">
      <header className="topbar">
        <a className="brand" href="/" aria-label="SignalLedger home"><span className="brand-mark" aria-hidden="true">SL</span><span>SignalLedger</span></a>
        <nav aria-label="Primary navigation">{navItems.map((item, index) => <a key={item} className={index === 0 ? 'active' : ''} href={index === 0 ? '/' : '#roadmap'}>{item}</a>)}</nav>
      </header>
      <main>
        <section className="hero-panel">
          <p className="eyebrow">OPEN-SOURCE MARKET BACKTESTING LAB</p>
          <h1>Backtests you can inspect,<br />not just admire.</h1>
          <p className="intro">Compare buy-and-hold with rule-based strategies using explicit execution timing, transparent costs, and an auditable trade ledger.</p>
          <div className="actions"><a className="primary" href="#roadmap">Start with synthetic data</a><a className="secondary" href="#methodology">Read the methodology</a></div>
        </section>
        <section className="status-grid" id="roadmap" aria-label="Project status">
          <article><span className="status-dot" aria-hidden="true" /><div><strong>Deterministic engine</strong><p>Next-session execution with no look-ahead bias.</p></div></article>
          <article><span className="status-dot" aria-hidden="true" /><div><strong>Auditable assumptions</strong><p>Fees, slippage, and price-series meaning stay visible.</p></div></article>
          <article><span className="status-dot" aria-hidden="true" /><div><strong>CSV-first data</strong><p>Works offline without redistributing market data.</p></div></article>
        </section>
        <aside className="notice" id="methodology"><strong>Foundation in progress</strong><span>The application shell is ready. Data import and the backtest engine are the next milestones.</span></aside>
      </main>
    </div>
  )
}

export default App

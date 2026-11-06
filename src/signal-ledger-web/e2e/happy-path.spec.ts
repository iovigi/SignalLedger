import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'

function syntheticCsv() {
  const rows = ['Date,Open,High,Low,Close,Volume']
  const start = new Date('2020-01-01T00:00:00Z')
  for (let i = 0; i < 80; i++) {
    const date = new Date(start); date.setUTCDate(start.getUTCDate() + i)
    const wave = Math.sin(i / 4) * 8; const close = 100 + i * .15 + wave; const open = close - Math.cos(i / 3)
    rows.push(`${date.toISOString().slice(0, 10)},${open.toFixed(4)},${(Math.max(open, close) + 1).toFixed(4)},${(Math.min(open, close) - 1).toFixed(4)},${close.toFixed(4)},${1000 + i}`)
  }
  return rows.join('\n')
}

test.describe.serial('browser workflow', () => {
  test('imports synthetic data and renders an auditable comparison', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('button', { name: 'Data Manager' }).click()
    await page.getByLabel('CSV file').setInputFiles({ name: 'synthetic.csv', mimeType: 'text/csv', buffer: Buffer.from(syntheticCsv()) })
    await page.getByRole('button', { name: 'Import CSV' }).click()
    await expect(page.getByText('Import completed')).toBeVisible()
    await expect(page.getByText(/80 added|80 updated/)).toBeVisible()

    await page.getByRole('button', { name: 'Dashboard' }).click()
    await page.getByLabel('From', { exact: true }).fill('2020-01-01')
    await page.getByLabel('To', { exact: true }).fill('2020-03-20')
    await page.getByLabel('Moving-average sessions').fill('5')
    const responsePromise = page.waitForResponse(response => response.url().endsWith('/api/backtests') && response.request().method() === 'POST')
    await page.getByRole('button', { name: 'Run comparison' }).click()
    const apiResult = await (await responsePromise).json()
    await expect(page.getByText('Strategy final equity')).toBeVisible()
    await expect(page.getByText(new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 2 }).format(apiResult.strategy.finalEquity), { exact: true })).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Equity curves' })).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Transaction journal' })).toBeVisible()
    await expect(page.locator('tbody tr').first()).toBeVisible()
    await expect(page.locator('tbody tr').first()).toContainText(apiResult.trades[0].executionDate)

    const accessibility = await new AxeBuilder({ page }).analyze()
    expect(accessibility.violations.filter(x => x.impact === 'critical' || x.impact === 'serious')).toEqual([])
    await page.screenshot({ path: '../../docs/SCREENSHOTS/dashboard.png', fullPage: true })
  })

  test('shows a representative API failure state', async ({ page }) => {
    await page.route('**/api/backtests', route => route.fulfill({ status: 400, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Invalid backtest request', detail: 'Representative validation failure.' }) }))
    await page.goto('/')
    await page.getByRole('button', { name: 'Run comparison' }).click()
    await expect(page.getByRole('alert')).toContainText('Representative validation failure')
  })

  test('remains usable at a mobile viewport', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    await page.goto('/')
    await expect(page.getByRole('heading', { name: 'Strategy dashboard' })).toBeVisible()
    await page.getByRole('button', { name: 'Methodology' }).click()
    await expect(page.getByRole('heading', { name: 'Methodology & limitations' })).toBeVisible()
    const width = await page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, client: document.documentElement.clientWidth }))
    expect(width.scroll).toBeLessThanOrEqual(width.client + 1)
  })
})

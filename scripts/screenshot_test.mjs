import { chromium } from 'playwright';

const BASE = 'https://ai.itbank.co.jp';
const pages = [
  { name: 'purchase-orders', path: '/purchase-orders' },
  { name: 'vendor-invoices', path: '/vendor-invoices' },
  { name: 'materials', path: '/materials' },
  { name: 'warehouses', path: '/warehouses' },
  { name: 'bins', path: '/bins' },
  { name: 'inventory-movements', path: '/inventory-movements' },
  { name: 'inventory-counts', path: '/inventory-counts' },
  { name: 'inventory-ledger', path: '/inventory-ledger' },
  { name: 'roles', path: '/roles' },
];

(async () => {
  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();

  await page.goto(BASE + '/login', { waitUntil: 'networkidle', timeout: 15000 });
  await page.waitForTimeout(2000);

  // All three fields are el-input (inner input elements)
  const inputs = await page.locator('.el-input__inner').all();
  console.log(`Found ${inputs.length} inputs`);

  // inputs[0] = companyCode (already "JP01"), inputs[1] = employeeCode, inputs[2] = password
  if (inputs.length >= 3) {
    await inputs[1].fill('miyano');
    await inputs[2].fill('itbank2026');
  }

  await page.screenshot({ path: 'D:/yanxia/scripts/screenshots/login-filled.png' });

  // Click login
  await page.locator('button:has-text("ログイン")').click();
  await page.waitForTimeout(5000);
  console.log('After login URL:', page.url());

  // Take screenshots
  for (const p of pages) {
    try {
      await page.goto(BASE + p.path, { waitUntil: 'networkidle', timeout: 15000 });
      await page.waitForTimeout(2500);
      await page.screenshot({ path: `D:/yanxia/scripts/screenshots/${p.name}.png`, fullPage: true });
      console.log(`OK: ${p.name}`);
    } catch (e) {
      console.error(`FAIL: ${p.name} - ${e.message}`);
      try { await page.screenshot({ path: `D:/yanxia/scripts/screenshots/${p.name}.png`, fullPage: true }); } catch {}
    }
  }

  await browser.close();
})();

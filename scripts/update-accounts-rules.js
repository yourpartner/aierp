const http = require('http');

const BASE = 'http://localhost:5179';
const COMPANY = 'JP01';

function request(method, path, body) {
  return new Promise((resolve, reject) => {
    const url = new URL(BASE + path);
    const data = body ? Buffer.from(JSON.stringify(body)) : null;
    const req = http.request({
      hostname: url.hostname,
      port: url.port,
      path: url.pathname,
      method,
      headers: {
        'x-company-code': COMPANY,
        'Content-Type': 'application/json',
        ...(data ? { 'Content-Length': data.length } : {})
      }
    }, res => {
      const chunks = [];
      res.on('data', c => chunks.push(c));
      res.on('end', () => {
        const text = Buffer.concat(chunks).toString();
        if (res.statusCode >= 200 && res.statusCode < 300) {
          try { resolve(text ? JSON.parse(text) : null); } catch { resolve(null); }
        } else {
          reject(new Error(`HTTP ${res.statusCode}: ${text}`));
        }
      });
    });
    req.on('error', reject);
    if (data) req.write(data);
    req.end();
  });
}

function buildRulesForAccount(a) {
  const code = a.account_code || a.payload?.code || '';
  const name = (a.payload?.name || '').toString();
  // defaults
  let openItem = false;
  let openItemBaseline = 'NONE';
  const fieldRules = {
    customerId: 'hidden',
    vendorId: 'hidden',
    employeeId: 'optional',
    departmentId: 'optional',
    paymentDate: 'optional'
  };

  const c = code;
  const is = (s) => name.includes(s);

  if (c === '1100' || is('売掛')) { // AR
    openItem = true; openItemBaseline = 'CUSTOMER';
    fieldRules.customerId = 'required';
    fieldRules.vendorId = 'hidden';
    fieldRules.paymentDate = 'optional';
  } else if (c === '2000' || is('買掛')) { // AP
    openItem = true; openItemBaseline = 'VENDOR';
    fieldRules.vendorId = 'required';
    fieldRules.customerId = 'hidden';
    fieldRules.paymentDate = 'required';
  } else if (c === '2100' || is('未払金')) {
    openItem = true; openItemBaseline = 'VENDOR';
    fieldRules.vendorId = 'required';
    fieldRules.customerId = 'hidden';
    fieldRules.paymentDate = 'required';
  } else if (c === '2200' || is('未払費用')) {
    openItem = true; openItemBaseline = 'VENDOR';
    fieldRules.vendorId = 'required';
    fieldRules.customerId = 'hidden';
    fieldRules.paymentDate = 'optional';
  } else if (c === '1200' || is('前払')) {
    openItem = true; openItemBaseline = 'VENDOR';
    fieldRules.vendorId = 'optional';
    fieldRules.customerId = 'hidden';
    fieldRules.paymentDate = 'optional';
  } else {
    // Cash/Bank, Inventory, Fixed assets, Revenues/Expenses -> no open item
    openItem = false; openItemBaseline = 'NONE';
    fieldRules.customerId = 'optional';
    fieldRules.vendorId = 'optional';
    fieldRules.paymentDate = 'optional';
  }

  return { openItem, openItemBaseline, fieldRules };
}

async function main() {
  const list = await request('POST', '/objects/account/search', { where: [], page: 1, pageSize: 500 });
  const rows = list?.data || [];
  for (const r of rows) {
    const payload = r.payload || {};
    const rules = buildRulesForAccount(r);
    const newPayload = { ...payload, ...rules };
    await request('PUT', `/objects/account/${r.id}`, { payload: newPayload });
  }
  console.log('OK');
}

main().catch(err => { console.error(err.message || err); process.exit(1); });



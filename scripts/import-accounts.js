// Import demo accounts into backend via HTTP
const fs = require('fs');
const http = require('http');

const BASE = 'http://localhost:5179';
const COMPANY = 'JP01';
const PATH = '/objects/account';

function postJson(path, body, headers = {}) {
  return new Promise((resolve, reject) => {
    const data = Buffer.from(JSON.stringify(body));
    const url = new URL(BASE + path);
    const req = http.request({
      hostname: url.hostname,
      port: url.port,
      path: url.pathname,
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Content-Length': data.length,
        'x-company-code': COMPANY,
        ...headers
      }
    }, res => {
      const chunks = [];
      res.on('data', c => chunks.push(c));
      res.on('end', () => {
        if (res.statusCode >= 200 && res.statusCode < 300) resolve();
        else reject(new Error(`HTTP ${res.statusCode}: ${Buffer.concat(chunks).toString()}`));
      });
    });
    req.on('error', reject);
    req.write(data);
    req.end();
  });
}

async function main() {
  const raw = fs.readFileSync('seed/accounts.json', 'utf8');
  const items = JSON.parse(raw);
  for (const it of items) {
    const body = { payload: { code: it.code, name: it.name, category: it.category, openItem: !!it.openItem } };
    await postJson(PATH, body);
  }
  console.log('OK');
}

main().catch(err => {
  console.error(err.message || err);
  process.exit(1);
});



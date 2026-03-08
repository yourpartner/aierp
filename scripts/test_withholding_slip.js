const https = require('https');

const API_BASE = 'yanxia-api.azurewebsites.net';
let TOKEN = '';

function req(method, path, body) {
  return new Promise((resolve, reject) => {
    const data = body ? JSON.stringify(body) : null;
    const options = {
      hostname: API_BASE,
      port: 443,
      path: path,
      method: method,
      headers: {
        'Content-Type': 'application/json',
        ...(data ? { 'Content-Length': Buffer.byteLength(data) } : {}),
        ...(TOKEN ? { 'Authorization': 'Bearer ' + TOKEN, 'x-company-code': 'JP01' } : {}),
      },
    };
    const r = https.request(options, (res) => {
      let b = '';
      res.on('data', c => (b += c));
      res.on('end', () => {
        try { resolve({ status: res.statusCode, data: JSON.parse(b) }); }
        catch { resolve({ status: res.statusCode, data: b }); }
      });
    });
    r.on('error', reject);
    if (data) r.write(data);
    r.end();
  });
}

function sep(title) {
  console.log('\n' + '='.repeat(60));
  console.log(`  ${title}`);
  console.log('='.repeat(60));
}

async function main() {
  // Step 1: Login
  sep('Step 1: ログイン');
  const login = await req('POST', '/auth/login', { companyCode: 'JP01', employeeCode: 'admin', password: 'test1234' });
  if (login.status !== 200) {
    console.error('Login failed:', login.status, login.data);
    return;
  }
  TOKEN = login.data.token;
  console.log('Login OK');

  // Step 2: Get employees with payroll data for 2025
  sep('Step 2: 2025年の従業員一覧を取得');
  const empResp = await req('GET', '/payroll/withholding-slip/employees?year=2025');
  console.log('Status:', empResp.status);
  if (empResp.status !== 200) {
    console.error('Failed to get employees:', empResp.data);
    return;
  }
  const employees = empResp.data;
  console.log(`対象従業員数: ${employees.length}`);
  employees.forEach(e => console.log(`  - ${e.employeeCode}: ${e.name} (${e.department})`));

  if (employees.length === 0) {
    console.log('給与データがありません。テスト終了。');
    return;
  }

  // Step 3: Check existing PDFs
  const empCodes = employees.map(e => e.employeeCode);
  sep('Step 3: 既存PDF確認');
  const checkResp = await req('GET', '/payroll/withholding-slip/check-existing?year=2025&employeeCodes=' + empCodes.join(','));
  console.log('Status:', checkResp.status);
  console.log('既存PDF:', JSON.stringify(checkResp.data, null, 2));

  // Step 4: Generate for first 3 employees (or all if less)
  const testCodes = empCodes.slice(0, 3);
  sep(`Step 4: PDF生成テスト (${testCodes.length}名)`);
  console.log('対象:', testCodes.join(', '));
  
  const genResp = await req('POST', '/payroll/withholding-slip/generate', {
    year: '2025',
    employeeCodes: testCodes,
    overwrite: true,
  });
  console.log('Status:', genResp.status);
  if (genResp.status === 200) {
    console.log(`作成結果 (total: ${genResp.data.total}):`);
    genResp.data.results.forEach(r => {
      console.log(`  ${r.employeeCode} (${r.name}): ${r.status}${r.reason ? ' - ' + r.reason : ''}`);
      if (r.url) console.log(`    URL: ${r.url.substring(0, 100)}...`);
    });
  } else {
    console.error('Generation failed:', JSON.stringify(genResp.data, null, 2));
  }

  // Step 5: Generate for ALL employees
  sep('Step 5: 全従業員のPDF生成');
  const genAllResp = await req('POST', '/payroll/withholding-slip/generate', {
    year: '2025',
    employeeCodes: empCodes,
    overwrite: true,
  });
  console.log('Status:', genAllResp.status);
  if (genAllResp.status === 200) {
    console.log(`作成結果 (total: ${genAllResp.data.total}):`);
    let successCount = 0;
    genAllResp.data.results.forEach(r => {
      if (r.status === 'success') successCount++;
      console.log(`  ${r.employeeCode} (${r.name}): ${r.status}${r.reason ? ' - ' + r.reason : ''}`);
    });
    console.log(`\n成功: ${successCount}/${genAllResp.data.total}`);
  } else {
    console.error('Generation failed:', JSON.stringify(genAllResp.data, null, 2));
  }

  // Step 6: Verify no-overwrite behavior
  sep('Step 6: 上書き拒否テスト');
  const noOverwriteResp = await req('POST', '/payroll/withholding-slip/generate', {
    year: '2025',
    employeeCodes: testCodes,
    overwrite: false,
  });
  console.log('Status:', noOverwriteResp.status);
  if (noOverwriteResp.status === 200) {
    noOverwriteResp.data.results.forEach(r => {
      console.log(`  ${r.employeeCode}: ${r.status}${r.reason ? ' - ' + r.reason : ''}`);
    });
  }

  sep('テスト完了');
}

main().catch(e => console.error('Error:', e));

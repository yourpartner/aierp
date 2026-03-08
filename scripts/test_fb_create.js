const https = require('https');
const API_BASE = 'yanxia-api.azurewebsites.net';
let TOKEN = '';

function req(m, p, b) {
  return new Promise((res, rej) => {
    const d = b ? JSON.stringify(b) : null;
    const o = {
      hostname: API_BASE, port: 443, path: p, method: m,
      headers: {
        'Content-Type': 'application/json',
        ...(d ? { 'Content-Length': Buffer.byteLength(d) } : {}),
        ...(TOKEN ? { 'Authorization': 'Bearer ' + TOKEN, 'x-company-code': 'JP01' } : {}),
      },
    };
    const r = https.request(o, rs => {
      let b = '';
      rs.on('data', c => b += c);
      rs.on('end', () => {
        try { res({ status: rs.statusCode, data: JSON.parse(b) }); }
        catch { res({ status: rs.statusCode, data: b }); }
      });
    });
    r.on('error', rej);
    if (d) r.write(d);
    r.end();
  });
}

(async () => {
  const login = await req('POST', '/auth/login', { companyCode: 'JP01', employeeCode: 'admin', password: 'test1234' });
  TOKEN = login.data.token;
  console.log('Login OK');

  const debts = await req('POST', '/fb-payment/pending-debts', { accountCodes: ['312'] });
  const all = debts.data?.data || [];
  const testDebts = all.filter(d => (d.headerText || '').indexOf('FBテスト') >= 0);
  console.log('Test debts:', testDebts.length);

  if (testDebts.length === 0) {
    console.log('No test debts found, using first 3 from all', all.length);
  }

  const source = testDebts.length > 0 ? testDebts.slice(0, 3) : all.slice(0, 3);
  const items = source.map(d => ({
    voucherId: d.voucherId,
    voucherNo: d.voucherNo,
    lineNo: d.lineNo,
    amount: d.residualAmount || d.amount,
    bankCode: d.bankCode || '',
    bankName: '',
    branchCode: d.branchCode || '',
    branchName: '',
    depositType: d.depositType || '1',
    accountNumber: d.accountNumber || '',
    accountHolder: d.accountHolder || d.vendorName || '',
  }));

  console.log('Items:', JSON.stringify(items, null, 2));

  const createResp = await req('POST', '/fb-payment/create', {
    paymentDate: '2026-03-06',
    bankCode: '131',
    bankName: 'PayPay',
    branchCode: '005',
    branchName: '',
    depositType: '1',
    accountNumber: '4875430',
    accountHolder: 'ITBANK',
    items
  });
  console.log('Create status:', createResp.status);
  console.log('Response:', JSON.stringify(createResp.data, null, 2));

  if (createResp.status === 200 && createResp.data?.id) {
    console.log('\nDownload test:');
    const dlResp = await req('GET', '/fb-payment/download/' + createResp.data.id);
    console.log('Download status:', dlResp.status);
    console.log('File content preview:', typeof dlResp.data === 'string' ? dlResp.data.substring(0, 200) : JSON.stringify(dlResp.data).substring(0, 200));
  }
})();

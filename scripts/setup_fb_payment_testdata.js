const https = require('https');
const fs = require('fs');
const API_BASE = 'yanxia-api.azurewebsites.net';
let TOKEN = '';

function req(method, path, body) {
  return new Promise((resolve, reject) => {
    const data = body ? JSON.stringify(body) : null;
    const options = {
      hostname: API_BASE, port: 443, path,
      method,
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

function sep(title) { console.log('\n' + '='.repeat(60) + '\n  ' + title + '\n' + '='.repeat(60)); }

async function main() {
  const mode = process.argv[2] || 'create';
  
  sep('ログイン');
  const login = await req('POST', '/auth/login', { companyCode: 'JP01', employeeCode: 'admin', password: 'test1234' });
  if (login.status !== 200) { console.error('Login failed:', login.data); return; }
  TOKEN = login.data.token;
  console.log('Login OK');

  if (mode === 'create') await runCreate();
  else if (mode === 'cleanup') await runCleanup();
}

async function runCreate() {
  const record = { vendorsUpdated: [], voucherIds: [] };

  // Get vendors
  sep('Step 1: 取引先を取得');
  const bpResp = await req('POST', '/objects/businesspartner/search', { page: 1, pageSize: 50, filters: {} });
  const bps = bpResp.data?.data || [];
  
  const vendorBank = [
    { bankCode: '0001', bankName: 'ﾐｽﾞﾎ', branchCode: '001', branchName: 'ﾏﾙﾉｳﾁ', accountNumber: '1234567', depositType: '1' },
    { bankCode: '0005', bankName: 'ﾐﾂﾋﾞｼUFJ', branchCode: '010', branchName: 'ﾎﾝﾃﾝ', accountNumber: '2345678', depositType: '1' },
    { bankCode: '0009', bankName: 'ﾐﾂｲｽﾐﾄﾓ', branchCode: '210', branchName: 'ｼﾝｼﾞｭｸ', accountNumber: '3456789', depositType: '1' },
    { bankCode: '0010', bankName: 'ﾘｿﾅ', branchCode: '304', branchName: 'ｵｵｻｶ', accountNumber: '4567890', depositType: '2' },
  ];

  const targetVendors = bps.slice(0, 4);
  sep('Step 2: 取引先に銀行口座を設定');
  for (let i = 0; i < targetVendors.length; i++) {
    const v = targetVendors[i];
    const p = v.payload || {};
    const code = p.code || p.partnerCode || v.partner_code;
    const bank = vendorBank[i];
    const holderKana = (p.nameKana || p.name || '').replace(/[^ｦ-ﾟ\u30A0-\u30FFa-zA-Z0-9\s()（）]/g, '').substring(0, 30) || `ﾍﾞﾝﾀﾞ${i+1}`;
    
    const originalBankAccounts = p.bankAccounts ? JSON.parse(JSON.stringify(p.bankAccounts)) : null;
    
    const updatedPayload = { ...p, bankAccounts: [{ ...bank, accountHolder: holderKana }] };
    const updateResp = await req('PUT', `/objects/businesspartner/${v.id}`, { payload: updatedPayload });
    const ok = updateResp.status === 200;
    console.log(`  ${code} (${p.name}): ${ok ? 'OK' : 'FAIL'} - ${bank.bankCode}/${bank.branchCode}/${bank.accountNumber}/${holderKana}`);
    if (ok) {
      record.vendorsUpdated.push({ id: v.id, code, originalBankAccounts });
    }
  }
  const vendorCodes = targetVendors.map(v => v.payload?.code || v.payload?.partnerCode || v.partner_code);

  sep('Step 3: テスト伝票を作成（買掛金312, 未払金314）');
  const testVouchers = [
    { vendor: vendorCodes[0], crAcct: '312', drAcct: '713', amount: 330000, desc: '外注費 - ソフトウェア開発', dueDate: '2026-03-15', postDate: '2026-02-10' },
    { vendor: vendorCodes[1], crAcct: '312', drAcct: '865', amount: 550000, desc: '業務委託費 - システム保守', dueDate: '2026-03-20', postDate: '2026-02-15' },
    { vendor: vendorCodes[2], crAcct: '312', drAcct: '868', amount: 220000, desc: '顧問料 - 法務コンサル', dueDate: '2026-03-25', postDate: '2026-02-20' },
    { vendor: vendorCodes[3], crAcct: '314', drAcct: '852', amount: 88000, desc: '消耗品費 - オフィス家具', dueDate: '2026-03-31', postDate: '2026-02-25' },
    { vendor: vendorCodes[0], crAcct: '312', drAcct: '857', amount: 1100000, desc: '広告宣伝 - オンライン広告費', dueDate: '2026-04-10', postDate: '2026-03-01' },
    { vendor: vendorCodes[1], crAcct: '314', drAcct: '846', amount: 165000, desc: '賃借料 - サーバーホスティング', dueDate: '2026-03-15', postDate: '2026-02-28' },
    { vendor: vendorCodes[2], crAcct: '312', drAcct: '842', amount: 75000, desc: '旅費交通費 - 出張手配', dueDate: '2026-03-20', postDate: '2026-02-18' },
  ];

  for (const tv of testVouchers) {
    if (!tv.vendor) { console.log('  SKIP: vendor code missing'); continue; }
    const payload = {
      header: { companyCode: 'JP01', postingDate: tv.postDate, voucherType: 'AP', currency: 'JPY', summary: `【FBテスト】${tv.desc}` },
      lines: [
        { lineNo: 1, accountCode: tv.drAcct, drcr: 'DR', amount: tv.amount, description: tv.desc },
        { lineNo: 2, accountCode: tv.crAcct, drcr: 'CR', amount: tv.amount, description: `${tv.desc}`, vendorId: tv.vendor, dueDate: tv.dueDate, paymentDate: tv.dueDate },
      ]
    };
    const resp = await req('POST', '/objects/voucher', { payload });
    if (resp.status === 200) {
      const d = resp.data;
      const vid = d.id;
      const vno = d.payload?.header?.voucherNo || d.voucher_no || '?';
      record.voucherIds.push(vid);
      console.log(`  OK: ${vno} | ${tv.crAcct} | ${tv.vendor} | ¥${tv.amount.toLocaleString()} | 期日:${tv.dueDate}`);
    } else {
      console.error(`  FAIL (${resp.status}): ${JSON.stringify(resp.data).substring(0, 300)}`);
    }
  }

  sep('Step 4: クリーンアップ用データ保存');
  fs.writeFileSync('scripts/_fb_test_record.json', JSON.stringify(record, null, 2));
  console.log(`保存: scripts/_fb_test_record.json`);
  console.log(`  伝票数: ${record.voucherIds.length}`);
  console.log(`  更新取引先数: ${record.vendorsUpdated.length}`);

  sep('Step 5: 作成した未払い債務を確認');
  const debtsResp = await req('POST', '/fb-payment/pending-debts', { accountCodes: ['312', '314'] });
  const debts = debtsResp.data?.data || [];
  const testDebts = debts.filter(d => d.headerText?.includes('FBテスト'));
  console.log(`買掛金(312)/未払金(314)の未払い債務: 全${debts.length}件, テスト分: ${testDebts.length}件`);
  testDebts.forEach(d => {
    console.log(`  ${d.voucherNo} L${d.lineNo}: ${d.accountCode} ¥${Number(d.residualAmount||d.amount).toLocaleString()} | ${d.vendorName||'-'} | 銀行:${d.bankCode||'-'}/${d.accountNumber||'-'} | 期日:${d.dueDate||'-'}`);
  });

  sep('完了');
  console.log('テストデータの準備が完了しました。');
  console.log('画面で確認後、以下のコマンドでクリーンアップできます:');
  console.log('  node scripts/setup_fb_payment_testdata.js cleanup');
}

async function runCleanup() {
  let record;
  try {
    record = JSON.parse(fs.readFileSync('scripts/_fb_test_record.json', 'utf8'));
  } catch {
    console.error('クリーンアップ用ファイルが見つかりません: scripts/_fb_test_record.json');
    return;
  }

  sep('Step 1: テスト伝票を削除');
  for (const id of record.voucherIds) {
    const resp = await req('DELETE', `/objects/voucher/${id}`, null);
    console.log(`  ${id}: ${resp.status === 200 ? 'OK' : 'FAIL (' + resp.status + ')'}`);
  }

  sep('Step 2: テストFBファイルを削除');
  const filesResp = await req('POST', '/fb-payment/files', { page: 1, pageSize: 100 });
  const allFiles = filesResp.data?.data || [];
  for (const f of allFiles) {
    const resp = await req('DELETE', `/fb-payment/files/${f.id}`, null);
    console.log(`  ${f.fileName} (${f.id}): ${resp.status === 200 ? 'OK' : `FAIL (${resp.status}) - manual deletion may be needed`}`);
  }

  sep('Step 3: 取引先の銀行口座情報を復元');
  for (const vu of record.vendorsUpdated) {
    const bpResp = await req('POST', '/objects/businesspartner/search', { page: 1, pageSize: 1, where: [{ field: 'id', op: 'eq', value: vu.id }] });
    const bp = (bpResp.data?.data || [])[0];
    if (!bp) { console.log(`  ${vu.code}: not found, skip`); continue; }
    const p = bp.payload || {};
    if (vu.originalBankAccounts) {
      p.bankAccounts = vu.originalBankAccounts;
    } else {
      delete p.bankAccounts;
    }
    const updateResp = await req('PUT', `/objects/businesspartner/${vu.id}`, { payload: p });
    console.log(`  ${vu.code}: ${updateResp.status === 200 ? 'OK (銀行口座情報を復元)' : 'FAIL'}`);
  }

  try { fs.unlinkSync('scripts/_fb_test_record.json'); } catch {}
  sep('クリーンアップ完了');
}

main().catch(e => console.error('Error:', e));

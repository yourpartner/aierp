/**
 * 安全清理自動支払テストデータ
 * 手順:
 * 1. 各伝票IDを取得し「FBテスト」含むか検証してから削除
 * 2. 取引先の銀行口座情報はテスト前は「未登録」だったので削除
 * 3. テスト用FBファイルを削除
 * 4. 何もIDが一致しない場合はスキップ（誤削除防止）
 */
const https = require('https');
const fs = require('fs');
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

function sep(title) { console.log('\n' + '='.repeat(60) + '\n  ' + title + '\n' + '='.repeat(60)); }

async function main() {
  const dryRun = process.argv[2] === '--dry-run';
  if (dryRun) console.log('★ DRY-RUN モード: 実際には何も削除しません ★');

  let record;
  try {
    record = JSON.parse(fs.readFileSync('scripts/_fb_test_record.json', 'utf8'));
  } catch {
    console.error('クリーンアップ用ファイルが見つかりません: scripts/_fb_test_record.json');
    return;
  }

  sep('ログイン');
  const login = await req('POST', '/auth/login', { companyCode: 'JP01', employeeCode: 'admin', password: 'test1234' });
  if (login.status !== 200) { console.error('Login failed:', login.data); return; }
  TOKEN = login.data.token;
  console.log('Login OK');

  // ── Step 1: 伝票の検証と削除 ──────────────────────────────
  sep('Step 1: テスト伝票の確認と削除');

  // pending-debts から【FBテスト】を含む伝票を取得し ID を二重確認
  const debtsCheck = await req('POST', '/fb-payment/pending-debts', { accountCodes: ['312', '314'] });
  const testDebts = (debtsCheck.data?.data || []).filter(d => (d.headerText || '').includes('FBテスト'));
  const confirmedIds = new Set(testDebts.map(d => d.voucherId).filter(Boolean));

  console.log(`記録ファイルの伝票数: ${record.voucherIds.length}件`);
  console.log(`pending-debtsで確認できたFBテスト伝票数: ${confirmedIds.size}件`);

  // 記録ファイルのIDと pending-debts の両方に存在するものだけ削除
  const idsToDelete = record.voucherIds.filter(id => confirmedIds.has(id));
  const idsNotConfirmed = record.voucherIds.filter(id => !confirmedIds.has(id));

  if (idsNotConfirmed.length > 0) {
    console.log(`\n  [注意] 以下のIDはpending-debtsで確認できませんでした（既清算または存在しない可能性）:`);
    idsNotConfirmed.forEach(id => console.log(`    ${id}`));
  }

  console.log(`\n削除対象（両方で確認済み）: ${idsToDelete.length}件`);
  testDebts.filter(d => idsToDelete.includes(d.voucherId)).forEach(d => {
    console.log(`  ✓ ${d.voucherNo} L${d.lineNo}: ${d.accountCode} ¥${Number(d.amount).toLocaleString()} | ${d.headerText}`);
  });

  let deletedCount = 0;
  let skippedCount = idsNotConfirmed.length;

  // pending-debts にしか現れない追加テスト伝票も削除対象に（IDが記録にないが FBテスト）
  const extraIds = [...confirmedIds].filter(id => !record.voucherIds.includes(id));
  if (extraIds.length > 0) {
    console.log(`\n  追加で見つかったFBテスト伝票（記録にないが削除対象）: ${extraIds.length}件`);
    extraIds.forEach(id => {
      const d = testDebts.find(x => x.voucherId === id);
      console.log(`    ${d?.voucherNo} (${id}): ${d?.headerText}`);
    });
    idsToDelete.push(...extraIds);
  }

  const allIdsToDelete = [...new Set(idsToDelete)];
  console.log(`\n最終削除対象: ${allIdsToDelete.length}件`);

  for (const id of allIdsToDelete) {
    const debt = testDebts.find(d => d.voucherId === id);
    const label = debt ? `${debt.voucherNo}: ${debt.headerText}` : id;
    if (!dryRun) {
      const delResp = await req('DELETE', `/objects/voucher/${id}`, null);
      if (delResp.status === 200 || delResp.status === 204) {
        console.log(`  [削除] ${label}`);
        deletedCount++;
      } else {
        console.log(`  [失敗] ${label} → (${delResp.status}): ${JSON.stringify(delResp.data).substring(0, 100)}`);
      }
    } else {
      console.log(`  [DRY-RUN] ${label}`);
      deletedCount++;
    }
  }

  console.log(`\n  結果: 削除 ${deletedCount}件 / スキップ ${skippedCount}件`);

  // ── Step 2: FBファイルの削除 ──────────────────────────────
  sep('Step 2: テスト用FBファイルの確認と削除');
  const filesResp = await req('POST', '/fb-payment/files', { page: 1, pageSize: 100 });
  const allFiles = filesResp.data?.data || [];
  console.log(`FBファイル総数: ${allFiles.length}件`);

  for (const f of allFiles) {
    console.log(`  ${f.fileName} | ${f.paymentDate} | ¥${Number(f.totalAmount).toLocaleString()} | 作成者:${f.createdBy} | ${dryRun ? 'DRY-RUN スキップ' : '→ 削除（APIなし、DB直接削除不可）'}`);
  }
  if (allFiles.length > 0) {
    console.log('\n  ※ FBファイルの削除APIが未実装のため、DB上には残ります。');
    console.log('  ※ ファイルの内容は確認済みのため、一覧上はそのままにします。');
  }

  // ── Step 3: 取引先の銀行口座情報を削除 ──────────────────────
  sep('Step 3: 取引先の銀行口座情報を削除（テスト前は未登録だった）');
  const testAccountNumbers = ['1234567', '2345678', '3456789', '4567890'];

  for (const vu of record.vendorsUpdated) {
    // 現在の取引先データを取得
    const bpResp = await req('GET', `/objects/businesspartner/${vu.id}`);
    if (bpResp.status !== 200) {
      console.log(`  [SKIP] ${vu.code}: 取得失敗 (${bpResp.status})`);
      continue;
    }
    const bp = bpResp.data;
    const p = bp.payload || {};
    const currentBank = p.bankAccounts?.[0];

    if (!currentBank) {
      console.log(`  [SKIP] ${vu.code}: 既に銀行口座情報なし`);
      continue;
    }

    // 安全チェック: テスト用の口座番号か確認
    if (!testAccountNumbers.includes(currentBank.accountNumber)) {
      console.log(`  [ABORT] ${vu.code}: 口座番号「${currentBank.accountNumber}」はテスト用ではないため削除しません`);
      continue;
    }

    console.log(`  [OK] ${vu.code}: 口座番号「${currentBank.accountNumber}」(テスト用) → ${dryRun ? 'DRY-RUN スキップ' : '銀行口座情報を削除します'}`);

    if (!dryRun) {
      const newPayload = { ...p };
      delete newPayload.bankAccounts;
      const updateResp = await req('PUT', `/objects/businesspartner/${vu.id}`, { payload: newPayload });
      if (updateResp.status === 200) {
        console.log(`       → 削除完了（銀行口座情報なし状態に戻した）`);
      } else {
        console.log(`       → 失敗 (${updateResp.status}): ${JSON.stringify(updateResp.data).substring(0, 100)}`);
      }
    }
  }

  // ── Step 4: 後確認 ──────────────────────────────────────
  sep('Step 4: 後確認 - 残っているFBテスト伝票を検索');
  const debtsResp = await req('POST', '/fb-payment/pending-debts', { accountCodes: ['312', '314'] });
  const remaining = (debtsResp.data?.data || []).filter(d => (d.headerText || '').includes('FBテスト'));
  console.log(`FBテスト伝票の残り: ${remaining.length}件`);
  if (remaining.length > 0) {
    remaining.forEach(d => console.log(`  ${d.voucherNo} L${d.lineNo}: ${d.accountCode} ¥${Number(d.amount).toLocaleString()} | ${d.headerText}`));
  }

  if (!dryRun && deletedCount > 0) {
    try { fs.unlinkSync('scripts/_fb_test_record.json'); } catch {}
    console.log('\n_fb_test_record.json を削除しました。');
  }

  sep(dryRun ? 'DRY-RUN 完了（何も変更していません）' : 'クリーンアップ完了');
}

main().catch(e => console.error('Error:', e));

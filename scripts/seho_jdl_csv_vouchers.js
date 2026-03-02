/**
 * 从 JDL会計 仕訳一覧 CSV 生成 SEHO 会计凭证 SQL。
 * 日期格式：令和+月日，如 70401 = 令和7年4月1日 = 2025-04-01
 * 用法: node scripts/seho_jdl_csv_vouchers.js [CSV路径]
 * 默认: %USERPROFILE%\Downloads\ 下匹配 *仕訳*.csv 或 *JDL*.csv 的第一个文件
 */
import { readFileSync, existsSync, writeFileSync, readdirSync } from 'fs';
import { join } from 'path';

const downloadsDir = process.env.USERPROFILE ? join(process.env.USERPROFILE, 'Downloads') : '';
const defaultCsvPath = process.argv[2];

function findCsv() {
  if (defaultCsvPath && existsSync(defaultCsvPath)) return defaultCsvPath;
  if (!downloadsDir || !existsSync(downloadsDir)) return null;
  const files = readdirSync(downloadsDir);
  const candidates = files.filter(f => /\.csv$/i.test(f) && /JDL|仕訳/.test(f));
  const csv = candidates.find(f => /0803/.test(f)) || candidates.find(f => /0304/.test(f)) || candidates[0];
  return csv ? join(downloadsDir, csv) : null;
}

/** 令和 R + MM + DD → YYYY-MM-DD。例: 70401 → 2025-04-01 (令和7年4月1日) */
function reiwaToIso(str) {
  const s = String(str).trim().replace(/\D/g, '');
  if (s.length < 5) return null;
  const reiwaYear = parseInt(s.slice(0, -4), 10) || parseInt(s.slice(0, 1), 10);
  const month = s.slice(-4, -2);
  const day = s.slice(-2);
  const year = 2018 + reiwaYear; // 令和1=2019
  if (year < 2019 || year > 2030) return null;
  const mm = month.padStart(2, '0');
  const dd = day.padStart(2, '0');
  if (parseInt(mm, 10) < 1 || parseInt(mm, 10) > 12) return null;
  if (parseInt(dd, 10) < 1 || parseInt(dd, 10) > 31) return null;
  return `${year}-${mm}-${dd}`;
}

function parseCsv(content) {
  const lines = content.split(/\r?\n/).map(l => l.trim()).filter(Boolean);
  const parseRow = (line) => {
    const out = [];
    let cur = '';
    let inQuoted = false;
    for (let i = 0; i < line.length; i++) {
      const c = line[i];
      if (c === '"') { inQuoted = !inQuoted; continue; }
      if (!inQuoted && (c === ',' || c === '\t')) { out.push(cur.trim()); cur = ''; continue; }
      cur += c;
    }
    out.push(cur.trim());
    return out;
  };
  let dataStart = 0;
  let headers = [];
  for (let i = 0; i < lines.length; i++) {
    const row = parseRow(lines[i]);
    if (row.some(c => /日付|借方科目|貸方科目|金額/.test(c))) {
      headers = row;
      dataStart = i + 1;
      break;
    }
  }
  const rows = lines.slice(dataStart).map(l => parseRow(l)).filter(r => r.length >= 10);
  return { headers, rows };
}

async function main() {
  const csvPath = defaultCsvPath || findCsv();
  if (!csvPath || !existsSync(csvPath)) {
    console.error('未找到 CSV。请指定路径: node scripts/seho_jdl_csv_vouchers.js <path>');
    console.error('或将 JDL会計-0304-0803-仕訳一覧(1).csv 放入 Downloads');
    process.exit(1);
  }
  let content = readFileSync(csvPath, 'utf8');
  try {
    const buf = readFileSync(csvPath);
    const iconv = (await import('iconv-lite')).default;
    const sjis = iconv.decode(buf, 'Shift_JIS');
    if (sjis.includes('日付') || sjis.includes('借方') || sjis.includes('貸方')) content = sjis;
  } catch (_) {}
  const { headers, rows } = parseCsv(content);
  console.log('CSV 列:', headers.slice(0, 25));
  console.log('行数:', rows.length);

  // JDL 仕訳一覧: 番号, 日付, 借方科目,, 借方補助,, 貸方科目,, 貸方補助,, 金額, 摘要, ...
  const idxDate = headers.findIndex(h => /日付/.test(h));
  const idxDebitAccount = headers.findIndex(h => /借方科目/.test(h));
  const idxCreditAccount = headers.findIndex(h => /貸方科目/.test(h));
  const idxAmount = headers.findIndex(h => /^金額$/.test(h));
  const idxSummary = headers.findIndex(h => /摘要/.test(h));
  const idxNo = headers.findIndex(h => /番号/.test(h));

  if (idxDate < 0 || idxDebitAccount < 0 || idxCreditAccount < 0 || idxAmount < 0) {
    console.error('未找到必要列: 日付, 借方科目, 貸方科目, 金額');
    process.exit(1);
  }

  const out = [];
  let voucherIndex = 1;
  for (const row of rows) {
    const dateStr = row[idxDate];
    const postingDate = reiwaToIso(dateStr) || (dateStr && /^\d{4}-\d{2}-\d{2}$/.test(dateStr) ? dateStr : null);
    if (!postingDate) continue;
    const debitCode = String(row[idxDebitAccount] || '').trim();
    const creditCode = String(row[idxCreditAccount] || '').trim();
    const amount = parseFloat(String(row[idxAmount] || '0').replace(/,/g, ''));
    if (!debitCode || !creditCode || amount <= 0) continue;
    const summary = (row[idxSummary] || '').slice(0, 200).replace(/\s+/g, ' ');
    const voucherNo = `25${postingDate.replace(/-/g, '').slice(2)}${String(voucherIndex).padStart(4, '0')}`;
    const payload = {
      header: {
        companyCode: 'SEHO',
        voucherNo,
        voucherType: 'GL',
        postingDate,
        currency: 'JPY',
        summary: summary || '仕訳'
      },
      lines: [
        { lineNo: 1, accountCode: debitCode, drcr: 'DR', amount, note: summary },
        { lineNo: 2, accountCode: creditCode, drcr: 'CR', amount, note: summary }
      ]
    };
    out.push({ voucherNo, payload });
    voucherIndex++;
  }

  const sqlLines = out.map(({ payload }) =>
    `INSERT INTO vouchers (company_code, payload) VALUES ('SEHO', '${JSON.stringify(payload).replace(/'/g, "''")}'::jsonb) ON CONFLICT (company_code, voucher_no) DO NOTHING;`
  );
  const sqlPath = join(process.cwd(), 'server-dotnet', 'sql', 'fix_seho_vouchers_from_jdl.sql');
  writeFileSync(sqlPath, '-- SEHO 凭证（从 JDL 仕訳一覧 CSV 生成，令和日期已转换）\n\n' + sqlLines.join('\n'), 'utf8');
  console.log('已生成:', sqlPath, '凭证数:', out.length);
}

main().catch(e => { console.error(e); process.exit(1); });

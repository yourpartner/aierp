/**
 * 从「エイテイエルテイ株式会社_社員リスト.pdf」提取社員一覧，生成 ATLT 员工 INSERT 与工资 UPDATE 的 SQL。
 * 用法: node scripts/atlt_import_from_pdf.js [PDF路径]
 * 默认 PDF: %USERPROFILE%\Downloads\エイテイエルテイ株式会社_社員リスト.pdf
 * 依赖: npm install pdf-parse
 */

import { readFileSync, existsSync, writeFileSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const sqlDir = join(__dirname, '..', 'server-dotnet', 'sql');

const defaultPdfPath = process.env.ATLT_PDF || join(
  process.env.USERPROFILE || process.env.HOME || '',
  'Downloads',
  'エイテイエルテイ株式会社_社員リスト.pdf'
);

async function main() {
  const pdfPath = process.argv[2] || defaultPdfPath;
  if (!existsSync(pdfPath)) {
    console.error('PDF 未找到:', pdfPath);
    console.error('请将 エイテイエルテイ株式会社_社員リスト.pdf 放入下载文件夹，或传入路径: node scripts/atlt_import_from_pdf.js <path>');
    process.exit(1);
  }

  let PDFParse;
  try {
    const mod = await import('pdf-parse');
    PDFParse = mod.PDFParse || mod.default?.PDFParse || mod.default;
  } catch (e) {
    console.error('请先安装 pdf-parse: npm install pdf-parse');
    process.exit(1);
  }

  const buffer = readFileSync(pdfPath);
  const parser = new PDFParse({ data: buffer });
  const textResult = await parser.getText();
  await parser.destroy();
  const text = (textResult && textResult.text) || '';

  // 解析：表头「項番 氏名 性別 基本給 その他」，数据行如 "1 張宏亮 男 ¥300,000"
  // 基本給列格式：¥300,000（日元，含逗号）
  const lines = text.split(/\r?\n/).map(s => s.trim()).filter(Boolean);
  const employees = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    // 跳过表头
    if (/項番|氏名|性別|基本給|その他|社員リスト/i.test(line) && line.length < 80) continue;
    // 基本給列：¥ 开头 + 数字与逗号
    const salaryMatch = line.match(/¥\s*([\d,]+)/);
    if (!salaryMatch) continue;
    const salaryYen = parseInt(salaryMatch[1].replace(/,/g, ''), 10);
    if (salaryYen <= 0 || salaryYen > 10000000) continue;
    // 項番：行首数字
    const numMatch = line.match(/^\s*(\d+)\s+/);
    const num = numMatch ? numMatch[1] : String(employees.length + 1);
    const code = `ATLT${String(num).padStart(3, '0')}`;
    // 性别
    const gender = /\s女\s/.test(line) ? 'F' : 'M';
    // 姓名：去掉項番、性別(男/女)、¥金额后剩余部分
    let namePart = line
      .replace(/^\s*\d+\s+/, '')
      .replace(/\s*男\s*/, ' ')
      .replace(/\s*女\s*/, ' ')
      .replace(/\s*¥\s*[\d,]+\s*.*$/, '')
      .trim();
    if (!namePart) continue;
    employees.push({ code, nameKanji: namePart, baseSalary: salaryYen, gender });
  }

  if (employees.length === 0) {
    console.log('未解析到员工行，原始文本前 3000字：\n');
    console.log(text.slice(0, 3000));
    console.log('\n请根据上述内容手動编辑 server-dotnet/sql/fix_atlt_employees_insert.sql 与 fix_atlt_employees_salaries.sql');
    return;
  }

  // 输出 INSERT 与 UPDATE 用 SQL
  const insertSql = [
    '-- ATLT 社員（从 エイテイエルテイ株式会社_社員リスト.pdf 自动生成，仅 ATLT，不影响他社）',
    '-- 执行顺序: 先 fix_atlt_company.sql，再本文件，再 contracts/schema/policy/salaries',
    ''
  ];
  const basePayload = {
    nameKana: '',
    gender: 'M',
    birthDate: '1990-01-01',
    nationality: 'JP',
    contact: { phone: null, email: null, postalCode: null, address: null },
    insurance: { hireInsuranceNo: null, endowNo: null, healthNo: null, endowBaseNo: null, joinDate: null, quitDate: null },
    contracts: [],
    departments: [],
    bankAccounts: [],
    emergencies: [],
    attachments: []
  };

  for (const e of employees) {
    const payload = { code: e.code, nameKanji: e.nameKanji, ...basePayload, gender: e.gender || 'M' };
    insertSql.push(`INSERT INTO employees (company_code, payload) SELECT 'ATLT', '${JSON.stringify(payload).replace(/'/g, "''")}'::jsonb WHERE NOT EXISTS (SELECT 1 FROM employees WHERE company_code = 'ATLT' AND payload->>'code' = '${e.code}');`);
  }

  const salarySql = [
    '-- ATLT 社員の給与を PDF の「給与」列に合わせて設定（从 社員リスト.pdf 自动生成）',
    ''
  ];
  for (const e of employees) {
    const desc = `基本給${Math.round(e.baseSalary / 10000)}万、社会保険加入、雇用保険加入`;
    const config = JSON.stringify({ baseSalary: e.baseSalary, socialInsurance: true, employmentInsurance: true });
    const salaries = JSON.stringify([{ startDate: '2024-01-01', description: desc, payrollConfig: JSON.parse(config) }]);
    salarySql.push(`UPDATE employees SET payload = jsonb_set(payload, '{salaries}', '${salaries.replace(/'/g, "''")}'::jsonb) WHERE company_code = 'ATLT' AND payload->>'code' = '${e.code}';`);
  }

  const insertPath = join(sqlDir, 'fix_atlt_employees_insert.sql');
  const salaryPath = join(sqlDir, 'fix_atlt_employees_salaries.sql');
  writeFileSync(insertPath, insertSql.join('\n'), 'utf8');
  writeFileSync(salaryPath, salarySql.join('\n'), 'utf8');
  console.log('已生成:', insertPath);
  console.log('已生成:', salaryPath);
  console.log('员工数:', employees.length);
}

main().catch(e => {
  console.error(e);
  process.exit(1);
});

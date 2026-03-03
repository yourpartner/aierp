const { Client } = require('pg');
const client = new Client({
  host: 'aimate-db-server.postgres.database.azure.com',
  port: 5432,
  user: 'postgres',
  password: 'Yanxia_ERP_2026!',
  database: 'postgres',
  ssl: { rejectUnauthorized: false }
});
async function run() {
  await client.connect();

  // JP01のスキーマ確認
  const r1 = await client.query(
    "SELECT name, company_code, schema FROM schemas WHERE name = 'timesheet' AND company_code = 'JP01'"
  );
  console.log('=== JP01 timesheet schema ===');
  if (r1.rows.length === 0) {
    console.log('JP01 schema NOT FOUND - falls back to global (company_code IS NULL)');
  } else {
    const s = typeof r1.rows[0].schema === 'string' ? JSON.parse(r1.rows[0].schema) : r1.rows[0].schema;
    console.log(JSON.stringify(s, null, 2));
  }

  // グローバルスキーマ確認
  const r2 = await client.query(
    "SELECT name, company_code, schema FROM schemas WHERE name = 'timesheet' AND company_code IS NULL"
  );
  console.log('\n=== Global (NULL) timesheet schema ===');
  if (r2.rows.length === 0) {
    console.log('No global schema found');
  } else {
    const s2 = typeof r2.rows[0].schema === 'string' ? JSON.parse(r2.rows[0].schema) : r2.rows[0].schema;
    console.log(JSON.stringify(s2, null, 2));
  }

  // timesheetsテーブルのサンプルデータ（JP01）
  const r3 = await client.query(
    "SELECT id, payload FROM timesheets WHERE company_code='JP01' ORDER BY created_at DESC LIMIT 3"
  ).catch(() => ({ rows: [] }));
  console.log('\n=== timesheets payload sample (JP01) ===');
  for (const row of r3.rows) {
    console.log(`id=${row.id}:`, JSON.stringify(row.payload, null, 2));
  }

  // 全スキーマのcompany_code一覧
  const r4 = await client.query(
    "SELECT name, company_code FROM schemas WHERE name='timesheet' ORDER BY company_code"
  );
  console.log('\n=== All timesheet schema companies ===');
  console.table(r4.rows);

  await client.end();
}
run().catch(e => { console.error(e); process.exit(1); });

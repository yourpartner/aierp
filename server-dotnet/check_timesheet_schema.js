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

  // timesheetスキーマ確認
  const r1 = await client.query(
    "SELECT name, company_code, schema FROM schemas WHERE name = 'timesheet' LIMIT 3"
  );
  console.log('=== timesheet schemas ===');
  for (const row of r1.rows) {
    console.log(`name=${row.name} company=${row.company_code}`);
    try { console.log(JSON.stringify(JSON.parse(row.schema), null, 2)); } catch(e) { console.log(row.schema); }
  }

  // timesheet payloadサンプル確認（objectsテーブル）
  const r3 = await client.query("SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_name LIKE '%timesheet%'");
  console.log('\n=== timesheet-related tables ===');
  console.table(r3.rows);

  // objectsテーブルがある場合
  const r2 = await client.query(
    "SELECT id, payload FROM objects WHERE entity_type='timesheet' AND company_code='JP01' ORDER BY created_at DESC LIMIT 2"
  ).catch(() => ({ rows: [] }));
  console.log('\n=== timesheet payload sample ===');
  for (const row of r2.rows) {
    console.log(`id=${row.id}`, JSON.stringify(row.payload, null, 2));
  }

  await client.end();
}
run().catch(e => { console.error(e); process.exit(1); });

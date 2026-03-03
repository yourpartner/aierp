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

  // YP01のユーザー名を確認
  const r1 = await client.query(
    "SELECT employee_code, name, company_code FROM users WHERE company_code = 'YP01' ORDER BY employee_code"
  );
  console.log('=== YP01 users ===');
  console.table(r1.rows);

  // name が NULL のユーザーを全社確認
  const r2 = await client.query(
    "SELECT company_code, employee_code, name FROM users WHERE name IS NULL OR name = '' ORDER BY company_code, employee_code LIMIT 20"
  );
  console.log('\n=== Users with NULL/empty name ===');
  console.table(r2.rows);

  await client.end();
}
run().catch(e => { console.error(e); process.exit(1); });

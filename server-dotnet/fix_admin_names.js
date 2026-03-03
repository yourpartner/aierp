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

  // name が NULL の admin ユーザーに '管理者' を設定
  const res = await client.query(
    "UPDATE users SET name = '管理者' WHERE (name IS NULL OR name = '') AND employee_code = 'admin' RETURNING company_code, employee_code, name"
  );
  console.log('Updated:');
  console.table(res.rows);

  await client.end();
}
run().catch(e => { console.error(e); process.exit(1); });

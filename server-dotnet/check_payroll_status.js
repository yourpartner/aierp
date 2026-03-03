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
  // Check payroll_runs statuses
  const r1 = await client.query(
    "SELECT status, run_type, period_month, COUNT(*) FROM payroll_runs WHERE company_code='JP01' GROUP BY status, run_type, period_month ORDER BY period_month DESC LIMIT 20"
  );
  console.log('=== payroll_runs statuses ===');
  console.table(r1.rows);

  // Check payroll_sheet sample
  const r2 = await client.query(
    "SELECT e.employee_code, r.period_month, r.status, r.run_type, e.payroll_sheet FROM payroll_run_entries e JOIN payroll_runs r ON r.id=e.run_id WHERE e.company_code='JP01' ORDER BY r.period_month DESC LIMIT 5"
  );
  console.log('\n=== payroll_run_entries sample ===');
  for (const row of r2.rows) {
    const sheet = row.payroll_sheet;
    const items = Array.isArray(sheet) ? sheet : (typeof sheet === 'string' ? JSON.parse(sheet) : []);
    console.log(`${row.employee_code} | ${row.period_month} | status=${row.status} | run_type=${row.run_type} | items=${items.length}`);
    if (items.length > 0) console.log('  first item:', JSON.stringify(items[0]));
  }
  await client.end();
}
run().catch(e => { console.error(e); process.exit(1); });

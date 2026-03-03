const { Client } = require('pg');
const c = new Client({
  host: 'aimate-db-server.postgres.database.azure.com',
  port: 5432, user: 'postgres', password: 'Yanxia_ERP_2026!',
  database: 'postgres', ssl: { rejectUnauthorized: false }
});
c.connect().then(async () => {
  const cols = await c.query(
    "SELECT column_name, data_type FROM information_schema.columns WHERE table_name='payroll_run_entries' ORDER BY ordinal_position"
  );
  console.log('=== payroll_run_entries columns ===');
  cols.rows.forEach(r => console.log(' ', r.column_name, '-', r.data_type));

  const runCols = await c.query(
    "SELECT column_name FROM information_schema.columns WHERE table_name='payroll_runs' ORDER BY ordinal_position"
  );
  console.log('\n=== payroll_runs columns ===');
  runCols.rows.forEach(r => console.log(' ', r.column_name));

  const sample = await c.query(
    'SELECT e.employee_code, e.employee_name, e.department_code, r.period_month, e.payroll_sheet FROM payroll_run_entries e JOIN payroll_runs r ON r.id=e.run_id WHERE e.payroll_sheet IS NOT NULL LIMIT 1'
  );
  if (sample.rows.length > 0) {
    const row = sample.rows[0];
    console.log('\n=== sample entry ===');
    console.log('employee_code:', row.employee_code);
    console.log('period_month:', row.period_month);
    const sheet = typeof row.payroll_sheet === 'string' ? JSON.parse(row.payroll_sheet) : row.payroll_sheet;
    console.log('payroll_sheet keys:', sheet ? Object.keys(sheet) : null);
    if (sheet) console.log(JSON.stringify(sheet, null, 2).substring(0, 3000));
  } else {
    console.log('\nno payroll_sheet data found');
    const count = await c.query('SELECT COUNT(*) FROM payroll_run_entries');
    console.log('total entries:', count.rows[0].count);
  }

  await c.end();
}).catch(e => { console.error(e); process.exit(1); });

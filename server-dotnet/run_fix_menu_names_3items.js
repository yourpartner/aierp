const fs = require('fs');
const path = require('path');
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
  const sql = fs.readFileSync(path.join(__dirname, 'sql', 'fix_menu_names_3items.sql'), 'utf8');
  await client.connect();
  console.log('Connected to Azure...');
  await client.query(sql);
  console.log('SQL executed.');

  const r = await client.query(`
    SELECT menu_key, module_code, menu_name->>'ja' as ja_name
    FROM permission_menus
    WHERE menu_key IN ('rcpt.planner', 'op.bankPayment')
       OR module_code IN ('fixed_asset', 'fixed_assets')
    ORDER BY module_code, menu_key
  `);
  console.log('\n=== Updated rows ===');
  for (const row of r.rows) {
    console.log(`  [${row.module_code}] ${row.menu_key}: ${row.ja_name}`);
  }
  await client.end();
}

run().catch(e => { console.error(e); process.exit(1); });

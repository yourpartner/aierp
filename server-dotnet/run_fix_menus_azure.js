const fs = require('fs');
const path = require('path');
const { Client } = require('pg');

const azureConn = {
  host: 'aimate-db-server.postgres.database.azure.com',
  port: 5432,
  user: 'postgres',
  password: 'Yanxia_ERP_2026!',
  database: 'postgres',
  ssl: { rejectUnauthorized: false }
};

async function run() {
  const sqlPath = path.join(__dirname, 'sql', 'fix_permission_menus_names.sql');
  const sql = fs.readFileSync(sqlPath, 'utf8');
  const client = new Client(azureConn);
  await client.connect();
  console.log('Connected to Azure (aimate-db-server)...');
  try {
    const result = await client.query(sql);
    console.log('fix_permission_menus_names.sql executed successfully on Azure.');
    
    // 验证结果
    const verify = await client.query(`
      SELECT menu_key, menu_name->>'ja' as ja_name, menu_path
      FROM permission_menus
      WHERE module_code = 'finance'
      ORDER BY display_order
    `);
    console.log('\n=== Finance menus after update ===');
    for (const row of verify.rows) {
      console.log(`  ${row.menu_key}: ${row.ja_name} -> ${row.menu_path || '(no path)'}`);
    }
  } finally {
    await client.end();
  }
}

run().catch(err => {
  console.error(err);
  process.exit(1);
});

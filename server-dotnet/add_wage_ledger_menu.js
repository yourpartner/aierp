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
  console.log('Connected to Azure...');

  await client.query(`
    INSERT INTO permission_menus (id, module_code, menu_key, menu_name, menu_path, caps_required, caps_all_required, parent_menu_key, description, display_order, is_active)
    VALUES (
      gen_random_uuid(),
      'hr',
      'hr.wage_ledger',
      '{"ja":"賃金台帳","zh":"工资台账","en":"Wage Ledger"}',
      '/hr/wage-ledger',
      ARRAY['payroll:view'],
      ARRAY[]::text[],
      NULL,
      '{"ja":"賃金台帳のダウンロード","zh":"工资台账下载","en":"Wage Ledger Download"}',
      13,
      true
    )
    ON CONFLICT (menu_key) DO UPDATE SET
      menu_name = EXCLUDED.menu_name,
      menu_path = EXCLUDED.menu_path,
      display_order = EXCLUDED.display_order,
      is_active = EXCLUDED.is_active
  `);
  console.log('Menu item inserted/updated.');

  const r = await client.query(
    "SELECT menu_key, module_code, menu_name->>'ja' as ja_name, menu_path, display_order FROM permission_menus WHERE module_code = 'hr' ORDER BY display_order"
  );
  console.log('\n=== HR Menus ===');
  console.table(r.rows);
  await client.end();
}

run().catch(e => { console.error(e); process.exit(1); });

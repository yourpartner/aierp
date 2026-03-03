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

  // まず現金関連のメニューを確認
  const check = await client.query(`
    SELECT id, menu_key, menu_name, menu_path, is_active, display_order
    FROM permission_menus
    WHERE menu_path LIKE '%cash%' OR menu_name::text ILIKE '%cash%' OR menu_name::text ILIKE '%現金%' OR menu_name::text ILIKE '%小口%'
    ORDER BY display_order
  `);
  console.log('現金関連メニュー一覧:');
  for (const row of check.rows) {
    console.log(`  menu_key=${row.menu_key}, name=${JSON.stringify(row.menu_name)}, path=${row.menu_path}, active=${row.is_active}, order=${row.display_order}`);
  }

  if (check.rows.length === 0) {
    console.log('該当メニューが見つかりませんでした。');
    await client.end();
    return;
  }

  // 小口現金管理メニューを無効化（現金出納帳以外の現金関連メニュー）
  const disable = await client.query(`
    UPDATE permission_menus
    SET is_active = false
    WHERE (menu_name::text ILIKE '%小口%' OR menu_key ILIKE '%petty%' OR menu_key ILIKE '%imprest%' OR menu_key ILIKE '%koguchi%')
      AND is_active = true
    RETURNING id, menu_key, menu_name
  `);
  if (disable.rows.length > 0) {
    console.log('無効化したメニュー:');
    for (const row of disable.rows) {
      console.log(`  menu_key=${row.menu_key}, name=${JSON.stringify(row.menu_name)}`);
    }
  } else {
    console.log('「小口現金管理」のメニューは見つかりませんでした（既に無効化済み or 別のキー名）。');
  }

  await client.end();
  console.log('Done.');
}

run().catch(e => {
  console.error('Error:', e.message);
  client.end();
});

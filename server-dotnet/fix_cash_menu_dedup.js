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

  // fin.cashLedger（古いキー）を無効化し、cash.ledger（新しいキー）を残す
  const r1 = await client.query(`
    UPDATE permission_menus
    SET is_active = false
    WHERE menu_key = 'fin.cashLedger'
    RETURNING id, menu_key
  `);
  console.log('fin.cashLedger を無効化:', r1.rows.length > 0 ? '成功' : '対象なし');

  // fin.cashFlow（古いキー）を無効化し、menu_cash_flow（新しいキー）を残す
  const r2 = await client.query(`
    UPDATE permission_menus
    SET is_active = false
    WHERE menu_key = 'fin.cashFlow'
    RETURNING id, menu_key
  `);
  console.log('fin.cashFlow を無効化:', r2.rows.length > 0 ? '成功' : '対象なし');

  // 最終状態確認
  const check = await client.query(`
    SELECT menu_key, menu_name->>'ja' as ja_name, menu_path, is_active, display_order
    FROM permission_menus
    WHERE menu_path LIKE '%cash%' OR menu_name::text ILIKE '%現金%' OR menu_name::text ILIKE '%小口%' OR menu_name::text ILIKE '%資金%'
    ORDER BY display_order
  `);
  console.log('\n現金関連メニュー（最終状態）:');
  for (const row of check.rows) {
    const status = row.is_active ? '✓ 有効' : '✗ 無効';
    console.log(`  ${status}  ${row.ja_name} (${row.menu_key}) → ${row.menu_path}`);
  }

  await client.end();
  console.log('\nDone.');
}

run().catch(e => {
  console.error('Error:', e.message);
  client.end();
});

const { Client } = require('pg');
const client = new Client({
  host: 'aimate-db-server.postgres.database.azure.com',
  port: 5432,
  user: 'postgres',
  password: 'Yanxia_ERP_2026!',
  database: 'postgres',
  ssl: { rejectUnauthorized: false }
});
client.connect().then(async () => {
  const res = await client.query(
    "SELECT menu_key, caps_required FROM permission_menus WHERE module_code = 'hr' ORDER BY display_order LIMIT 5"
  );
  console.table(res.rows);
  await client.end();
}).catch(e => { console.error(e); process.exit(1); });

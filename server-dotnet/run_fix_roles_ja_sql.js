// 在 Azure/本地执行 fix_roles_chinese_to_japanese.sql：将角色中文改为日语
// 连接：优先 PG_CONNECTION_STRING，否则 AZURE_DB_*，否则本地。
const fs = require('fs');
const path = require('path');
const { Client } = require('pg');

function getConn() {
  const cs = process.env.PG_CONNECTION_STRING;
  if (cs && cs.trim()) return { connectionString: cs.trim(), ssl: cs.includes('azure.com') ? { rejectUnauthorized: false } : undefined };
  if (process.env.AZURE_DB_PASSWORD) {
    return {
      host: process.env.AZURE_DB_HOST || 'yanxia-db-server.postgres.database.azure.com',
      port: 5432,
      user: process.env.AZURE_DB_USER || 'yanxia@yanxia-db-server',
      password: process.env.AZURE_DB_PASSWORD,
      database: 'postgres',
      ssl: { rejectUnauthorized: false }
    };
  }
  return {
    host: 'localhost',
    port: 5432,
    user: 'postgres',
    password: process.env.PGPASSWORD || 'Hpxdcd2508',
    database: 'postgres'
  };
}

async function run() {
  const sqlPath = path.join(__dirname, 'sql', 'fix_roles_chinese_to_japanese.sql');
  const sql = fs.readFileSync(sqlPath, 'utf8');
  const client = new Client(getConn());
  await client.connect();
  try {
    await client.query(sql);
    console.log('fix_roles_chinese_to_japanese.sql executed successfully.');
  } finally {
    await client.end();
  }
}

run().catch(err => {
  console.error(err);
  process.exit(1);
});

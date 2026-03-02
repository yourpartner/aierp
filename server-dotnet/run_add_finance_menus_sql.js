// 仅执行 add_finance_cash_flow_expense_menus.sql：新增資金繰り/経費精算一覧菜单，并统一固定資産菜单日语名称
// 仅操作 permission_menus 表，不影响其他数据。
// 连接：优先使用环境变量 PG_CONNECTION_STRING（与 .NET 一致）；否则使用 AZURE_DB_* 或本地默认。
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
const conn = getConn();

async function run() {
  const sqlPath = path.join(__dirname, 'sql', 'add_finance_cash_flow_expense_menus.sql');
  const sql = fs.readFileSync(sqlPath, 'utf8');
  const client = new Client(conn);
  await client.connect();
  try {
    await client.query(sql);
    console.log('SQL executed successfully.');
  } finally {
    await client.end();
  }
}

run().catch(err => {
  console.error(err);
  process.exit(1);
});

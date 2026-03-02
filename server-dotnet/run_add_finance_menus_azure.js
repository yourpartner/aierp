// 在 Azure PostgreSQL 上执行 add_finance_cash_flow_expense_menus.sql
// 连接方式与 update_azure_moneytree.py 一致（aimate-db-server）
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
  const sqlPath = path.join(__dirname, 'sql', 'add_finance_cash_flow_expense_menus.sql');
  const sql = fs.readFileSync(sqlPath, 'utf8');
  const client = new Client(azureConn);
  await client.connect();
  console.log('Connected to Azure (aimate-db-server)...');
  try {
    const res = await client.query(sql);
    console.log('SQL executed successfully on Azure.');
    if (res.length && res[res.length - 1].rows) console.log(res[res.length - 1].rows[0]);
  } finally {
    await client.end();
  }
}

run().catch(err => {
  console.error(err);
  process.exit(1);
});

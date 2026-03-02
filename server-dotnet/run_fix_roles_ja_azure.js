// 在 Azure PostgreSQL (aimate-db-server) 上执行 fix_roles_chinese_to_japanese.sql
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
  const sqlPath = path.join(__dirname, 'sql', 'fix_roles_chinese_to_japanese.sql');
  const sql = fs.readFileSync(sqlPath, 'utf8');
  const client = new Client(azureConn);
  await client.connect();
  console.log('Connected to Azure (aimate-db-server)...');
  try {
    await client.query(sql);
    console.log('fix_roles_chinese_to_japanese.sql executed successfully on Azure.');
  } finally {
    await client.end();
  }
}

run().catch(err => {
  console.error(err);
  process.exit(1);
});

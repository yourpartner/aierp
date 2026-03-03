const { Client } = require('pg');
const client = new Client({
  host: 'aimate-db-server.postgres.database.azure.com',
  port: 5432,
  user: 'postgres',
  password: 'Yanxia_ERP_2026!',
  database: 'postgres',
  ssl: { rejectUnauthorized: false }
});

// 修正後のスキーマ
// 変更点:
//   1. status enum に "saved" を追加
//   2. startTime / endTime を anyOf[null, string(pattern)] に変更
const FIXED_SCHEMA = {
  type: 'object',
  required: ['date', 'hours'],
  properties: {
    date:         { type: 'string', format: 'date' },
    note:         { type: 'string', maxLength: 1000 },
    task:         { type: 'string', maxLength: 200 },
    hours:        { type: 'number', minimum: 0, maximum: 24 },
    status: {
      type: 'string',
      enum: ['draft', 'saved', 'submitted', 'approved', 'rejected']
    },
    startTime: {
      anyOf: [
        { type: 'null' },
        { type: 'string', pattern: '^\\d{2}:\\d{2}$' }
      ]
    },
    endTime: {
      anyOf: [
        { type: 'null' },
        { type: 'string', pattern: '^\\d{2}:\\d{2}$' }
      ]
    },
    overtime:     { type: 'number', minimum: 0, maximum: 24 },
    projectCode:  { type: 'string', maxLength: 100 },
    lunchMinutes: { type: 'number', minimum: 0, maximum: 240 },
    isHoliday:    { type: 'boolean' },
    creatorUserId:{ type: ['string', 'null'] },
    createdMonth: { type: 'string' }
  },
  additionalProperties: true
};

async function run() {
  await client.connect();
  console.log('Connected to Azure...');

  // 全社のtimesheetスキーマを更新
  const res = await client.query(
    "SELECT company_code FROM schemas WHERE name = 'timesheet'"
  );
  console.log(`Found ${res.rows.length} company schemas to update:`, res.rows.map(r => r.company_code));

  for (const row of res.rows) {
    await client.query(
      "UPDATE schemas SET schema = $1::jsonb, updated_at = NOW() WHERE name = 'timesheet' AND company_code = $2",
      [JSON.stringify(FIXED_SCHEMA), row.company_code]
    );
    console.log(`  Updated: ${row.company_code}`);
  }

  // グローバルスキーマも存在すれば更新
  const global = await client.query(
    "SELECT 1 FROM schemas WHERE name = 'timesheet' AND company_code IS NULL"
  );
  if (global.rows.length > 0) {
    await client.query(
      "UPDATE schemas SET schema = $1::jsonb, updated_at = NOW() WHERE name = 'timesheet' AND company_code IS NULL",
      [JSON.stringify(FIXED_SCHEMA)]
    );
    console.log('  Updated: global (NULL)');
  }

  console.log('\nAll timesheet schemas updated successfully.');
  await client.end();
}

run().catch(e => { console.error(e); process.exit(1); });

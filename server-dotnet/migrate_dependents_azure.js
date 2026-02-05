// 迁移旧系统扶养人数据到 Azure 数据库
const fs = require('fs');
const { Client } = require('pg');

// 从 dependants dump 文件解析数据
function parseDependants(sqlContent) {
  const insertMatch = sqlContent.match(/INSERT INTO `dependants` VALUES (.+);/s);
  if (!insertMatch) return [];
  
  const valuesStr = insertMatch[1];
  const records = [];
  
  const regex = /\((\d+),(\d+),(\d+),'[^']*',\d+,\d+,'[^']*',(\d+),(?:NULL|'([^']*)'),(?:'([^']*)'|NULL),(?:'([^']*)'|NULL),'([^']+)',(\d+),'([^']*)',(\d+),(\d+)\)/g;
  
  let match;
  while ((match = regex.exec(valuesStr)) !== null) {
    records.push({
      id: parseInt(match[1]),
      companyId: parseInt(match[2]),
      employeeId: parseInt(match[3]),
      relation: parseInt(match[4]),
      otherRelation: match[5] || '',
      nameKana: match[6] || '',
      nameKanji: match[7] || '',
      birthDate: match[8].split(' ')[0],
      gender: match[9] === '1' ? 'M' : 'F',
      address: match[10] || '',
      cohabiting: match[11] === '1'
    });
  }
  return records;
}

// 从 employees dump 文件解析旧系统 EmployeeID -> 姓名 映射
function parseOldEmployees(sqlContent) {
  const insertMatch = sqlContent.match(/INSERT INTO `employees` VALUES (.+);/s);
  if (!insertMatch) return {};
  
  const valuesStr = insertMatch[1];
  const mapping = {};
  
  // (ID, CompanyID, ..., Furigana_FirstName, Furigana_LastName, FirstName, LastName, FullName, ...)
  // 简化匹配：提取 ID 和 FullName
  const regex = /\((\d+),(\d+),'[^']*',\d+,\d+,'[^']*','[^']*','[^']*','[^']*','[^']*','[^']*','([^']*)','([^']*)','([^']*)'/g;
  
  let match;
  while ((match = regex.exec(valuesStr)) !== null) {
    const id = parseInt(match[1]);
    const companyId = parseInt(match[2]);
    const lastName = match[3];
    const firstName = match[4];
    const fullName = match[5];
    
    if (companyId === 172) { // JP01
      mapping[id] = fullName || (lastName + ' ' + firstName);
    }
  }
  return mapping;
}

function relationToString(code, otherRelation) {
  const map = { 1: '配偶者', 2: '配偶者', 3: '子', 4: '父', 5: '母', 6: '祖父', 7: '祖母', 8: '夫の父', 9: '夫の母', 99: otherRelation || 'その他' };
  return map[code] || 'その他';
}

async function migrate() {
  console.log('读取 dump 文件...');
  const dependantsSql = fs.readFileSync('D:/yanxia/server-dotnet/Dump20251222/yourpartnerdb2_dependants.sql', 'utf8');
  const employeesSql = fs.readFileSync('D:/yanxia/server-dotnet/Dump20251222/yourpartnerdb2_employees.sql', 'utf8');
  
  const dependants = parseDependants(dependantsSql);
  const oldEmployeeNames = parseOldEmployees(employeesSql);
  
  const jp01Dependants = dependants.filter(d => d.companyId === 172);
  
  const byName = {};
  for (const dep of jp01Dependants) {
    const name = oldEmployeeNames[dep.employeeId];
    if (!name) continue;
    if (!byName[name]) byName[name] = [];
    byName[name].push({
      nameKana: dep.nameKana,
      nameKanji: dep.nameKanji,
      birthDate: dep.birthDate,
      gender: dep.gender,
      cohabiting: dep.cohabiting,
      relation: relationToString(dep.relation, dep.otherRelation),
      address: dep.address
    });
  }

  console.log(`准备迁移 ${Object.keys(byName).length} 名员工的扶养人数据到 Azure...`);

  const client = new Client({
    host: 'yanxia-db-server.postgres.database.azure.com',
    port: 5432,
    user: 'yanxia@yanxia-db-server',
    password: 'Hpxdcd2508',
    database: 'postgres',
    ssl: { rejectUnauthorized: false }
  });

  await client.connect();
  console.log('已连接 Azure 数据库');

  let updated = 0;
  let skipped = 0;
  let notFound = 0;

  for (const [name, deps] of Object.entries(byName)) {
    // 按姓名匹配（去除空格干扰）
    const result = await client.query(
      `SELECT id, payload FROM employees WHERE company_code = 'JP01' AND (REPLACE(payload->>'nameKanji', ' ', '') = REPLACE($1, ' ', '') OR REPLACE(payload->>'nameKanji', '　', '') = REPLACE($1, ' ', ''))`,
      [name]
    );

    if (result.rows.length === 0) {
      console.log(`Azure 未找到员工: ${name}`);
      notFound++;
      continue;
    }

    const employee = result.rows[0];
    const payload = employee.payload || {};

    if (payload.dependents && payload.dependents.length > 0) {
      console.log(`Azure 员工 ${name} 已有数据，跳过`);
      skipped++;
      continue;
    }

    payload.dependents = deps;
    await client.query(
      `UPDATE employees SET payload = $1, updated_at = NOW() WHERE id = $2`,
      [JSON.stringify(payload), employee.id]
    );
    console.log(`已更新 Azure 员工 ${name}: ${deps.length} 个扶养人`);
    updated++;
  }

  await client.end();
  console.log(`\nAzure 迁移完成! 成功: ${updated}, 跳过: ${skipped}, 未找到: ${notFound}`);
}

migrate().catch(console.error);

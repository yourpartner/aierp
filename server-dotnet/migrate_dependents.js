// 迁移旧系统扶养人数据到新系统
const fs = require('fs');
const { Client } = require('pg');

// 从 dependants dump 文件解析数据
function parseDependants(sqlContent) {
  const insertMatch = sqlContent.match(/INSERT INTO `dependants` VALUES (.+);/s);
  if (!insertMatch) return [];
  
  const valuesStr = insertMatch[1];
  const records = [];
  
  // 解析每条记录 (ID,CompanyID,EmployeeID,UpdateTime,CreateUserID,UpdateUserID,CreateTime,Relation,OtherRelation,DependantFurigana,DependantName,DependantBirthday,DependantSex,DependantAddress,LiveTogether,SpouseIncome)
  // 注意：NULL 值需要特殊处理
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
      birthDate: match[8].split(' ')[0], // 只取日期部分
      gender: match[9] === '1' ? 'M' : 'F',
      address: match[10] || '',
      cohabiting: match[11] === '1'
    });
  }
  
  return records;
}

// Relation 代码转换
function relationToString(code, otherRelation) {
  const map = {
    1: '配偶者',
    2: '配偶者',
    3: '子',
    4: '父',
    5: '母',
    6: '祖父',
    7: '祖母',
    8: '夫の父',
    9: '夫の母',
    99: otherRelation || 'その他'
  };
  return map[code] || 'その他';
}

// 将旧系统 EmployeeID 转换为新系统 employee_code
function employeeIdToCode(employeeId) {
  return 'E' + String(employeeId).padStart(4, '0');
}

async function migrate() {
  console.log('读取 dump 文件...');
  
  const dependantsSql = fs.readFileSync('D:/yanxia/server-dotnet/Dump20251222/yourpartnerdb2_dependants.sql', 'utf8');
  
  const dependants = parseDependants(dependantsSql);
  
  console.log(`解析到 ${dependants.length} 条扶养人记录`);
  
  // 只处理 CompanyID=172 (JP01) 的数据
  const jp01Dependants = dependants.filter(d => d.companyId === 172);
  console.log(`JP01 公司扶养人记录: ${jp01Dependants.length} 条`);
  
  // 按员工分组（旧系统 EmployeeID 直接对应新系统 employee_code 的数字部分）
  const byEmployee = {};
  for (const dep of jp01Dependants) {
    const empCode = employeeIdToCode(dep.employeeId);
    if (!byEmployee[empCode]) {
      byEmployee[empCode] = [];
    }
    byEmployee[empCode].push({
      nameKana: dep.nameKana,
      nameKanji: dep.nameKanji,
      birthDate: dep.birthDate,
      gender: dep.gender,
      cohabiting: dep.cohabiting,
      relation: relationToString(dep.relation, dep.otherRelation),
      address: dep.address
    });
  }
  
  console.log(`按员工分组后: ${Object.keys(byEmployee).length} 个员工有扶养人数据`);
  console.log(`员工列表: ${Object.keys(byEmployee).join(', ')}`);
  
  // 连接本地 PostgreSQL
  const client = new Client({
    host: 'localhost',
    port: 5432,
    user: 'postgres',
    password: 'Hpxdcd2508',
    database: 'postgres',
    ssl: false
  });
  
  await client.connect();
  console.log('已连接本地数据库');
  
  let updated = 0;
  let notFound = 0;
  let skipped = 0;
  
  for (const [empCode, deps] of Object.entries(byEmployee)) {
    // 查找员工
    const result = await client.query(
      `SELECT id, payload FROM employees WHERE company_code = 'JP01' AND employee_code = $1`,
      [empCode]
    );
    
    if (result.rows.length === 0) {
      console.log(`员工 ${empCode} 在新系统中未找到`);
      notFound++;
      continue;
    }
    
    const employee = result.rows[0];
    const payload = employee.payload || {};
    
    // 合并扶养人数据（如果已有数据则跳过）
    if (payload.dependents && payload.dependents.length > 0) {
      console.log(`员工 ${empCode} 已有扶养人数据 (${payload.dependents.length} 人)，跳过`);
      skipped++;
      continue;
    }
    
    payload.dependents = deps;
    
    // 更新员工记录
    await client.query(
      `UPDATE employees SET payload = $1, updated_at = NOW() WHERE id = $2`,
      [JSON.stringify(payload), employee.id]
    );
    
    console.log(`已更新员工 ${empCode}: ${deps.length} 个扶养人`);
    updated++;
  }
  
  await client.end();
  
  console.log('\n迁移完成!');
  console.log(`成功更新: ${updated} 个员工`);
  console.log(`已有数据跳过: ${skipped} 个员工`);
  console.log(`未找到: ${notFound} 个员工`);
}

migrate().catch(console.error);

const { Client } = require('pg');

async function updatePolicy() {
  const client = new Client({
    host: 'localhost',
    user: 'postgres',
    password: 'Hpxdcd2508',
    database: 'postgres'
  });
  
  await client.connect();
  
  // 获取当前 policy
  const res = await client.query(`
    SELECT id, payload FROM payroll_policies 
    WHERE company_code = 'JP01' AND (payload->>'isActive')::boolean = true 
    ORDER BY created_at DESC LIMIT 1
  `);
  
  if (res.rows.length === 0) {
    console.log('No policy found');
    await client.end();
    return;
  }
  
  const { id, payload } = res.rows[0];
  console.log('Policy ID:', id);
  
  // 时薪关键词
  const hourlyKeywords = ['時給', '时薪', 'hourly', '時間給'];
  
  // 更新 rules
  function updateRules(rules) {
    return rules.map(rule => {
      const activation = rule.activation || {};
      
      if (activation.isHourlyRateMode === true) {
        // 时薪规则：使用 salaryDescriptionContains
        return {
          ...rule,
          activation: { salaryDescriptionContains: hourlyKeywords }
        };
      } else if (activation.forEmploymentTypes && !activation.isHourlyRateMode) {
        // 月薪规则：添加 salaryDescriptionNotContains
        return {
          ...rule,
          activation: {
            ...activation,
            salaryDescriptionNotContains: hourlyKeywords
          }
        };
      }
      return rule;
    });
  }
  
  if (payload.rules) {
    payload.rules = updateRules(payload.rules);
  }
  if (payload.dsl && payload.dsl.rules) {
    payload.dsl.rules = updateRules(payload.dsl.rules);
  }
  
  // 更新数据库
  await client.query(
    'UPDATE payroll_policies SET payload = $1 WHERE id = $2',
    [JSON.stringify(payload), id]
  );
  
  console.log('Policy updated successfully');
  console.log('Sample rule activation:', JSON.stringify(payload.rules[0].activation, null, 2));
  
  await client.end();
}

updatePolicy().catch(console.error);


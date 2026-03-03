const { Client } = require('pg');
const c = new Client({ host:'aimate-db-server.postgres.database.azure.com', port:5432, user:'postgres', password:'Yanxia_ERP_2026!', database:'postgres', ssl:{rejectUnauthorized:false} });
c.connect().then(async()=>{
  const r = await c.query('SELECT employee_code, name, department_code, payload FROM employees LIMIT 3');
  r.rows.forEach(row=>{
    console.log('code:', row.employee_code, 'name:', row.name, 'dept:', row.department_code);
    const p = typeof row.payload==='string'?JSON.parse(row.payload):row.payload;
    if(p) console.log('payload keys:', Object.keys(p), '\nfull:', JSON.stringify(p).substring(0,400));
    console.log('---');
  });
  await c.end();
}).catch(e=>{console.error(e);});

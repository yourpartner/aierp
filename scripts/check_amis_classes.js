const fs = require('fs');
const path = require('path');
const arr = JSON.parse(fs.readFileSync(path.join(__dirname, 'amis_assets.json'), 'utf8'));
const classes = JSON.parse(fs.readFileSync(path.join(__dirname, 'amis_classes.json'), 'utf8'));
console.log('=== AMIS Asset Classes ===');
classes.forEach(c => console.log('  ID:', c.id, '| Name:', c.class_name));
console.log('');
const g = {};
arr.forEach(r => {
  const k = r.asset_class_id;
  if (!(k in g)) g[k] = [];
  g[k].push(r.asset_name);
});
Object.entries(g).forEach(([k, v]) => {
  console.log('ClassID:', k, '| Count:', v.length, '| Sample:', v.slice(0, 2).join('; '));
});

import { readdirSync } from 'fs';
import { join } from 'path';
const dir = process.env.USERPROFILE ? join(process.env.USERPROFILE, 'Downloads') : '';
const files = readdirSync(dir).filter(f => /\.csv$/i.test(f));
console.log('Downloads CSV files:');
files.forEach(f => console.log(' ', f));

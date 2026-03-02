import { readFileSync, writeFileSync, readdirSync } from 'fs';
import { join } from 'path';
import iconv from 'iconv-lite';

const dir = join(process.env.USERPROFILE, 'Downloads');
const files = readdirSync(dir);
const name = files.find(f => /\.csv$/i.test(f) && /0803.*仕訳|JDL.*0803/.test(f)) || files.find(f => /JDL.*仕訳.*\.csv$/i.test(f));
const path = join(dir, name);
console.log('Reading:', path);
const buf = readFileSync(path);
const utf8 = buf.toString('utf8');
const sjis = iconv.decode(buf, 'Shift_JIS');
const content = sjis.includes('日付') || sjis.includes('借方') ? sjis : utf8;
const lines = content.split(/\r?\n/).slice(0, 25);
writeFileSync(join(process.cwd(), 'server-dotnet', 'sql', 'jdl_csv_sample.txt'), lines.join('\n'), 'utf8');
console.log('First 25 lines written to server-dotnet/sql/jdl_csv_sample.txt');
console.log('First line (len=%d):', lines[0].length, lines[0].slice(0, 200));

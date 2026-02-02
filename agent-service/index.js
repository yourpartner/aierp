import 'dotenv/config';
import express from 'express';
import cors from 'cors';
import { OpenAI } from 'openai';
import { toolSpecs, executeTool, createCtx } from './tools.js';
import nodemailer from 'nodemailer';
import PDFKit from 'pdfkit';
import fs from 'fs';
import QRCode from 'qrcode';

const app = express();
app.use(cors());
app.use(express.json());

const client = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });
const ctx = createCtx();

// 将工具规格转成 OpenAI 的 function calling 工具
const tools = toolSpecs.map(t => ({
  type: 'function',
  function: {
    name: t.name,
    description: t.description,
    parameters: t.parameters
  }
}));

app.post('/chat', async (req, res) => {
  try {
    const { messages } = req.body; // [{role, content}]

    const thread = messages.map(m => ({ role: m.role, content: m.content }));

    while (true) {
      const completion = await client.chat.completions.create({
        model: 'gpt-4o',
        messages: thread,
        tools,
        tool_choice: 'auto'
      });

      const msg = completion.choices[0].message;
      if (msg.tool_calls && msg.tool_calls.length > 0) {
        for (const call of msg.tool_calls) {
          const { name, arguments: argText } = call.function;
          const args = JSON.parse(argText || '{}');
          try {
            const result = await executeTool(name, args, ctx);
            thread.push({ role: 'tool', tool_call_id: call.id, content: JSON.stringify(result) });
          } catch (e) {
            thread.push({ role: 'tool', tool_call_id: call.id, content: JSON.stringify({ error: String(e.message || e) }) });
          }
        }
        continue; // 继续让模型结合工具结果生成最终回答
      } else {
        return res.json({ message: msg });
      }
    }
  } catch (e) {
    res.status(500).json({ error: String(e.message || e) });
  }
});

async function resolveCjkFontPath() {
  // 优先 TTF/OTF（更少嵌字/编码问题），再回落 TTC
  const candidates = [
    process.env.CJK_FONT,
    // 首选 TTF/OTF（更稳定）
    'C:/Windows/Fonts/msyh.ttf',
    'C:/Windows/Fonts/simhei.ttf',
    'C:/Windows/Fonts/NotoSansCJKjp-Regular.otf',
    'C:/Windows/Fonts/NotoSansCJKsc-Regular.otf',
    '/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttf',
    '/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.otf',
    // 再回落 TTC（可能在某些环境下导致乱码，放在最后）
    'C:/Windows/Fonts/meiryo.ttc',
    'C:/Windows/Fonts/YuGothM.ttc',
    'C:/Windows/Fonts/msgothic.ttc',
    'C:/Windows/Fonts/simhei.ttf',
    'C:/Windows/Fonts/msyh.ttc',
    'C:/Windows/Fonts/simsun.ttc',
    '/usr/share/fonts/truetype/wqy/wqy-microhei.ttc',
    '/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc'
  ].filter(Boolean);
  for (const p of candidates) { try { if (p && fs.existsSync(p)) return p; } catch {}
  }
  return null;
}

function drawKvp(doc, x, y, label, value, opts = {}) {
  const { labelWidth = 80, valueWidth = 360, lineHeight = 18, fontSize = 11 } = opts;
  doc.fontSize(fontSize).text(String(label ?? ''), x, y, { width: labelWidth });
  doc.fontSize(fontSize).text(String(value ?? ''), x + labelWidth, y, { width: valueWidth });
  return y + lineHeight;
}

function drawTable(doc, x, y, table, opts = {}) {
  const {
    width = doc.page.width - doc.page.margins.left - doc.page.margins.right,
    headerBg = '#f5f7fa',
    borderColor = '#dcdfe6',
    fontSize = 10,
    rowHeight = 22,
    headerHeight = 24
  } = opts;
  if (!table || !Array.isArray(table.columns) || table.columns.length === 0) return y;
  const cols = table.columns;
  const rows = Array.isArray(table.rows) ? table.rows : [];
  const weights = cols.map(c => Number(c.width || 1));
  const total = weights.reduce((a,b)=>a+b,0) || 1;
  const colWidths = weights.map(w => Math.floor(width * w / total));

  // header background
  doc.save();
  try { doc.rect(x, y, width, headerHeight).fill(headerBg); } catch {}
  doc.restore();
  // borders
  doc.strokeColor(borderColor).lineWidth(0.8).rect(x, y, width, headerHeight).stroke();
  // header texts
  let cx = x;
  doc.fontSize(fontSize).fillColor('#000');
  cols.forEach((c, i) => {
    doc.text(String(c.title || ''), cx + 6, y + 5, { width: colWidths[i] - 12 });
    // column separator
    if (i < cols.length - 1) doc.moveTo(cx + colWidths[i], y).lineTo(cx + colWidths[i], y + headerHeight).stroke(borderColor);
    cx += colWidths[i];
  });
  y += headerHeight;

  // data rows
  rows.forEach((r) => {
    // row border
    doc.rect(x, y, width, rowHeight).stroke(borderColor);
    let cx2 = x;
    cols.forEach((c, i) => {
      const key = c.key || c.field || c.prop;
      let val = key ? (r[key]) : '';
      if (val === undefined || val === null) val = '';
      doc.text(String(val), cx2 + 6, y + 4, { width: colWidths[i] - 12 });
      if (i < cols.length - 1) doc.moveTo(cx2 + colWidths[i], y).lineTo(cx2 + colWidths[i], y + rowHeight).stroke(borderColor);
      cx2 += colWidths[i];
    });
    y += rowHeight;
  });
  return y;
}

async function drawQrOverlay(doc, qr) {
  try {
    if (!qr || !qr.url) return;
    const x = Number(qr.x ?? (doc.page.width - doc.page.margins.right - 100));
    const y = Number(qr.y ?? (doc.page.height - doc.page.margins.bottom - 100));
    const size = Math.max(40, Number(qr.size ?? 100));
    const level = String(qr.level || 'M');
    const buf = await QRCode.toBuffer(String(qr.url), { errorCorrectionLevel: level, width: size, margin: 0 });
    doc.save();
    // 背景白底，避免覆盖在网格或文字上影响识别
    try { doc.rect(x, y, size, size).fill('#FFFFFF'); } catch {}
    doc.image(buf, x, y, { width: size, height: size });
    doc.restore();
  } catch {}
}

function drawSealOverlay(doc, pdf) {
  const seal = pdf?.seal;
  if (!seal) return;
  const x = Number(seal.x ?? (doc.page.width - doc.page.margins.right - 160));
  const y = Number(seal.y ?? (doc.page.height - doc.page.margins.bottom - 160));
  const size = Math.max(60, Number(seal.size ?? 140));
  const opacity = Math.max(0, Math.min(1, Number(seal.opacity ?? 0.22)));
  const text = String(seal.text ?? (pdf?.companyName || '社 印'));

  doc.save();
  try { if (opacity < 1) doc.opacity(opacity); } catch {}
  // 如果提供图片则优先使用图片
  const img = seal.image;
  try {
    if (img && typeof img === 'string') {
      if (img.startsWith('data:')) {
        const base64 = img.substring(img.indexOf(',') + 1);
        const buf = Buffer.from(base64, 'base64');
        doc.image(buf, x, y, { width: size, height: size });
      } else if (fs.existsSync(img)) {
        doc.image(img, x, y, { width: size, height: size });
      } else {
        // 回退为矢量圆章
        doc.strokeColor('#d40000').lineWidth(3).circle(x + size/2, y + size/2, size/2 - 3).stroke();
        doc.fillColor('#d40000').fontSize(Math.floor(size/6)).text(text, x, y + size/2 - Math.floor(size/12), { width: size, align: 'center' });
      }
    } else {
      // 矢量圆章
      doc.strokeColor('#d40000').lineWidth(3).circle(x + size/2, y + size/2, size/2 - 3).stroke();
      doc.fillColor('#d40000').fontSize(Math.floor(size/6)).text(text, x, y + size/2 - Math.floor(size/12), { width: size, align: 'center' });
      // 微文（可选）
      const micro = String(pdf?.docId || '').slice(0, 18);
      if (micro) {
        try {
          doc.fontSize(Math.max(6, Math.floor(size/14))).text(micro, x, y + size - Math.max(10, Math.floor(size/10)), { width: size, align: 'center' });
        } catch {}
      }
    }
  } catch {}
  doc.restore();
}

function drawFooterDocId(doc, pdf) {
  const docId = String(pdf?.docId || '').trim();
  if (!docId) return;
  const x = doc.page.margins.left;
  const y = doc.page.height - doc.page.margins.bottom - 10; // 页内距底部 10px
  doc.save();
  try { doc.fillColor('#666666'); } catch {}
  try { doc.fontSize(8); } catch {}
  const prefix = String(pdf?.labels?.docIdPrefix || 'DocID:');
  try { doc.text(`${prefix} ${docId}`, x, y, { width: doc.page.width - doc.page.margins.left - doc.page.margins.right, align: 'left' }); } catch {}
  doc.restore();
}

async function renderTemplate(doc, pdf) {
  const pageWidth = doc.page.width - doc.page.margins.left - doc.page.margins.right;
  const name = String(pdf.template || '').toLowerCase();
  if (!name) return false;

  // 背景图片（可选）：支持本地路径或 dataURL/base64
  try {
    const bg = pdf.bgImage;
    if (bg) {
      if (typeof bg === 'string' && bg.startsWith('data:')) {
        const base64 = bg.substring(bg.indexOf(',') + 1);
        const buf = Buffer.from(base64, 'base64');
        doc.image(buf, 0, 0, { width: doc.page.width, height: doc.page.height });
      } else if (typeof bg === 'string' && fs.existsSync(bg)) {
        doc.image(bg, 0, 0, { width: doc.page.width, height: doc.page.height });
      }
    }
  } catch {}

  // 预置模板：employment_certificate（A4 纵向，左上对齐，带边框与签章占位）
  if (name === 'employment_certificate' || name === 'zh_certificate') {
    const title = String(pdf.title || '在职证明');
    // 标题
    doc.fontSize(22).text(title, { width: pageWidth, align: 'center' });
    doc.moveDown(1);
    // 信息框
    const x = doc.page.margins.left;
    let y = doc.y;
    doc.roundedRect(x - 6, y - 8, pageWidth + 12, 160, 6).stroke();
    y = drawKvp(doc, x, y, '公司', pdf.company, { valueWidth: pageWidth - 80 });
    y = drawKvp(doc, x, y, '员工', `${pdf?.employee?.name || ''}（${pdf?.employee?.code || ''}）`);
    y = drawKvp(doc, x, y, '类型', pdf.type || '在职证明');
    y = drawKvp(doc, x, y, '用途', pdf.purpose || '');
    y = drawKvp(doc, x, y, '出具日期', pdf.date || '');

    doc.moveDown(1);
    const bodyText = String(pdf.bodyText || '兹证明上述员工目前在本公司任职，特此证明。');
    doc.fontSize(12).text(bodyText, { width: pageWidth, align: 'left' });

    // 可选表格
    if (pdf.table && Array.isArray(pdf.table?.columns)) {
      doc.moveDown(1);
      const tx = doc.page.margins.left;
      let ty = doc.y;
      ty = drawTable(doc, tx, ty, pdf.table, {});
      doc.y = ty;
    }

    // 右下签章占位
    const sigY = doc.page.height - doc.page.margins.bottom - 120;
    const sigX = doc.page.width - doc.page.margins.right - 220;
    doc.fontSize(12).text('公司盖章：', sigX, sigY);
    doc.roundedRect(sigX + 60, sigY - 6, 140, 70, 4).stroke();
    // 叠加元素（印章）
    try { drawSealOverlay(doc, pdf); } catch {}
    // 页脚 DocID（小字号）
    try { drawFooterDocId(doc, pdf); } catch {}
    return true;
  }

  // jp_employment_form: 日文表格版在籍証明書（与示例图近似）
  if (name === 'jp_employment_form') {
    const L = Object.assign({
      name: '氏　名',
      address: '住　所',
      birthday: '生 年 月 日',
      hireDate: '入 社 年 月 日',
      separationDate: '退 職 年 月 日',
      years: '勤 続 年 数',
      worktime: '勤 務 時 間',
      duties: '従事する業務',
      remarks: '備　考　欄',
      closing: '上記のとおり、当社に在籍している（いた）ことを証明します。',
      zip: '〒　　',
      companyAddressPrefix: '所在地：',
      companyNamePrefix: '事業所名：',
      companyRepPrefix: '代表者名：',
      yearSuffix: '年', monthSuffix: '月', daySuffix: '日',
      yearsUnit: '年', monthsUnit: 'か月',
      breakFmt: '（休憩 {min} 分）',
      hireSuffix: '入社', sepSuffix: '退職'
    }, pdf?.labels || {});
    const locale = String(pdf?.locale || '').toLowerCase();
    if (locale === 'zh') {
      Object.assign(L, {
        name: '姓名', address: '住址', birthday: '出生日期', hireDate: '入职日期', separationDate: '离职日期', years: '工龄', worktime: '工作时间', duties: '工作内容', remarks: '备注',
        closing: '兹证明上述人员在本公司在职（或曾在职）。', zip: '邮编：', companyAddressPrefix: '公司地址：', companyNamePrefix: '公司名称：', companyRepPrefix: '法定代表人：',
        yearSuffix: '年', monthSuffix: '月', daySuffix: '日', yearsUnit: '年', monthsUnit: '个月', breakFmt: '（休息 {min} 分钟）', hireSuffix: '入职', sepSuffix: '离职',
        docIdPrefix: '文档编号:'
      });
    } else if (locale === 'en') {
      Object.assign(L, {
        name: 'Name', address: 'Address', birthday: 'Date of Birth', hireDate: 'Date of Joining', separationDate: 'Date of Leaving', years: 'Years of Service', worktime: 'Working Hours', duties: 'Duties', remarks: 'Remarks',
        closing: 'We hereby certify the above.', zip: 'ZIP:', companyAddressPrefix: 'Address: ', companyNamePrefix: 'Company: ', companyRepPrefix: 'Representative: ',
        yearSuffix: '-', monthSuffix: '-', daySuffix: '', yearsUnit: ' years', monthsUnit: ' months', breakFmt: ' (Break {min} minutes)', hireSuffix: 'Joined', sepSuffix: 'Left',
        docIdPrefix: 'DocID:'
      });
    }
    const x = doc.page.margins.left;
    let y = doc.page.margins.top + 10;
    const titleText = String(pdf.title || (locale === 'zh' ? '在职证明（表格版）' : (locale === 'en' ? 'Employment Certificate (Form)' : '在 籍 証 明 書')));
    try { doc.fontSize(18).text(titleText, { align: 'center' }); } catch {}
    y += 16;
    doc.moveDown(1);
    y = doc.y;

    const fullW = pageWidth;
    const lh = 38; // 行高
    const colLeftW = 120; // 左侧标签列宽
    const colRightW = fullW - colLeftW; // 右侧内容列宽

    function row(label, drawContent) {
      // 外框
      doc.rect(x, y, fullW, lh).stroke();
      // 竖分隔线
      doc.moveTo(x + colLeftW, y).lineTo(x + colLeftW, y + lh).stroke();
      // label
      doc.fontSize(12).text(label, x + 10, y + 10, { width: colLeftW - 20 });
      // 内容
      drawContent(x + colLeftW, y, colRightW, lh);
      y += lh;
    }

    // 1 氏名
    row(L.name, (cx, cy, w) => { doc.text(String(pdf?.employee?.name || ''), cx + 12, cy + 10, { width: w - 24 }); });
    // 2 住所
    row(L.address, (cx, cy, w) => { doc.text(String(pdf?.employee?.address || ''), cx + 12, cy + 10, { width: w - 24 }); });
    // 3 生年月日（紧凑靠左）
    row(L.birthday, (cx, cy, w) => {
      const parts = (pdf?.employee?.birthday || '').split('-');
      const year = parts[0] || ''; const month = parts[1] || ''; const day = parts[2] || '';
      const text = `${year}${L.yearSuffix} ${month}${L.monthSuffix} ${day}${L.daySuffix}`;
      doc.text(text, cx + 12, cy + 10, { width: w - 24, align: 'left' });
    });
    // 4 入 社 年 月 日（紧凑靠左，固定全角空格确保等距视觉）
    row(L.hireDate, (cx, cy, w) => {
      const parts = (pdf?.employment?.startDate || '').split('-');
      const year = parts[0] || ''; const month = parts[1] || ''; const day = parts[2] || '';
      const text = `${year}${L.yearSuffix} ${month}${L.monthSuffix} ${day}${L.daySuffix} ${L.hireSuffix}`;
      doc.text(text, cx + 12, cy + 10, { width: w - 24, align: 'left' });
    });
    // 5 退 職 年 月 日（紧凑靠左，固定全角空格确保等距视觉）
    row(L.separationDate, (cx, cy, w) => {
      const parts = (pdf?.employment?.endDate || '').split('-');
      const year = parts[0] || ''; const month = parts[1] || ''; const day = parts[2] || '';
      const text = (year || month || day) ? `${year}${L.yearSuffix} ${month}${L.monthSuffix} ${day}${L.daySuffix} ${L.sepSuffix}` : '';
      doc.text(text, cx + 12, cy + 10, { width: w - 24, align: 'left' });
    });
    // 6 勤続年数（紧凑靠左）
    row(L.years, (cx, cy, w) => {
      const years = String(pdf?.employment?.years || '');
      const months = String(pdf?.employment?.months || '');
      const text = `${years}${L.yearsUnit} ${months}${L.monthsUnit}`;
      doc.text(text, cx + 12, cy + 10, { width: w - 24, align: 'left' });
    });
    // 7 勤務時間（使用 ~ 作为分隔符；某些字体下全角波形可能显示为方框，统一使用 ASCII ~）
    row(L.worktime, (cx, cy, w) => {
      const s = pdf?.worktime?.start || '09:00';
      const e = pdf?.worktime?.end || '18:00';
      const br = pdf?.worktime?.breakMinutes || 60;
      const text = `${s} ~ ${e}` + String(L.breakFmt || '（休憩 {min} 分）').replace('{min}', String(br));
      doc.text(text, cx + 12, cy + 10, { width: w - 24 });
    });
    // 8 従事する業務（多行）
    const h2 = 80; doc.rect(x, y, fullW, h2).stroke(); doc.moveTo(x + colLeftW, y).lineTo(x + colLeftW, y + h2).stroke();
    doc.fontSize(12).text(L.duties, x + 10, y + 10, { width: colLeftW - 20 });
    doc.text(String(pdf?.employment?.duties || ''), x + colLeftW + 12, y + 10, { width: colRightW - 24 });
    y += h2;
    // 9 備考欄（多行）
    const h3 = 80; doc.rect(x, y, fullW, h3).stroke(); doc.moveTo(x + colLeftW, y).lineTo(x + colLeftW, y + h3).stroke();
    doc.fontSize(12).text(L.remarks, x + 10, y + 10, { width: colLeftW - 20 });
    doc.text(String(pdf?.remarks || ''), x + colLeftW + 12, y + 10, { width: colRightW - 24 });
    y += h3 + 24;

    // 结尾说明
    doc.fontSize(11).text(String(L.closing || ''), x, y);
    y += 24;
    // 日付
    const d = String(pdf?.date || '');
    doc.text(d, x, y);
    y += 40;

    // 会社情報与印章区
    const sigW = 220; const sigH = 90;
    const sx = doc.page.width - doc.page.margins.right - sigW;
    const sy = y;
    doc.text(String(L.zip || ''), sx, sy);
    doc.text(`${String(L.companyAddressPrefix || '')}${String(pdf?.companyAddress || '')}`, sx, sy + 20);
    doc.text(`${String(L.companyNamePrefix || '')}${String(pdf?.companyName || '')}`, sx, sy + 40);
    doc.text(`${String(L.companyRepPrefix || '')}${String(pdf?.companyRep || '')}`, sx, sy + 60);
    // 动态印章：默认锚定公司信息区右侧上方，支持 offset/绝对坐标；不绘制边框
    try {
      const sealCfg = pdf?.seal || {};
      const defaultSize = Math.max(40, Number(sealCfg.size ?? 56.7)); // 2cm≈56.7pt
      let ox = Number.isFinite(sealCfg.x) ? Number(sealCfg.x) : (sx + 150);
      let oy = Number.isFinite(sealCfg.y) ? Number(sealCfg.y) : (sy + 6); // 轻微覆盖文字
      ox += Number(sealCfg.offsetX || 0);
      oy += Number(sealCfg.offsetY || 0);
      const tmpPdf = { ...pdf, seal: { ...sealCfg, x: ox, y: oy, size: defaultSize } };
      drawSealOverlay(doc, tmpPdf);
    } catch {}
    // 页脚 DocID（小字号）
    try { drawFooterDocId(doc, pdf); } catch {}
    return true;
  }

  // jp_resignation_form: 退職証明書（表格+退職理由欄）
  if (name === 'jp_resignation_form') {
    const L = Object.assign({
      title: '退職証明書',
      dear: '殿',
      statement: '以下の事由により、貴殿は当社を退職したことを証明します。',
      name: '氏　名',
      birthday: '生 年 月 日',
      hireDate: '入 社 年 月 日',
      separationDate: '退 職 年 月 日',
      position: '役 職 / 地 位',
      reason: '退　職　理　由',
      zip: '所在地',
      companyNamePrefix: '事業所名',
      companyRepPrefix: '事業主名',
      yearSuffix: '年', monthSuffix: '月', daySuffix: '日'
    }, pdf?.labels || {});

    const x = doc.page.margins.left;
    let y = doc.page.margins.top + 10;
    // 标题
    try { doc.fontSize(18).text(String(pdf.title || L.title), { align: 'center' }); } catch {}
    // 右上日期
    const dateText = String(pdf?.date || '');
    if (dateText) { doc.fontSize(11).text(dateText, doc.page.width - doc.page.margins.right - 140, doc.page.margins.top + 6, { width: 140, align: 'right' }); }

    y += 26;
    // 氏名 殿 行
    const nameLineH = 26;
    doc.fontSize(12).text('氏名', x, y);
    const nameVal = String(pdf?.employee?.name || '');
    doc.text(nameVal + '　' + L.dear, x + 40, y, { width: pageWidth - 40 });
    y += nameLineH + 8;

    // 说明文字
    doc.fontSize(11).text(String(L.statement), x, y);
    y += 20;

    const fullW = pageWidth;
    const lh = 34;
    const colLeftW = 120;
    const colRightW = fullW - colLeftW;

    function row(label, drawContent, height = lh) {
      doc.rect(x, y, fullW, height).stroke();
      doc.moveTo(x + colLeftW, y).lineTo(x + colLeftW, y + height).stroke();
      doc.fontSize(12).text(label, x + 10, y + 9, { width: colLeftW - 20 });
      drawContent(x + colLeftW, y, colRightW, height);
      y += height;
    }

    // 氏名（表内再次记录）
    row(L.name, (cx, cy, w, h) => { doc.text(nameVal, cx + 12, cy + 9, { width: w - 24 }); });
    // 生年月日
    row(L.birthday, (cx, cy, w) => {
      const parts = (pdf?.employee?.birthday || '').split('-');
      const t = `${parts[0]||''}${L.yearSuffix} ${parts[1]||''}${L.monthSuffix} ${parts[2]||''}${L.daySuffix}`;
      doc.text(t, cx + 12, cy + 9, { width: w - 24 });
    });
    // 入社年月日
    row(L.hireDate, (cx, cy, w) => {
      const parts = (pdf?.employment?.startDate || '').split('-');
      const t = `${parts[0]||''}${L.yearSuffix} ${parts[1]||''}${L.monthSuffix} ${parts[2]||''}${L.daySuffix}`;
      doc.text(t, cx + 12, cy + 9, { width: w - 24 });
    });
    // 退職年月日
    row(L.separationDate, (cx, cy, w) => {
      const parts = (pdf?.employment?.endDate || '').split('-');
      const t = `${parts[0]||''}${L.yearSuffix} ${parts[1]||''}${L.monthSuffix} ${parts[2]||''}${L.daySuffix}`;
      doc.text(t, cx + 12, cy + 9, { width: w - 24 });
    });
    // 役職・地位
    row(L.position, (cx, cy, w) => { doc.text(String(pdf?.employment?.position || ''), cx + 12, cy + 9, { width: w - 24 }); });

    // 退職理由（大块）
    const reasonH = 180;
    doc.rect(x, y, fullW, reasonH).stroke();
    doc.moveTo(x + colLeftW, y).lineTo(x + colLeftW, y + reasonH).stroke();
    doc.fontSize(12).text(L.reason, x + 10, y + 9, { width: colLeftW - 20 });
    doc.fontSize(11).text(String(pdf?.reason || ''), x + colLeftW + 12, y + 9, { width: colRightW - 24 });
    y += reasonH + 30;

    // 会社情報 + 印
    // 公司信息区整体左移约 3cm（可通过 companyBlockOffsetX 覆盖）
    const companyBlockOffsetX = Number.isFinite(pdf?.companyBlockOffsetX) ? Number(pdf.companyBlockOffsetX) : -(28.35 * 3);
    const sx = doc.page.margins.left + 320 + companyBlockOffsetX;
    const sy = y;
    doc.fontSize(12).text(String(L.zip), sx, sy);
    doc.text(String(pdf?.companyAddress || ''), sx + 60, sy);
    doc.text(String(L.companyNamePrefix), sx, sy + 20); doc.text(String(pdf?.companyName || ''), sx + 60, sy + 20);
    doc.text(String(L.companyRepPrefix), sx, sy + 40); doc.text(String(pdf?.companyRep || ''), sx + 60, sy + 40);
    // 印章（覆盖右侧，支持外部配置）
    try {
      const sealCfg = pdf?.seal || {};
      const size = Math.max(40, Number(sealCfg.size ?? 56.7));
      // 锚定在「事業主名」行，默认在当前基础上向左偏移 1.5cm 并略微覆盖
      const baseOx = sx + 200; // 事業主名文字右侧附近
      const baseOy = sy + 40 - 6; // 对齐到“事業主名”行
      const defaultLeftOffset = -(28.35 * 1.5);
      const ox = Number.isFinite(sealCfg.x) ? Number(sealCfg.x) : (baseOx + defaultLeftOffset + Number(sealCfg.offsetX || 0));
      const oy = Number.isFinite(sealCfg.y) ? Number(sealCfg.y) : (baseOy + Number(sealCfg.offsetY || 0));
      const tmpPdf = { ...pdf, seal: { ...sealCfg, x: ox, y: oy, size } };
      drawSealOverlay(doc, tmpPdf);
    } catch {}

    // 页脚 DocID（小字号）
    try { drawFooterDocId(doc, pdf); } catch {}
    return true;
  }

  // staffing_invoice: 人才派遣请求书（日本标准格式）
  if (name === 'staffing_invoice') {
    const L = Object.assign({
      title: '請　求　書',
      invoiceNo: '請求書番号',
      invoiceDate: '請求日',
      dueDate: 'お支払期限',
      clientLabel: '御中',
      fromLabel: '請求元',
      subtotalLabel: '小計',
      taxLabel: '消費税',
      totalLabel: '合計金額',
      remarkLabel: '備考',
      bankLabel: 'お振込先',
      itemNo: 'No.',
      itemDescription: '品名・摘要',
      itemQuantity: '数量',
      itemUnit: '単位',
      itemUnitPrice: '単価',
      itemAmount: '金額',
      currency: '¥',
      thankYou: '上記の通りご請求申し上げます。'
    }, pdf?.labels || {});

    const x = doc.page.margins.left;
    const rightX = doc.page.width - doc.page.margins.right;
    let y = doc.page.margins.top;

    // 标题
    doc.fontSize(24).text(L.title, x, y, { width: pageWidth, align: 'center' });
    y += 40;

    // 请求书番号・请求日（右上角）
    const infoX = rightX - 200;
    doc.fontSize(10);
    doc.text(`${L.invoiceNo}: ${pdf?.invoiceNo || ''}`, infoX, y, { width: 200, align: 'left' });
    y += 16;
    doc.text(`${L.invoiceDate}: ${pdf?.invoiceDate || ''}`, infoX, y, { width: 200, align: 'left' });
    y += 16;
    doc.text(`${L.dueDate}: ${pdf?.dueDate || ''}`, infoX, y, { width: 200, align: 'left' });
    y += 30;

    // 客户信息（左侧）
    const clientY = doc.page.margins.top + 40;
    doc.fontSize(14).text(String(pdf?.clientName || ''), x, clientY, { width: 280 });
    doc.fontSize(12).text(L.clientLabel, x, clientY + 20);
    if (pdf?.clientAddress) {
      doc.fontSize(10).text(pdf.clientAddress, x, clientY + 38, { width: 280 });
    }
    y = Math.max(y, clientY + 70);

    // 合计金额（大字体突出显示）
    doc.fontSize(10).text(L.totalLabel, x, y);
    const totalAmount = Number(pdf?.totalAmount || 0);
    doc.fontSize(20).fillColor('#000000').text(`${L.currency}${totalAmount.toLocaleString('ja-JP')}`, x + 80, y - 4, { width: 200 });
    doc.moveTo(x, y + 24).lineTo(x + 280, y + 24).lineWidth(2).stroke();
    doc.lineWidth(1);
    y += 40;

    // 请求元信息（右侧）
    const fromX = rightX - 220;
    const fromY = y - 80;
    doc.fontSize(10);
    if (pdf?.companyZip) doc.text(`〒${pdf.companyZip}`, fromX, fromY);
    if (pdf?.companyAddress) doc.text(pdf.companyAddress, fromX, fromY + 14, { width: 220 });
    doc.fontSize(11).text(pdf?.companyName || '', fromX, fromY + 32, { width: 220 });
    if (pdf?.companyTel) doc.fontSize(9).text(`TEL: ${pdf.companyTel}`, fromX, fromY + 48);
    if (pdf?.companyEmail) doc.fontSize(9).text(`Email: ${pdf.companyEmail}`, fromX, fromY + 60);

    // 印章叠加在公司信息旁边
    try {
      const sealCfg = pdf?.seal || {};
      const size = Math.max(40, Number(sealCfg.size ?? 56));
      const ox = Number.isFinite(sealCfg.x) ? Number(sealCfg.x) : (fromX + 160 + Number(sealCfg.offsetX || 0));
      const oy = Number.isFinite(sealCfg.y) ? Number(sealCfg.y) : (fromY + 10 + Number(sealCfg.offsetY || 0));
      const tmpPdf = { ...pdf, seal: { ...sealCfg, x: ox, y: oy, size } };
      drawSealOverlay(doc, tmpPdf);
    } catch {}

    // 明细表格
    y += 20;
    const tableX = x;
    const colWidths = [30, 220, 50, 40, 70, 80]; // No, 品名, 数量, 単位, 単価, 金額
    const tableWidth = colWidths.reduce((a, b) => a + b, 0);
    const rowHeight = 24;

    // 表头
    doc.fillColor('#f0f0f0').rect(tableX, y, tableWidth, rowHeight).fill();
    doc.fillColor('#000000').fontSize(9);
    let cx = tableX;
    const headers = [L.itemNo, L.itemDescription, L.itemQuantity, L.itemUnit, L.itemUnitPrice, L.itemAmount];
    headers.forEach((h, i) => {
      doc.text(h, cx + 4, y + 7, { width: colWidths[i] - 8, align: i >= 2 ? 'right' : 'left' });
      cx += colWidths[i];
    });
    doc.rect(tableX, y, tableWidth, rowHeight).stroke();
    y += rowHeight;

    // 明细行
    const lines = pdf?.lines || [];
    lines.forEach((line, idx) => {
      cx = tableX;
      const vals = [
        String(idx + 1),
        String(line.description || ''),
        String(line.quantity ?? ''),
        String(line.unit || ''),
        line.unitPrice != null ? `${L.currency}${Number(line.unitPrice).toLocaleString('ja-JP')}` : '',
        line.amount != null ? `${L.currency}${Number(line.amount).toLocaleString('ja-JP')}` : ''
      ];
      vals.forEach((v, i) => {
        doc.text(v, cx + 4, y + 7, { width: colWidths[i] - 8, align: i >= 2 ? 'right' : 'left' });
        cx += colWidths[i];
      });
      doc.rect(tableX, y, tableWidth, rowHeight).stroke();
      y += rowHeight;
    });

    // 空白行（至少显示几行）
    const minRows = 5;
    for (let i = lines.length; i < minRows; i++) {
      doc.rect(tableX, y, tableWidth, rowHeight).stroke();
      y += rowHeight;
    }

    // 小计・消费税・合计（右侧）
    y += 10;
    const sumX = tableX + tableWidth - 180;
    const sumW = 180;
    const subtotal = Number(pdf?.subtotal || 0);
    const taxAmount = Number(pdf?.taxAmount || 0);
    const taxRate = Number(pdf?.taxRate || 0.10);

    doc.fontSize(10);
    doc.text(L.subtotalLabel, sumX, y, { width: 80 });
    doc.text(`${L.currency}${subtotal.toLocaleString('ja-JP')}`, sumX + 80, y, { width: 100, align: 'right' });
    y += 18;
    doc.text(`${L.taxLabel} (${(taxRate * 100).toFixed(0)}%)`, sumX, y, { width: 80 });
    doc.text(`${L.currency}${taxAmount.toLocaleString('ja-JP')}`, sumX + 80, y, { width: 100, align: 'right' });
    y += 18;
    doc.fontSize(12).fillColor('#000000');
    doc.text(L.totalLabel, sumX, y, { width: 80 });
    doc.text(`${L.currency}${totalAmount.toLocaleString('ja-JP')}`, sumX + 80, y, { width: 100, align: 'right' });
    doc.moveTo(sumX, y + 16).lineTo(sumX + sumW, y + 16).lineWidth(1.5).stroke();
    doc.lineWidth(1);
    y += 30;

    // 备注
    if (pdf?.remarks) {
      doc.fontSize(9).text(`${L.remarkLabel}: ${pdf.remarks}`, tableX, y, { width: tableWidth });
      y += 20;
    }

    // 振込先
    if (pdf?.bankInfo) {
      doc.fontSize(9).text(`${L.bankLabel}:`, tableX, y);
      y += 14;
      doc.text(pdf.bankInfo, tableX + 10, y, { width: tableWidth - 20 });
    }

    // 页脚 DocID
    try { drawFooterDocId(doc, pdf); } catch {}
    return true;
  }

  // 其他模板：仅叠加印章与页脚 DocID
  try { drawSealOverlay(doc, pdf); } catch {}
  try { drawFooterDocId(doc, pdf); } catch {}
  return false;
}

async function renderWithPdfKit(pdf) {
  const fontPath = await resolveCjkFontPath();
  const doc = new PDFKit({ size: 'A4', margin: 48 });
  const chunks = [];
  return await new Promise((resolve, reject) => {
    doc.on('data', (c) => chunks.push(c));
    doc.on('error', reject);
    doc.on('end', () => resolve(Buffer.concat(chunks)));
    try {
      if (fontPath) {
        doc.font(fontPath);
        try { console.log('[pdf] using font:', fontPath); } catch {}
      }
    } catch {}

    // 模板分支（支持异步渲染）
    (async () => {
      const usedTemplate = await renderTemplate(doc, pdf || {});
      if (!usedTemplate) {
      const pageWidth = doc.page.width - doc.page.margins.left - doc.page.margins.right;
      doc.fontSize(20).text(String(pdf?.title || '証明書'), { width: pageWidth, align: 'left' });
      doc.moveDown(0.5);
      const lines = [];
      if (pdf?.company) lines.push(`公司: ${pdf.company}`);
      if (pdf?.employee) lines.push(`员工: ${(pdf.employee?.name||'')} (${pdf.employee?.code||''})`);
      if (pdf?.type) lines.push(`类型: ${pdf.type}`);
      if (pdf?.purpose) lines.push(`用途: ${pdf.purpose}`);
      if (pdf?.date) lines.push(`出具日期: ${pdf.date}`);
      if (lines.length) doc.fontSize(12).text(lines.join('\n'), { width: pageWidth });
      if (lines.length) doc.moveDown(0.5);
      const bodyText = String(pdf?.bodyText || '');
      if (bodyText) doc.fontSize(12).text(bodyText, { width: pageWidth });
        try { drawSealOverlay(doc, pdf); } catch {}
        try { drawFooterDocId(doc, pdf); } catch {}
      }
      doc.end();
    })().catch(reject);
  });
}

// 发送带 PDF 附件的邮件
// 环境变量：SMTP_HOST, SMTP_PORT, SMTP_USER, SMTP_PASS, SMTP_SECURE(optional), SMTP_FROM
app.post('/email/pdf', async (req, res) => {
  try {
    const { to, subject, textBody, html, pdf } = req.body || {};
    if (!to || !subject || !pdf) return res.status(400).json({ error: 'to/subject/pdf required' });

    const host = process.env.SMTP_HOST;
    const port = Number(process.env.SMTP_PORT || '587');
    const user = process.env.SMTP_USER;
    const pass = process.env.SMTP_PASS;
    const secure = String(process.env.SMTP_SECURE || '').toLowerCase() === 'true';
    const from = process.env.SMTP_FROM || user;
    if (!host || !user || !pass) return res.status(501).json({ error: 'SMTP not configured' });

    const transporter = nodemailer.createTransport({ host, port, secure, auth: { user, pass } });

    // 构建 PDF
    const pdfBytes = await renderWithPdfKit(pdf);

    const toList = Array.isArray(to) ? to : [to];
    const safeTitle = String((pdf && pdf.title) || subject || 'certificate');
    const mailOptions = {
      from,
      to: toList,
      subject,
      attachments: [
        { filename: `${safeTitle}.pdf`, content: Buffer.from(pdfBytes), contentType: 'application/pdf' }
      ]
    };
    const infoText = textBody || (html ? undefined : '请查收 PDF 附件。');
    if (infoText) mailOptions.text = infoText;
    if (html) mailOptions.html = String(html);

    console.log('[email/pdf] sending', { to: toList, subject });
    const mail = await transporter.sendMail(mailOptions);
    console.log('[email/pdf] sent', { id: mail.messageId, response: mail.response });
    res.json({ ok: true, id: mail.messageId, response: mail.response });
  } catch (e) {
    console.error('[email/pdf] error', e);
    res.status(500).json({ error: String(e.message || e) });
  }
});

// 仅生成 PDF 并返回 base64（不发送邮件），使用支持中文的字体
app.post('/pdf/render', async (req, res) => {
  try {
    const { pdf } = req.body || {};
    if (!pdf) return res.status(400).json({ error: 'pdf payload required' });
    const pdfBytes = await renderWithPdfKit(pdf);
    const b64 = Buffer.from(pdfBytes).toString('base64');
    const safeTitle = String((pdf && pdf.title) || 'certificate');
    res.json({ ok: true, filename: `${safeTitle}.pdf`, data: b64, contentType: 'application/pdf' });
  } catch (e) {
    res.status(500).json({ error: String(e.message || e) });
  }
});

// 健康检查
app.get('/health', (req, res) => res.json({ ok: true }));

app.listen(3030, () => console.log('Agent service on :3030'));

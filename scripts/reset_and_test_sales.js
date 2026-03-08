const https = require('https');
const fs = require('fs');
const path = require('path');
const { PDFDocument, rgb, StandardFonts } = require('pdf-lib');
const fontkit = require('@pdf-lib/fontkit');

const API_HOST = 'yanxia-api.azurewebsites.net';
let TOKEN = '';

function req(method, apiPath, body) {
  return new Promise((resolve, reject) => {
    const data = body ? JSON.stringify(body) : '';
    const opts = {
      hostname: API_HOST, path: apiPath, method,
      headers: {
        'Content-Type': 'application/json',
        'x-company-code': 'JP01',
        ...(TOKEN ? { 'Authorization': `Bearer ${TOKEN}` } : {}),
        ...(data ? { 'Content-Length': Buffer.byteLength(data) } : {})
      }
    };
    const r = https.request(opts, res => {
      let b = '';
      res.on('data', c => b += c);
      res.on('end', () => {
        try { resolve({ status: res.statusCode, data: JSON.parse(b) }); }
        catch { resolve({ status: res.statusCode, data: b }); }
      });
    });
    r.on('error', reject);
    if (data) r.write(data);
    r.end();
  });
}

// ========== Step 1: Login ==========
async function login() {
  console.log('=== Step 1: ログイン ===');
  const res = await req('POST', '/auth/login', {
    companyCode: 'JP01', employeeCode: 'admin', password: 'test1234'
  });
  if (res.status >= 300) {
    console.log('  ✗ ログイン失敗:', res.status, JSON.stringify(res.data).substring(0, 200));
    process.exit(1);
  }
  TOKEN = res.data.token;
  console.log(`  ✓ ログイン成功 (name: ${res.data.name})\n`);
}

// ========== Step 2: Delete existing data ==========
async function deleteExistingData() {
  console.log('=== Step 2: JP01 既存受注データ削除 ===');

  const entities = ['sales_invoice', 'delivery_note', 'sales_order'];
  for (const entity of entities) {
    const res = await req('POST', `/objects/${entity}/search`, { page: 1, pageSize: 200, where: [], orderBy: [] });
    if (res.status < 300 && res.data?.data?.length > 0) {
      const items = res.data.data;
      console.log(`  ${entity}: ${items.length}件 検出 → 削除中...`);
      let deleted = 0;
      for (const item of items) {
        const delRes = await req('DELETE', `/objects/${entity}/${item.id}`);
        if (delRes.status < 300) deleted++;
      }
      console.log(`  ✓ ${entity}: ${deleted}/${items.length}件 削除完了`);
    } else {
      console.log(`  ${entity}: 0件 (削除不要)`);
    }
  }
  console.log('');
}

// ========== Step 3: Ensure business partners exist ==========
async function ensureBusinessPartners() {
  console.log('=== Step 3: 得意先マスタ確認・作成 ===');

  const customers = [
    {
      code: 'C001', name: '東京テクノロジー株式会社',
      address: { prefecture: '東京都', address: '千代田区大手町1-1-1 大手町ビル5F', postalCode: '100-0004' },
      paymentTerms: { cutOffDay: 31, paymentMonth: 1, paymentDay: 31, description: '月末締め翌月末払い' },
    },
    {
      code: 'C002', name: '大阪デジタルソリューションズ合同会社',
      address: { prefecture: '大阪府', address: '大阪市北区梅田2-2-2 梅田スカイビル10F', postalCode: '530-0001' },
      paymentTerms: { cutOffDay: 31, paymentMonth: 2, paymentDay: 31, description: '月末締め翌々月末払い' },
    },
    {
      code: 'C003', name: '名古屋エレクトロニクス株式会社',
      address: { prefecture: '愛知県', address: '名古屋市中村区名駅3-3-3', postalCode: '450-0002' },
      paymentTerms: { cutOffDay: 31, paymentMonth: 1, paymentDay: 31, description: '月末締め翌月末払い' },
    },
  ];

  for (const c of customers) {
    const search = await req('POST', '/objects/businesspartner/search', {
      page: 1, pageSize: 1,
      where: [{ field: 'partner_code', op: 'eq', value: c.code }],
      orderBy: []
    });

    if (search.status < 300 && search.data?.data?.length > 0) {
      console.log(`  ✓ ${c.code} ${c.name} → 既存`);
      continue;
    }

    const payload = {
      code: c.code, name: c.name,
      flags: { customer: true, vendor: false },
      address: c.address,
      paymentTerms: c.paymentTerms,
      status: 'active'
    };
    const res = await req('POST', '/objects/businesspartner', { payload });
    if (res.status < 300) {
      console.log(`  ✓ ${c.code} ${c.name} → 新規作成`);
    } else {
      console.log(`  ✗ ${c.code} 作成失敗: ${res.status} ${JSON.stringify(res.data).substring(0, 200)}`);
    }
  }
  console.log('');
}

// ========== Step 4: Ensure materials exist ==========
async function ensureMaterials() {
  console.log('=== Step 4: 品目マスタ確認・作成 ===');

  const materials = [
    { code: 'ELEC-001', name: 'ノートパソコン 15.6型', baseUom: '台', price: 128000, category: '電子機器' },
    { code: 'ELEC-002', name: 'ワイヤレスマウス', baseUom: '個', price: 3980, category: '周辺機器' },
    { code: 'ELEC-003', name: '27インチ 4Kモニター', baseUom: '台', price: 54800, category: '周辺機器' },
    { code: 'ELEC-004', name: 'USBドッキングステーション', baseUom: '個', price: 18500, category: '周辺機器' },
    { code: 'ELEC-005', name: 'ワイヤレスキーボード', baseUom: '個', price: 8900, category: '周辺機器' },
    { code: 'ELEC-006', name: 'ウェブカメラ HD1080p', baseUom: 'EA', price: 6800, category: '周辺機器' },
    { code: 'ELEC-007', name: 'USBメモリ 128GB', baseUom: 'EA', price: 2480, category: 'ストレージ' },
    { code: 'ELEC-008', name: 'LANケーブル Cat6 3m', baseUom: 'EA', price: 680, category: 'ケーブル' },
  ];

  for (const m of materials) {
    const search = await req('POST', '/objects/material/search', {
      page: 1, pageSize: 1,
      where: [{ field: 'material_code', op: 'eq', value: m.code }],
      orderBy: []
    });

    if (search.status < 300 && search.data?.data?.length > 0) {
      console.log(`  ✓ ${m.code} ${m.name} → 既存`);
      continue;
    }

    const payload = {
      code: m.code, name: m.name, baseUom: m.baseUom,
      description: m.category, materialType: 'Product',
      batchManagement: false, spec: '', status: 'Active'
    };
    const res = await req('POST', '/objects/material', { payload });
    if (res.status < 300) {
      console.log(`  ✓ ${m.code} ${m.name} → 新規作成`);
    } else {
      console.log(`  ✗ ${m.code} 作成失敗: ${res.status} ${JSON.stringify(res.data).substring(0, 120)}`);
    }
  }
  console.log('');
}

// ========== Step 5: Create sales orders ==========
async function createSalesOrders() {
  console.log('=== Step 5: 受注テストデータ作成 ===');

  const orders = [
    {
      orderDate: '2026-02-15',
      partnerCode: 'C001', partnerName: '東京テクノロジー株式会社',
      requestedDeliveryDate: '2026-03-01',
      currency: 'JPY', status: 'new',
      deliveryAddress: '東京都千代田区大手町1-1-1 大手町ビル5F',
      note: '新入社員用PC・周辺機器セット',
      lines: [
        { lineNo: 1, materialCode: 'ELEC-001', materialName: 'ノートパソコン 15.6型', quantity: 5, uom: '台', unitPrice: 128000, taxRate: 10 },
        { lineNo: 2, materialCode: 'ELEC-003', materialName: '27インチ 4Kモニター', quantity: 5, uom: '台', unitPrice: 54800, taxRate: 10 },
        { lineNo: 3, materialCode: 'ELEC-002', materialName: 'ワイヤレスマウス', quantity: 5, uom: '個', unitPrice: 3980, taxRate: 10 },
        { lineNo: 4, materialCode: 'ELEC-005', materialName: 'ワイヤレスキーボード', quantity: 5, uom: '個', unitPrice: 8900, taxRate: 10 },
      ],
    },
    {
      orderDate: '2026-02-20',
      partnerCode: 'C002', partnerName: '大阪デジタルソリューションズ合同会社',
      requestedDeliveryDate: '2026-03-10',
      currency: 'JPY', status: 'new',
      note: 'リモートワーク環境整備',
      lines: [
        { lineNo: 1, materialCode: 'ELEC-004', materialName: 'USBドッキングステーション', quantity: 15, uom: '個', unitPrice: 18500, taxRate: 10 },
        { lineNo: 2, materialCode: 'ELEC-006', materialName: 'ウェブカメラ HD1080p', quantity: 15, uom: '個', unitPrice: 6800, taxRate: 10 },
        { lineNo: 3, materialCode: 'ELEC-008', materialName: 'LANケーブル Cat6 3m', quantity: 30, uom: '本', unitPrice: 680, taxRate: 10 },
      ],
    },
    {
      orderDate: '2026-03-01',
      partnerCode: 'C003', partnerName: '名古屋エレクトロニクス株式会社',
      requestedDeliveryDate: '2026-03-20',
      currency: 'JPY', status: 'new',
      deliveryAddress: '愛知県名古屋市中村区名駅3-3-3',
      note: '営業部デスク環境更新',
      lines: [
        { lineNo: 1, materialCode: 'ELEC-001', materialName: 'ノートパソコン 15.6型', quantity: 3, uom: '台', unitPrice: 128000, taxRate: 10 },
        { lineNo: 2, materialCode: 'ELEC-003', materialName: '27インチ 4Kモニター', quantity: 3, uom: '台', unitPrice: 54800, taxRate: 10 },
        { lineNo: 3, materialCode: 'ELEC-004', materialName: 'USBドッキングステーション', quantity: 3, uom: '個', unitPrice: 18500, taxRate: 10 },
        { lineNo: 4, materialCode: 'ELEC-007', materialName: 'USBメモリ 128GB', quantity: 10, uom: '個', unitPrice: 2480, taxRate: 10 },
      ],
    },
  ];

  const createdIds = [];
  for (let i = 0; i < orders.length; i++) {
    const o = orders[i];
    for (const line of o.lines) {
      line.amount = line.quantity * line.unitPrice;
      line.taxAmount = Math.round(line.amount * line.taxRate / 100);
      line.note = '';
    }
    const taxTotal = o.lines.reduce((s, l) => s + l.taxAmount, 0);
    const amtTotal = o.lines.reduce((s, l) => s + l.amount + l.taxAmount, 0);
    o.taxAmountTotal = taxTotal;
    o.amountTotal = amtTotal;

    const res = await req('POST', '/objects/sales_order', { payload: o });
    if (res.status < 300) {
      createdIds.push(res.data?.id);
      const soNo = res.data?.payload?.soNo || res.data?.so_no || `(id:${res.data?.id?.substring(0, 8)})`;
      console.log(`  ✓ 受注${i + 1} 作成: ${soNo} / ${o.partnerName} / ¥${amtTotal.toLocaleString()}`);
    } else {
      console.log(`  ✗ 受注${i + 1} 作成失敗: ${res.status} ${JSON.stringify(res.data).substring(0, 150)}`);
    }
  }
  console.log('');
  return createdIds;
}

// ========== Step 6: Generate test PDF ==========
async function generateTestPdf() {
  console.log('=== Step 6: テスト用注文書PDF作成 ===');

  const fontPath = 'C:\\Windows\\Fonts\\yumin.ttf';
  let fontBytes;
  try {
    fontBytes = fs.readFileSync(fontPath);
  } catch {
    console.log(`  ! フォント ${fontPath} が見つかりません。代替フォントを検索中...`);
    const altFonts = ['C:\\Windows\\Fonts\\msgothic.ttc', 'C:\\Windows\\Fonts\\YuGothR.ttc'];
    for (const alt of altFonts) {
      try { fontBytes = fs.readFileSync(alt); console.log(`  → ${alt} を使用`); break; } catch {}
    }
  }

  const pdfDoc = await PDFDocument.create();
  pdfDoc.registerFontkit(fontkit);

  let jpFont;
  if (fontBytes) {
    try {
      jpFont = await pdfDoc.embedFont(fontBytes);
    } catch {
      console.log('  ! 日本語フォント埋め込み失敗。Helveticaで代替（注文内容はASCII+ラベル英語で生成）');
      jpFont = null;
    }
  }

  const fallbackFont = await pdfDoc.embedFont(StandardFonts.Helvetica);
  const fallbackBold = await pdfDoc.embedFont(StandardFonts.HelveticaBold);
  const font = jpFont || fallbackFont;
  const boldFont = jpFont || fallbackBold;

  const page = pdfDoc.addPage([595.28, 841.89]); // A4
  const { width, height } = page.getSize();
  let y = height - 50;

  const drawText = (text, x, yPos, size = 10, f = font, color = rgb(0, 0, 0)) => {
    try { page.drawText(text, { x, y: yPos, size, font: f, color }); }
    catch { page.drawText(text.replace(/[^\x20-\x7E]/g, '?'), { x, y: yPos, size, font: fallbackFont, color }); }
  };

  const drawLine = (x1, y1, x2, y2, thickness = 0.5) => {
    page.drawLine({ start: { x: x1, y: y1 }, end: { x: x2, y: y2 }, thickness, color: rgb(0, 0, 0) });
  };

  // Title
  if (jpFont) {
    drawText('注 文 書', width / 2 - 50, y, 24, boldFont);
  } else {
    drawText('PURCHASE ORDER', width / 2 - 80, y, 22, fallbackBold);
  }
  y -= 15;
  drawLine(50, y, width - 50, y, 2);
  y -= 30;

  // Order info
  const labelX = 60;
  const valX = 200;
  const rightLabelX = 340;
  const rightValX = 440;

  const jp = !!jpFont;

  drawText(jp ? '注文番号:' : 'Order No:', labelX, y, 10, boldFont);
  drawText('PO-2026-0315', valX, y, 10);
  drawText(jp ? '注文日:' : 'Order Date:', rightLabelX, y, 10, boldFont);
  drawText('2026-03-15', rightValX, y, 10);
  y -= 18;

  drawText(jp ? '希望納期:' : 'Delivery Date:', labelX, y, 10, boldFont);
  drawText('2026-04-01', valX, y, 10);
  y -= 30;

  // Buyer info
  drawText(jp ? '【発注元】' : '[Buyer]', labelX, y, 11, boldFont);
  drawText(jp ? '【納入先】' : '[Ship To]', rightLabelX, y, 11, boldFont);
  y -= 18;
  drawText(jp ? '名古屋エレクトロニクス株式会社' : 'Nagoya Electronics Co., Ltd.', labelX, y, 10);
  drawText(jp ? '名古屋エレクトロニクス株式会社' : 'Nagoya Electronics Co., Ltd.', rightLabelX, y, 10);
  y -= 15;
  drawText(jp ? '担当: 鈴木一郎' : 'Contact: Suzuki Ichiro', labelX, y, 9);
  drawText(jp ? '愛知県名古屋市中村区名駅3-3-3' : '3-3-3 Meieki, Nakamura-ku, Nagoya', rightLabelX, y, 9);
  y -= 15;
  drawText(jp ? '電話: 052-1111-2222' : 'Tel: 052-1111-2222', labelX, y, 9);
  y -= 25;

  // Table header
  drawLine(50, y, width - 50, y, 1);
  y -= 15;

  const cols = [
    { label: jp ? 'No.' : 'No.', x: 55, w: 30 },
    { label: jp ? '品目コード' : 'Item Code', x: 85, w: 80 },
    { label: jp ? '品名' : 'Item Name', x: 165, w: 160 },
    { label: jp ? '数量' : 'Qty', x: 330, w: 45 },
    { label: jp ? '単位' : 'Unit', x: 375, w: 35 },
    { label: jp ? '単価' : 'Price', x: 415, w: 60 },
    { label: jp ? '金額' : 'Amount', x: 480, w: 65 },
  ];

  for (const col of cols) {
    drawText(col.label, col.x, y, 9, boldFont);
  }
  y -= 8;
  drawLine(50, y, width - 50, y, 0.5);
  y -= 15;

  // Line items
  const lineItems = [
    { code: 'ELEC-001', name: jp ? 'ノートパソコン 15.6型' : 'Laptop PC 15.6"', qty: 8, uom: jp ? '台' : 'pc', price: 128000 },
    { code: 'ELEC-003', name: jp ? '27インチ 4Kモニター' : '27" 4K Monitor', qty: 8, uom: jp ? '台' : 'pc', price: 54800 },
    { code: 'ELEC-005', name: jp ? 'ワイヤレスキーボード' : 'Wireless Keyboard', qty: 8, uom: jp ? '個' : 'pc', price: 8900 },
    { code: 'ELEC-002', name: jp ? 'ワイヤレスマウス' : 'Wireless Mouse', qty: 8, uom: jp ? '個' : 'pc', price: 3980 },
    { code: 'ELEC-004', name: jp ? 'USBドッキングステーション' : 'USB Docking Station', qty: 8, uom: jp ? '個' : 'pc', price: 18500 },
  ];

  let subtotal = 0;
  lineItems.forEach((item, idx) => {
    const amount = item.qty * item.price;
    subtotal += amount;

    drawText(`${idx + 1}`, cols[0].x, y, 9);
    drawText(item.code, cols[1].x, y, 9);
    drawText(item.name, cols[2].x, y, 9);
    drawText(`${item.qty}`, cols[3].x + 20, y, 9);
    drawText(item.uom, cols[4].x, y, 9);
    drawText(`${item.price.toLocaleString()}`, cols[5].x, y, 9);
    drawText(`${amount.toLocaleString()}`, cols[6].x, y, 9);
    y -= 18;
  });

  drawLine(50, y, width - 50, y, 0.5);
  y -= 20;

  // Totals
  const taxAmount = Math.round(subtotal * 0.10);
  const total = subtotal + taxAmount;

  const totalsX = 380;
  drawText(jp ? '小計 (税抜):' : 'Subtotal:', totalsX, y, 10, boldFont);
  drawText(`${subtotal.toLocaleString()}`, cols[6].x, y, 10);
  y -= 18;
  drawText(jp ? '消費税 (10%):' : 'Tax (10%):', totalsX, y, 10, boldFont);
  drawText(`${taxAmount.toLocaleString()}`, cols[6].x, y, 10);
  y -= 18;
  drawLine(totalsX, y + 14, width - 50, y + 14, 1);
  drawText(jp ? '合計:' : 'Total:', totalsX, y, 12, boldFont);
  drawText(`${total.toLocaleString()}`, cols[6].x, y, 12, boldFont);
  y -= 30;

  // Notes
  drawText(jp ? '備考:' : 'Notes:', labelX, y, 10, boldFont);
  y -= 15;
  drawText(jp ? '新オフィス開設に伴う情報機器一括発注です。' : 'Bulk order for new office setup - IT equipment.', labelX, y, 9);
  y -= 15;
  drawText(jp ? '納品先住所への直接配送をお願いいたします。' : 'Please deliver directly to the ship-to address.', labelX, y, 9);
  y -= 15;
  drawText(jp ? '支払条件: 月末締め翌月末払い' : 'Payment terms: End of month, payment by end of next month', labelX, y, 9);

  const pdfBytes = await pdfDoc.save();
  const outputPath = path.join(__dirname, '..', 'test_purchase_order.pdf');
  fs.writeFileSync(outputPath, pdfBytes);
  console.log(`  ✓ PDF生成完了: ${outputPath} (${(pdfBytes.length / 1024).toFixed(1)} KB)`);
  console.log(`    → 発注元: ${jp ? '名古屋エレクトロニクス株式会社 (C003)' : 'Nagoya Electronics (C003)'}`);
  console.log(`    → 品目: 5件 (ELEC-001, 003, 005, 002, 004)`);
  console.log(`    → 合計: ¥${total.toLocaleString()}`);
  console.log('');
}

// ========== Step 7: Verify data ==========
async function verifyData() {
  console.log('=== Step 7: データ確認 ===');

  const entities = [
    { name: '得意先', entity: 'businesspartner', where: [{ field: 'flag_customer', op: 'eq', value: true }] },
    { name: '品目', entity: 'material', where: [] },
    { name: '受注', entity: 'sales_order', where: [] },
    { name: '納品書', entity: 'delivery_note', where: [] },
    { name: '請求書', entity: 'sales_invoice', where: [] },
  ];

  for (const e of entities) {
    const res = await req('POST', `/objects/${e.entity}/search`, { page: 1, pageSize: 50, where: e.where, orderBy: [] });
    if (res.status < 300) {
      const count = res.data?.total || res.data?.data?.length || 0;
      console.log(`  ${e.name}: ${count}件`);
      if (e.entity === 'sales_order' && res.data?.data) {
        for (const so of res.data.data) {
          const p = so.payload || so;
          console.log(`    - ${so.so_no || p.soNo || '(番号なし)'} | ${p.partnerName || so.partner_code} | ¥${(so.amount_total || p.amountTotal || 0).toLocaleString()} | ${p.status || '-'}`);
        }
      }
    } else {
      console.log(`  ${e.name}: 取得失敗 (${res.status})`);
    }
  }
  console.log('');
}

// ========== Main ==========
async function main() {
  try {
    await login();
    await deleteExistingData();
    await ensureBusinessPartners();
    await ensureMaterials();
    await createSalesOrders();
    await generateTestPdf();
    await verifyData();
    console.log('=== 全テスト完了 ===');
    console.log('');
    console.log('次のステップ:');
    console.log('  1. ブラウザで受注一覧を確認 (3件の受注が表示される)');
    console.log('  2. 「受注入力」メニューを開き、test_purchase_order.pdf をアップロード');
    console.log('  3. LLMが注文書を解析し、自動的にフォームに入力されることを確認');
  } catch (err) {
    console.error('エラー:', err.message || err);
    process.exit(1);
  }
}

main();

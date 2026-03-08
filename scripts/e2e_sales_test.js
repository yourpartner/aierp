const https = require('https');
const fs = require('fs');
const path = require('path');
const FormData = require('form-data');

const API_HOST = 'yanxia-api.azurewebsites.net';
let TOKEN = '';

function req(method, apiPath, body, headers) {
  return new Promise((resolve, reject) => {
    const isBuffer = Buffer.isBuffer(body);
    const isStream = body && typeof body.pipe === 'function';
    const data = (!isBuffer && !isStream && body) ? JSON.stringify(body) : (isBuffer ? body : null);
    const opts = {
      hostname: API_HOST, path: apiPath, method,
      headers: {
        ...(isBuffer ? {} : isStream ? {} : { 'Content-Type': 'application/json' }),
        'x-company-code': 'JP01',
        ...(TOKEN ? { 'Authorization': `Bearer ${TOKEN}` } : {}),
        ...(data && !isStream ? { 'Content-Length': Buffer.byteLength(data) } : {}),
        ...(headers || {})
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
    if (isStream) { body.pipe(r); }
    else { if (data) r.write(data); r.end(); }
  });
}

function reqMultipart(apiPath, filePath, fieldName = 'file') {
  return new Promise((resolve, reject) => {
    const form = new FormData();
    form.append(fieldName, fs.createReadStream(filePath));
    const opts = {
      hostname: API_HOST,
      path: apiPath,
      method: 'POST',
      headers: {
        ...form.getHeaders(),
        'x-company-code': 'JP01',
        'Authorization': `Bearer ${TOKEN}`,
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
    form.pipe(r);
  });
}

function sep(title) { console.log(`\n${'='.repeat(60)}\n  ${title}\n${'='.repeat(60)}`); }

async function login() {
  sep('Step 1: ログイン');
  const res = await req('POST', '/auth/login', {
    companyCode: 'JP01', employeeCode: 'admin', password: 'test1234'
  });
  if (res.status >= 300) {
    console.log('  ✗ ログイン失敗:', res.status, JSON.stringify(res.data).substring(0, 200));
    process.exit(1);
  }
  TOKEN = res.data.token;
  console.log(`  ✓ ログイン成功 (name: ${res.data.name})`);
}

async function cleanExistingData() {
  sep('Step 2: 既存データクリーンアップ');
  for (const entity of ['sales_invoice', 'delivery_note', 'sales_order']) {
    const res = await req('POST', `/objects/${entity}/search`, { page: 1, pageSize: 200, where: [], orderBy: [] });
    if (res.status < 300 && res.data?.data?.length > 0) {
      let deleted = 0;
      for (const item of res.data.data) {
        const d = await req('DELETE', `/objects/${entity}/${item.id}`);
        if (d.status < 300) deleted++;
      }
      console.log(`  ✓ ${entity}: ${deleted}/${res.data.data.length}件 削除`);
    } else {
      console.log(`  - ${entity}: 0件`);
    }
  }
  // Also clean delivery_notes and sales_invoices via their own tables
  const dnRes = await req('GET', '/delivery-notes?page=1&pageSize=200');
  if (dnRes.status < 300 && dnRes.data?.data?.length > 0) {
    for (const dn of dnRes.data.data) {
      await req('DELETE', `/delivery-notes/${dn.id}`);
    }
    console.log(`  ✓ delivery_notes (table): ${dnRes.data.data.length}件 削除`);
  }
  const siRes = await req('GET', '/sales-invoices?page=1&pageSize=200');
  if (siRes.status < 300 && siRes.data?.data?.length > 0) {
    for (const si of siRes.data.data) {
      try { await req('POST', `/sales-invoices/${si.id}/cancel`); } catch {}
    }
    console.log(`  ✓ sales_invoices (table): ${siRes.data.data.length}件 キャンセル`);
  }
}

async function testPdfUploadAndParse() {
  sep('Step 3: PDF アップロード & LLM 解析');
  const pdfPath = path.join(__dirname, '..', 'test_purchase_order.pdf');
  if (!fs.existsSync(pdfPath)) {
    console.log(`  ✗ テストPDFが見つかりません: ${pdfPath}`);
    console.log('  → まず scripts/reset_and_test_sales.js を実行してください');
    process.exit(1);
  }
  console.log(`  PDF: ${pdfPath} (${(fs.statSync(pdfPath).size / 1024).toFixed(0)} KB)`);
  console.log('  → LLM解析中... (最大30秒)');

  const res = await reqMultipart('/crm/sales-order/parse-document', pdfPath);
  if (res.status >= 300) {
    console.log(`  ✗ 解析失敗: ${res.status}`);
    console.log(`    ${JSON.stringify(res.data).substring(0, 300)}`);
    return null;
  }

  const parsed = res.data;
  console.log('  ✓ LLM解析成功:');
  console.log(`    得意先: ${parsed.partnerName || '(未検出)'} / ${parsed.partnerCode || '(未検出)'}`);
  console.log(`    注文日: ${parsed.orderDate || '(未検出)'}`);
  console.log(`    希望納期: ${parsed.requestedDeliveryDate || '(未検出)'}`);
  console.log(`    明細数: ${parsed.lines?.length || 0}件`);
  if (parsed.lines) {
    for (const l of parsed.lines) {
      console.log(`      - ${l.materialCode || '?'} ${l.materialName || '?'} x${l.quantity || '?'} @¥${l.unitPrice?.toLocaleString() || '?'}`);
    }
  }
  return parsed;
}

async function createSalesOrderFromParsed(parsedData) {
  sep('Step 4: 受注作成（LLM解析データから）');
  
  const lines = (parsedData.lines || []).map((l, i) => {
    const qty = Number(l.quantity || 0);
    const price = Number(l.unitPrice || 0);
    const amount = qty * price;
    const taxRate = 10;
    const taxAmount = Math.round(amount * taxRate / 100);
    return {
      lineNo: i + 1,
      materialCode: l.materialCode || '',
      materialName: l.materialName || '',
      quantity: qty,
      uom: l.uom || '個',
      unitPrice: price,
      amount,
      taxRate,
      taxAmount,
      note: ''
    };
  });

  const amountTotal = lines.reduce((s, l) => s + l.amount + l.taxAmount, 0);
  const taxAmountTotal = lines.reduce((s, l) => s + l.taxAmount, 0);

  const payload = {
    partnerCode: parsedData.partnerCode || 'C003',
    partnerName: parsedData.partnerName || '名古屋エレクトロニクス株式会社',
    orderDate: parsedData.orderDate || '2026-03-15',
    requestedDeliveryDate: parsedData.requestedDeliveryDate || '2026-04-01',
    currency: 'JPY',
    status: 'new',
    lines,
    amountTotal,
    taxAmountTotal,
    note: parsedData.note || 'テスト受注（PDFから作成）',
    deliveryAddress: '愛知県名古屋市中村区名駅3-3-3'
  };

  const res = await req('POST', '/objects/sales_order', { payload });
  if (res.status >= 300) {
    console.log(`  ✗ 受注作成失敗: ${res.status} ${JSON.stringify(res.data).substring(0, 200)}`);
    return null;
  }

  const orderId = res.data?.id;
  console.log(`  ✓ 受注作成成功: id=${orderId}`);
  console.log(`    得意先: ${payload.partnerName} (${payload.partnerCode})`);
  console.log(`    明細: ${lines.length}件`);
  console.log(`    合計: ¥${amountTotal.toLocaleString()}`);
  return orderId;
}

async function createDeliveryNote(salesOrderId) {
  sep('Step 5: 納品書作成（受注から）');

  // First need a warehouse - check existing or create
  const whRes = await req('POST', '/objects/warehouse/search', { page: 1, pageSize: 10, where: [], orderBy: [] });
  let warehouseCode = 'WH001';
  if (whRes.status < 300 && whRes.data?.data?.length > 0) {
    warehouseCode = whRes.data.data[0].payload?.code || whRes.data.data[0].warehouse_code || 'WH001';
    console.log(`  倉庫: ${warehouseCode} (既存)`);
  } else {
    // Create a warehouse
    const whCreate = await req('POST', '/objects/warehouse', { payload: { code: 'WH001', name: '本社倉庫', status: 'active' } });
    if (whCreate.status < 300) {
      console.log(`  倉庫: WH001 (新規作成)`);
    } else {
      console.log(`  ! 倉庫作成失敗、WH001で続行: ${whCreate.status}`);
    }
  }

  const body = { warehouseCode, deliveryDate: '2026-03-20' };
  const res = await req('POST', `/delivery-notes/from-sales-order/${salesOrderId}`, body);
  
  if (res.status >= 300) {
    console.log(`  ✗ 納品書作成失敗: ${res.status} ${JSON.stringify(res.data).substring(0, 300)}`);
    return null;
  }

  const dnId = res.data?.id || res.data?.deliveryNoteId;
  const dnNo = res.data?.deliveryNo || res.data?.delivery_no;
  console.log(`  ✓ 納品書作成成功: id=${dnId}, No=${dnNo}`);
  if (res.data?.pdfUrl) {
    console.log(`    PDF: ${res.data.pdfUrl.substring(0, 80)}...`);
  }
  return { id: dnId, no: dnNo };
}

async function confirmAndShipDeliveryNote(dnId) {
  sep('Step 6: 納品書確認 & 出荷');

  // Confirm
  const confirmRes = await req('POST', `/delivery-notes/${dnId}/confirm`);
  if (confirmRes.status < 300) {
    console.log(`  ✓ 納品書確認成功`);
  } else {
    console.log(`  ✗ 確認失敗: ${confirmRes.status} ${JSON.stringify(confirmRes.data).substring(0, 200)}`);
    // Try shipping anyway as status might already be correct
  }

  // Ship
  const shipRes = await req('POST', `/delivery-notes/${dnId}/ship`);
  if (shipRes.status < 300) {
    console.log(`  ✓ 出荷成功`);
  } else {
    console.log(`  ! 出荷: ${shipRes.status} ${JSON.stringify(shipRes.data).substring(0, 200)}`);
    // Try delivering directly
  }

  // Deliver
  const deliverRes = await req('POST', `/delivery-notes/${dnId}/deliver`);
  if (deliverRes.status < 300) {
    console.log(`  ✓ 納品完了`);
  } else {
    console.log(`  ! 納品: ${deliverRes.status} ${JSON.stringify(deliverRes.data).substring(0, 200)}`);
  }
}

async function createInvoiceFromDeliveryNote(dnId) {
  sep('Step 7: 請求書作成（単件：納品書から）');

  const res = await req('POST', '/sales-invoices/from-delivery-notes', {
    deliveryNoteIds: [dnId],
    invoiceDate: '2026-03-25'
  });

  if (res.status >= 300) {
    console.log(`  ✗ 請求書作成失敗: ${res.status} ${JSON.stringify(res.data).substring(0, 300)}`);
    return null;
  }

  const invoice = res.data;
  console.log(`  ✓ 請求書作成成功:`);
  console.log(`    番号: ${invoice.invoiceNo}`);
  console.log(`    金額: ¥${invoice.amountTotal?.toLocaleString()}`);
  console.log(`    税額: ¥${invoice.taxAmount?.toLocaleString()}`);
  console.log(`    支払期限: ${invoice.dueDate}`);
  if (invoice.pdfBlobName) {
    console.log(`    PDF Blob: ${invoice.pdfBlobName}`);
  }
  if (invoice.pdfUrl) {
    console.log(`    PDF URL: ${invoice.pdfUrl.substring(0, 80)}...`);
  }
  if (invoice.pdfError) {
    console.log(`    PDF Error: ${invoice.pdfError}`);
  }
  return invoice;
}

async function testBatchInvoiceCreation() {
  sep('Step 8: 一括請求書作成 (Skill テスト)');

  // First clean existing invoices so batch can find uninvoiced delivery notes
  console.log('  8a. 既存請求書をキャンセル...');
  const existingInv = await req('GET', '/sales-invoices?page=1&pageSize=200');
  if (existingInv.status < 300 && existingInv.data?.data?.length > 0) {
    for (const inv of existingInv.data.data) {
      if (inv.status !== 'cancelled') {
        await req('POST', `/sales-invoices/${inv.id}/cancel`);
      }
    }
    console.log(`    ${existingInv.data.data.length}件 キャンセル済み`);
  }

  // Create additional test data: 2 more orders → delivery notes → ship
  console.log('  8b. 追加テストデータ作成（2件の受注＆納品書）...');
  
  const additionalOrders = [
    {
      partnerCode: 'C001', partnerName: '東京テクノロジー株式会社',
      orderDate: '2026-03-01', requestedDeliveryDate: '2026-03-15',
      currency: 'JPY', status: 'new',
      lines: [
        { lineNo: 1, materialCode: 'ELEC-001', materialName: 'ノートパソコン 15.6型', quantity: 3, uom: '台', unitPrice: 128000, amount: 384000, taxRate: 10, taxAmount: 38400, note: '' },
        { lineNo: 2, materialCode: 'ELEC-002', materialName: 'ワイヤレスマウス', quantity: 3, uom: '個', unitPrice: 3980, amount: 11940, taxRate: 10, taxAmount: 1194, note: '' }
      ],
      amountTotal: 435534, taxAmountTotal: 39594,
      note: '追加テスト用受注1'
    },
    {
      partnerCode: 'C002', partnerName: '大阪デジタルソリューションズ合同会社',
      orderDate: '2026-03-05', requestedDeliveryDate: '2026-03-20',
      currency: 'JPY', status: 'new',
      lines: [
        { lineNo: 1, materialCode: 'ELEC-004', materialName: 'USBドッキングステーション', quantity: 5, uom: '個', unitPrice: 18500, amount: 92500, taxRate: 10, taxAmount: 9250, note: '' }
      ],
      amountTotal: 101750, taxAmountTotal: 9250,
      note: '追加テスト用受注2'
    }
  ];

  // Check for warehouse
  const whRes = await req('POST', '/objects/warehouse/search', { page: 1, pageSize: 10, where: [], orderBy: [] });
  let warehouseCode = 'WH001';
  if (whRes.status < 300 && whRes.data?.data?.length > 0) {
    warehouseCode = whRes.data.data[0].payload?.code || whRes.data.data[0].warehouse_code || 'WH001';
  }

  for (const orderPayload of additionalOrders) {
    // Create sales order
    const soRes = await req('POST', '/objects/sales_order', { payload: orderPayload });
    if (soRes.status >= 300) {
      console.log(`    ✗ 受注作成失敗 (${orderPayload.partnerName}): ${soRes.status}`);
      continue;
    }
    const soId = soRes.data?.id;
    console.log(`    ✓ 受注: ${orderPayload.partnerName} → id=${soId?.substring(0, 8)}...`);

    // Create delivery note
    const dnRes = await req('POST', `/delivery-notes/from-sales-order/${soId}`, {
      warehouseCode, deliveryDate: '2026-03-18'
    });
    if (dnRes.status >= 300) {
      console.log(`    ✗ 納品書作成失敗: ${dnRes.status} ${JSON.stringify(dnRes.data).substring(0, 150)}`);
      continue;
    }
    const dnId = dnRes.data?.id || dnRes.data?.deliveryNoteId;
    console.log(`    ✓ 納品書: ${dnRes.data?.deliveryNo || 'N/A'}`);

    // Confirm & ship
    await req('POST', `/delivery-notes/${dnId}/confirm`);
    const shipRes = await req('POST', `/delivery-notes/${dnId}/ship`);
    if (shipRes.status < 300) {
      console.log(`    ✓ 出荷完了`);
    } else {
      console.log(`    ! 出荷: ${shipRes.status}`);
    }
    // Deliver
    await req('POST', `/delivery-notes/${dnId}/deliver`);
  }

  // 8c. Batch preview
  console.log('\n  8c. バッチプレビュー (2026年3月)...');
  const preview = await req('GET', '/sales-invoices/batch-preview?year=2026&month=3');
  if (preview.status < 300) {
    console.log(`    既存請求書: ${preview.data.existingInvoiceCount}件`);
    console.log(`    顧客グループ: ${preview.data.customerGroups?.length || 0}件`);
    if (preview.data.customerGroups) {
      for (const g of preview.data.customerGroups) {
        console.log(`      - ${g.customerName} (${g.customerCode}): 納品書${g.totalDns}件, 未請求${g.uninvoicedDns}件`);
      }
    }
  } else {
    console.log(`    ✗ プレビュー失敗: ${preview.status} ${JSON.stringify(preview.data).substring(0, 200)}`);
  }

  // 8d. Batch create
  console.log('\n  8d. 一括請求書作成実行...');
  const batchRes = await req('POST', '/sales-invoices/batch-create', {
    year: 2026, month: 3, mode: 'missing_only', invoiceDate: '2026-03-31'
  });

  if (batchRes.status >= 300) {
    console.log(`    ✗ 一括作成失敗: ${batchRes.status} ${JSON.stringify(batchRes.data).substring(0, 300)}`);
    return;
  }

  const results = batchRes.data?.results || [];
  console.log(`    作成結果: ${results.length}件`);
  for (const r of results) {
    if (r.success) {
      console.log(`    ✓ ${r.customerName} (${r.customerCode}): ${r.invoiceNo} / 納品書${r.dnCount}件`);
      if (r.voucherNo) console.log(`      仕訳: ${r.voucherNo}`);
      if (r.pdfBlobName) console.log(`      PDF: ${r.pdfBlobName}`);
      if (r.pdfUrl) console.log(`      PDF URL: ${r.pdfUrl.substring(0, 80)}...`);
    } else {
      console.log(`    ✗ ${r.customerName} (${r.customerCode}): 失敗 - ${r.error}`);
    }
  }
}

async function verifyResults() {
  sep('Step 9: 最終確認');

  const entities = [
    { name: '受注', path: '/objects/sales_order/search', body: { page: 1, pageSize: 50, where: [], orderBy: [] }, method: 'POST' },
    { name: '納品書', path: '/delivery-notes?page=1&pageSize=50', method: 'GET' },
    { name: '請求書', path: '/sales-invoices?page=1&pageSize=50', method: 'GET' },
  ];

  for (const e of entities) {
    const res = e.method === 'POST' ? await req('POST', e.path, e.body) : await req('GET', e.path);
    if (res.status < 300) {
      const items = res.data?.data || [];
      const total = res.data?.total || items.length;
      console.log(`  ${e.name}: ${total}件`);
      if (e.name === '請求書') {
        for (const inv of items) {
          const h = inv.payload?.header || inv;
          console.log(`    - ${h.invoiceNo || inv.invoice_no} | ${h.customerName || inv.customer_name} | ¥${(h.amountTotal || inv.amount_total || 0).toLocaleString()} | ${h.status || inv.status}`);
        }
      }
    } else {
      console.log(`  ${e.name}: 取得失敗 (${res.status})`);
    }
  }
}

async function main() {
  try {
    await login();
    await cleanExistingData();

    // Test 1: PDF upload → LLM parse
    const parsedData = await testPdfUploadAndParse();
    if (!parsedData) {
      console.log('\n⚠ PDF解析が失敗したため、手動データで受注を作成します');
    }

    // Test 2: Create sales order
    const orderId = await createSalesOrderFromParsed(parsedData || {
      partnerCode: 'C003', partnerName: '名古屋エレクトロニクス株式会社',
      orderDate: '2026-03-15', requestedDeliveryDate: '2026-04-01',
      lines: [
        { materialCode: 'ELEC-001', materialName: 'ノートパソコン 15.6型', quantity: 8, uom: '台', unitPrice: 128000 },
        { materialCode: 'ELEC-003', materialName: '27インチ 4Kモニター', quantity: 8, uom: '台', unitPrice: 54800 },
        { materialCode: 'ELEC-005', materialName: 'ワイヤレスキーボード', quantity: 8, uom: '個', unitPrice: 8900 },
        { materialCode: 'ELEC-002', materialName: 'ワイヤレスマウス', quantity: 8, uom: '個', unitPrice: 3980 },
        { materialCode: 'ELEC-004', materialName: 'USBドッキングステーション', quantity: 8, uom: '個', unitPrice: 18500 },
      ]
    });
    if (!orderId) { console.log('受注作成失敗、テスト中止'); process.exit(1); }

    // Test 3: Create delivery note
    const dn = await createDeliveryNote(orderId);
    if (!dn?.id) { console.log('納品書作成失敗、テスト中止'); process.exit(1); }

    // Test 4: Confirm & ship
    await confirmAndShipDeliveryNote(dn.id);

    // Test 5: Create single invoice
    const invoice = await createInvoiceFromDeliveryNote(dn.id);

    // Test 6: Batch invoice creation (skill test)
    await testBatchInvoiceCreation();

    // Final verification
    await verifyResults();

    sep('テスト完了');
    console.log('  全ステップが実行されました。');
    console.log('  Azure管理画面でPDFファイルを確認してください。');
  } catch (err) {
    console.error('\nエラー:', err.message || err);
    console.error(err.stack);
    process.exit(1);
  }
}

main();

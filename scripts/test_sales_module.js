const https = require('https');

const API_HOST = 'yanxia-api.azurewebsites.net';
let TOKEN = '';

function req(method, path, body) {
  return new Promise((resolve, reject) => {
    const data = body ? JSON.stringify(body) : '';
    const opts = {
      hostname: API_HOST, path, method,
      headers: {
        'Content-Type': 'application/json',
        'x-company-code': 'JP01',
        ...(TOKEN ? { 'Authorization': `Bearer ${TOKEN}` } : {}),
        ...(data ? { 'Content-Length': Buffer.byteLength(data) } : {})
      }
    };
    const r = https.request(opts, res => {
      let b = ''; res.on('data', c => b += c);
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

async function login() {
  console.log('=== 1. ログイン (JP01 / admin) ===');
  const res = await req('POST', '/auth/login', { companyCode: 'JP01', employeeCode: 'admin', password: 'admin' });
  TOKEN = res.data.token;
  console.log(`  ✓ ログイン成功 (name: ${res.data.name})\n`);
}

async function createMaterials() {
  console.log('=== 2. 品目マスタ作成（電子製品） ===');
  const materials = [
    { code: 'ELEC-001', name: 'ノートパソコン 15.6型', category: '電子機器', unit: '台', unitPrice: 128000, taxRate: 10, weight: 2.1, description: '高性能ビジネス向けノートPC、Core i7/16GB/512GB SSD' },
    { code: 'ELEC-002', name: 'ワイヤレスマウス', category: '周辺機器', unit: '個', unitPrice: 3980, taxRate: 10, weight: 0.08, description: 'Bluetooth5.0対応、静音クリック、USB-C充電' },
    { code: 'ELEC-003', name: '27インチ 4Kモニター', category: '周辺機器', unit: '台', unitPrice: 54800, taxRate: 10, weight: 6.5, description: 'IPS パネル、USB-C PD対応、HDR400' },
    { code: 'ELEC-004', name: 'USBドッキングステーション', category: '周辺機器', unit: '個', unitPrice: 18500, taxRate: 10, weight: 0.35, description: 'USB-C接続、HDMI×2/USB-A×3/GbE/SD' },
    { code: 'ELEC-005', name: 'ワイヤレスキーボード', category: '周辺機器', unit: '個', unitPrice: 8900, taxRate: 10, weight: 0.45, description: 'メカニカル、日本語配列、Bluetooth/2.4GHz両対応' },
  ];
  for (const m of materials) {
    const payload = { code: m.code, name: m.name, category: m.category, baseUnit: m.unit, salesPrice: m.unitPrice, purchasePrice: Math.round(m.unitPrice * 0.6), taxRate: m.taxRate, weight: m.weight, description: m.description, status: 'active' };
    const res = await req('POST', '/inventory/material', { payload });
    if (res.status < 300) {
      console.log(`  ✓ ${m.code} ${m.name} (¥${m.unitPrice.toLocaleString()}) 作成成功`);
    } else {
      console.log(`  ✗ ${m.code} 失敗: ${res.status} ${typeof res.data === 'string' ? res.data.substring(0, 120) : JSON.stringify(res.data).substring(0, 120)}`);
    }
  }
  console.log('');
}

async function testSalesOrders() {
  console.log('=== 3. 受注テスト（登録・編集・削除） ===');

  // 3a. 受注作成 1
  const order1 = {
    soNo: 'SO-2026-0301',
    orderDate: '2026-03-01',
    partnerCode: 'C001',
    partnerName: '東京テクノロジー株式会社',
    requestedDeliveryDate: '2026-03-15',
    paymentTerms: { code: 'NET30', description: '30日以内' },
    currency: 'JPY',
    status: 'new',
    lines: [
      { lineNo: 1, materialCode: 'ELEC-001', materialName: 'ノートパソコン 15.6型', quantity: 10, uom: '台', unitPrice: 128000, amount: 1280000, taxRate: 10, taxAmount: 128000 },
      { lineNo: 2, materialCode: 'ELEC-003', materialName: '27インチ 4Kモニター', quantity: 10, uom: '台', unitPrice: 54800, amount: 548000, taxRate: 10, taxAmount: 54800 },
      { lineNo: 3, materialCode: 'ELEC-002', materialName: 'ワイヤレスマウス', quantity: 10, uom: '個', unitPrice: 3980, amount: 39800, taxRate: 10, taxAmount: 3980 },
    ],
    taxAmountTotal: 186780,
    amountTotal: 2054580,
    note: 'オフィス新設に伴うPC・周辺機器一括購入',
    shipTo: { address: '東京都千代田区大手町1-1-1 大手町ビル5F' },
  };
  let res1 = await req('POST', '/objects/sales_order', { payload: order1 });
  let order1Id = null;
  if (res1.status < 300) {
    order1Id = res1.data?.id;
    console.log(`  ✓ 受注1 作成成功: ${order1.soNo} (id: ${order1Id}) - 合計 ¥${order1.amountTotal.toLocaleString()}`);
  } else {
    console.log(`  ✗ 受注1 作成失敗: ${res1.status} ${JSON.stringify(res1.data).substring(0, 150)}`);
  }

  // 3b. 受注作成 2
  const order2 = {
    soNo: 'SO-2026-0302',
    orderDate: '2026-03-01',
    partnerCode: 'C002',
    partnerName: '大阪デジタルソリューションズ合同会社',
    requestedDeliveryDate: '2026-03-20',
    paymentTerms: { code: 'NET60', description: '60日以内' },
    currency: 'JPY',
    status: 'new',
    lines: [
      { lineNo: 1, materialCode: 'ELEC-004', materialName: 'USBドッキングステーション', quantity: 20, uom: '個', unitPrice: 18500, amount: 370000, taxRate: 10, taxAmount: 37000 },
      { lineNo: 2, materialCode: 'ELEC-005', materialName: 'ワイヤレスキーボード', quantity: 20, uom: '個', unitPrice: 8900, amount: 178000, taxRate: 10, taxAmount: 17800 },
    ],
    taxAmountTotal: 54800,
    amountTotal: 602800,
    note: '社員増員に伴う周辺機器追加発注',
  };
  let res2 = await req('POST', '/objects/sales_order', { payload: order2 });
  let order2Id = null;
  if (res2.status < 300) {
    order2Id = res2.data?.id;
    console.log(`  ✓ 受注2 作成成功: ${order2.soNo} (id: ${order2Id}) - 合計 ¥${order2.amountTotal.toLocaleString()}`);
  } else {
    console.log(`  ✗ 受注2 作成失敗: ${res2.status} ${JSON.stringify(res2.data).substring(0, 150)}`);
  }

  // 3c. 受注編集 (order2 の備考を更新)
  if (order2Id) {
    const updatedOrder2 = { ...order2, note: '社員増員に伴う周辺機器追加発注（承認済み）' };
    const resEdit = await req('PUT', `/objects/sales_order/${order2Id}`, { payload: updatedOrder2 });
    if (resEdit.status < 300) {
      console.log(`  ✓ 受注2 編集成功: 備考更新`);
    } else {
      console.log(`  ✗ 受注2 編集失敗: ${resEdit.status} ${JSON.stringify(resEdit.data).substring(0, 150)}`);
    }
  }

  // 3d. 受注作成 3 (削除用)
  const order3 = {
    soNo: 'SO-2026-0303',
    orderDate: '2026-03-01',
    partnerCode: 'C003',
    partnerName: 'テスト商事（削除テスト用）',
    requestedDeliveryDate: '2026-03-25',
    currency: 'JPY',
    status: 'new',
    lines: [{ lineNo: 1, materialCode: 'ELEC-002', materialName: 'ワイヤレスマウス', quantity: 5, uom: '個', unitPrice: 3980, amount: 19900, taxRate: 10, taxAmount: 1990 }],
    taxAmountTotal: 1990,
    amountTotal: 21890,
  };
  let res3 = await req('POST', '/objects/sales_order', { payload: order3 });
  let order3Id = null;
  if (res3.status < 300) {
    order3Id = res3.data?.id;
    console.log(`  ✓ 受注3 作成成功: ${order3.soNo} (id: ${order3Id}) - 削除テスト用`);
  } else {
    console.log(`  ✗ 受注3 作成失敗: ${res3.status} ${JSON.stringify(res3.data).substring(0, 150)}`);
  }

  // 3e. 受注削除
  if (order3Id) {
    const resDel = await req('DELETE', `/objects/sales_order/${order3Id}`);
    if (resDel.status < 300) {
      console.log(`  ✓ 受注3 削除成功`);
    } else {
      console.log(`  ✗ 受注3 削除失敗: ${resDel.status} ${JSON.stringify(resDel.data).substring(0, 150)}`);
    }
  }

  // 3f. 受注一覧取得
  const resList = await req('POST', '/objects/sales_order/search', { limit: 10, offset: 0 });
  if (resList.status < 300 && resList.data.data) {
    console.log(`  ✓ 受注一覧: ${resList.data.total}件`);
  } else {
    console.log(`  ✗ 受注一覧取得失敗: ${resList.status} ${JSON.stringify(resList.data).substring(0,150)}`);
  }
  console.log('');
  return { order1Id, order2Id };
}

async function testDeliveryNotes(orderIds) {
  console.log('=== 4. 出荷伝票テスト ===');
  const delivery = {
    deliveryNo: 'DN-2026-0301',
    deliveryDate: '2026-03-15',
    salesOrderNo: 'SO-2026-0301',
    customerCode: 'C001',
    customerName: '東京テクノロジー株式会社',
    printStatus: 'pending',
    items: [
      { lineNo: 1, materialCode: 'ELEC-001', materialName: 'ノートパソコン 15.6型', qty: 10, uom: '台' },
      { lineNo: 2, materialCode: 'ELEC-003', materialName: '27インチ 4Kモニター', qty: 10, uom: '台' },
      { lineNo: 3, materialCode: 'ELEC-002', materialName: 'ワイヤレスマウス', qty: 10, uom: '個' },
    ],
  };
  const res = await req('POST', '/objects/delivery_note', { payload: delivery });
  if (res.status < 300) {
    console.log(`  ✓ 出荷伝票 作成成功: ${delivery.deliveryNo} (id: ${res.data?.id})`);
  } else {
    console.log(`  ✗ 出荷伝票 作成失敗: ${res.status} ${JSON.stringify(res.data).substring(0, 150)}`);
  }

  const resList = await req('POST', '/objects/delivery_note/search', { limit: 10, offset: 0 });
  if (resList.status < 300 && resList.data.data) {
    console.log(`  ✓ 出荷伝票一覧: ${resList.data.total}件`);
  } else {
    console.log(`  ✗ 出荷伝票一覧取得失敗: ${resList.status} ${JSON.stringify(resList.data).substring(0,150)}`);
  }
  console.log('');
}

async function testSalesInvoices(orderIds) {
  console.log('=== 5. 請求書テスト ===');
  const invoice = {
    header: {
      invoiceNo: 'INV-2026-0301',
      invoiceDate: '2026-03-15',
      dueDate: '2026-04-15',
      customerCode: 'C001',
      customerName: '東京テクノロジー株式会社',
      status: 'issued',
      currency: 'JPY',
      note: '2026年3月分 オフィス新設関連一括請求',
    },
    lines: [
      { lineNo: 1, materialCode: 'ELEC-001', materialName: 'ノートパソコン 15.6型', quantity: 10, uom: '台', unitPrice: 128000, amount: 1280000, taxRate: 10, taxAmount: 128000, soNo: 'SO-2026-0301' },
      { lineNo: 2, materialCode: 'ELEC-003', materialName: '27インチ 4Kモニター', quantity: 10, uom: '台', unitPrice: 54800, amount: 548000, taxRate: 10, taxAmount: 54800, soNo: 'SO-2026-0301' },
      { lineNo: 3, materialCode: 'ELEC-002', materialName: 'ワイヤレスマウス', quantity: 10, uom: '個', unitPrice: 3980, amount: 39800, taxRate: 10, taxAmount: 3980, soNo: 'SO-2026-0301' },
    ],
  };
  const res = await req('POST', '/objects/sales_invoice', { payload: invoice });
  if (res.status < 300) {
    console.log(`  ✓ 請求書 作成成功: ${invoice.header.invoiceNo} (id: ${res.data?.id})`);
  } else {
    console.log(`  ✗ 請求書 作成失敗: ${res.status} ${JSON.stringify(res.data).substring(0, 150)}`);
  }

  const resList = await req('POST', '/objects/sales_invoice/search', { limit: 10, offset: 0 });
  if (resList.status < 300 && resList.data.data) {
    console.log(`  ✓ 請求書一覧: ${resList.data.total}件`);
  } else {
    console.log(`  ✗ 請求書一覧取得失敗: ${resList.status} ${JSON.stringify(resList.data).substring(0,150)}`);
  }
  console.log('');
}

async function testSalesAnalytics() {
  console.log('=== 6. 販売分析レポート ===');
  const endpoints = [
    { path: '/analytics/sales/overview?startDate=2026-01-01&endDate=2026-03-31', label: 'サマリ' },
    { path: '/analytics/sales/by-customer?limit=10', label: '顧客別' },
    { path: '/analytics/sales/by-product?limit=10', label: '商品別' },
    { path: '/analytics/sales/trend?startDate=2026-01-01&endDate=2026-03-31&interval=month', label: 'トレンド' },
  ];
  for (const ep of endpoints) {
    const res = await req('GET', ep.path);
    if (res.status < 300) {
      const d = res.data;
      const summary = typeof d === 'object' ? JSON.stringify(d).substring(0, 200) : String(d).substring(0, 200);
      console.log(`  ✓ ${ep.label}: ${summary}`);
    } else {
      console.log(`  ✗ ${ep.label}: ${res.status} ${JSON.stringify(res.data).substring(0, 150)}`);
    }
  }
  console.log('');
}

async function main() {
  try {
    await login();
    await createMaterials();
    const orderIds = await testSalesOrders();
    await testDeliveryNotes(orderIds);
    await testSalesInvoices(orderIds);
    await testSalesAnalytics();
    console.log('=== テスト完了 ===');
  } catch (err) {
    console.error('テスト中にエラー:', err.message || err);
  }
}

main();

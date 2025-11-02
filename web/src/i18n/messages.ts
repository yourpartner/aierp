export type Lang = 'ja' | 'en'

export interface Messages {
  appTitle: string
  nav: {
    chat: string
    newSession: string
    common: string
    vouchers: string
    voucherNew: string
    accounts: string
    accountNew: string
    bankReceipt: string
    bankPayment: string
    bankPlanner: string
    schemaEditor: string
    approvalsDesigner: string
    notifRuleRuns: string
    notifLogs: string
    schedulerTasks: string
    partners: string
    partnerNew: string
    hrDept: string
    hrEmps: string
    hrEmpNew: string
    employmentTypes: string
    policyEditor: string
    payrollExecute: string
    timesheets: string
    timesheetNew: string
    certRequest: string
    certList: string
    approvalsInbox: string
    companySettings: string
    accountingPeriods: string
    inventory: string
    inventoryMaterials: string
    inventoryMaterialNew: string
    inventoryWarehouses: string
    inventoryWarehouseNew: string
    inventoryBins: string
    inventoryBinNew: string
    inventoryStatuses: string
    inventoryBatches: string
    inventoryBatchNew: string
    inventoryMovement: string
    inventoryBalances: string
    crm: string
    crmContacts: string
    crmDeals: string
    crmQuotes: string
    crmSalesOrders: string
    crmActivities: string
    crmContactNew: string
    crmDealNew: string
    crmQuoteNew: string
    crmSalesOrderNew: string
    crmActivityNew: string
    recent: string
  }
  chat: {
    aiTitle: string
    empty: string
    placeholder: string
    send: string
  }
  dialogs: {
    payroll: string
    todo: string
    todoEmpty: string
    proofRequest: {
      title: string
      type: string
      customType: string
      useCustom: string
      purpose: string
      language: string
      email: string
      submit: string
      success: string
      auto: string
      reason: string
      resignReason: string
    }
  }
  tables: {
    voucherList: {
      title: string
      date: string
      type: string
      number: string
      summary: string
      actions: string
      view: string
      createdAt: string
      createdBy: string
      updatedAt: string
      updatedBy: string
    }
    accounts: {
      title: string
      new: string
      code: string
      name: string
      bspl: string
      tax: string
      openItem: string
      bankCash: string
      detail: string
      open: string
      close: string
      bank: string
      cash: string
      none: string
      listEmpty: string
      save: string
      saved: string
      failed: string
      bankDialog: string
      branchDialog: string
    }
    voucherDetail: {
      title: string
      date: string
      type: string
      number: string
      summary: string
      customer: string
      vendor: string
      department: string
      employee: string
      createdAt: string
      createdBy: string
      updatedAt: string
      updatedBy: string
    }
    inventoryBalances: {
      material: string
      warehouse: string
      bin: string
      status: string
      batch: string
      quantity: string
    }
    partners: {
      title: string
      new: string
      code: string
      name: string
      customerVendor: string
      customerTag: string
      vendorTag: string
      status: string
    }
    contacts: {
      title: string
      new: string
      code: string
      name: string
      partner: string
      email: string
      status: string
    }
    deals: {
      title: string
      new: string
      code: string
      partner: string
      stage: string
      amount: string
      closeDate: string
      source: string
    }
    quotes: {
      title: string
      new: string
      number: string
      partner: string
      amount: string
      validUntil: string
      status: string
    }
    salesOrders: {
      title: string
      new: string
      number: string
      customer: string
      amount: string
      status: string
      issueDate: string
    }
    activities: {
      title: string
      new: string
      subject: string
      type: string
      dueDate: string
      owner: string
      status: string
    }
    accountingPeriods: {
      title: string
      new: string
      newTitle: string
      editTitle: string
      periodStart: string
      periodEnd: string
      status: string
      open: string
      closed: string
      memo: string
      actions: string
      edit: string
      delete: string
      save: string
      cancel: string
      createSuccess: string
      updateSuccess: string
      deleteSuccess: string
      saveFailed: string
      loadFailed: string
      deleteConfirm: string
      deleteTitle: string
    }
  }
  columns: {
    drcr: string
    amount: string
  }
  dynamicForm: {
    addRow: string
    removeRow: string
    select: string
    upload: string
    actions: string
    button: string
    value: string
  }
  schemaLabels: Record<string, string>
  schemaList: {
    create: string
    refresh: string
    createTitle: string
    loadFailed: string
    layoutMissing: string
  }
  voucherForm: {
    title: string
    actions: {
      save: string
      reset: string
      addLine: string
      deleteLine: string
      verifyInvoice: string
    }
    header: {
      companyCode: string
      postingDate: string
      voucherType: string
      currency: string
      summary: string
      invoiceRegistrationNo: string
    }
    lines: {
      account: string
      drcr: string
      amount: string
      taxRate: string
      taxAmount: string
      netAmount: string
      department: string
      employee: string
      customer: string
      vendor: string
      paymentDate: string
      note: string
      actions: string
    }
    totals: {
      prefix: string
      imbalance: string
    }
    placeholders: {
      account: string
      customer: string
      vendor: string
      department: string
      employee: string
    }
    messages: {
      saved: string
      error: string
      posted: string
      missingInputTaxAccount: string
      missingOutputTaxAccount: string
      periodClosed: string
      invoiceInvalid: string
      invoiceNotFound: string
      invoiceInactive: string
      invoiceExpired: string
      invoiceMatched: string
      invoiceCheckFailed: string
      invoiceRequired: string
      invoiceUnchecked: string
    }
    typeOptions: Record<string, string>
    drLabel: string
    crLabel: string
  }
  buttons: {
    search: string
    reset: string
    close: string
    refresh: string
  }
  common: {
    enabled: string
    disabled: string
    view: string
    save: string
    saved: string
    saveFailed: string
    close: string
    loadFailed: string
    backList: string
  }
  schemaEditor: {
    entity: string
    saveNew: string
    saving: string
    saved: string
  }
}

export const ja: Messages = {
  appTitle: 'ERP ハブ',
  nav: {
    chat: 'チャット会話',
    newSession: '新規会話',
    common: '共通メニュー',
    vouchers: '会計伝票一覧',
    voucherNew: '新規伝票',
    accounts: '勘定科目一覧',
    accountNew: '科目登録',
    bankReceipt: '銀行入金',
    bankPayment: '銀行出金',
    bankPlanner: '入金配分（テスト）',
    schemaEditor: 'スキーマ管理',
    approvalsDesigner: '承認ルール',
    notifRuleRuns: '通知ルール実行履歴',
    notifLogs: '通知送信ログ',
    schedulerTasks: 'タスクスケジューラ',
    partners: '取引先一覧',
    partnerNew: '取引先登録',
    hrDept: '部門階層',
    hrEmps: '社員一覧',
    hrEmpNew: '社員登録',
    employmentTypes: '雇用区分',
    policyEditor: '給与ポリシー',
    payrollExecute: '給与計算',
    timesheets: '工数一覧',
    timesheetNew: '工数入力',
    certRequest: '証明書申請',
    certList: '申請履歴',
    approvalsInbox: '承認待ち',
    companySettings: '会社設定',
    accountingPeriods: '会計期間',
    inventory: '在庫管理',
    inventoryMaterials: '品目一覧',
    inventoryMaterialNew: '品目登録',
    inventoryWarehouses: '倉庫一覧',
    inventoryWarehouseNew: '倉庫登録',
    inventoryBins: '棚番一覧',
    inventoryBinNew: '棚番登録',
    inventoryStatuses: '在庫ステータス',
    inventoryBatches: 'ロット一覧',
    inventoryBatchNew: 'ロット登録',
    inventoryMovement: '入出庫・振替',
    inventoryBalances: '在庫残高',
    crm: 'CRM',
    crmContacts: 'コンタクト一覧',
    crmDeals: '商談一覧',
    crmQuotes: '見積一覧',
    crmSalesOrders: '受注一覧',
    crmActivities: '活動一覧',
    crmContactNew: 'コンタクト登録',
    crmDealNew: '商談登録',
    crmQuoteNew: '見積登録',
    crmSalesOrderNew: '受注登録',
    crmActivityNew: '活動登録',
    recent: '最近のページ'
  },
  chat: {
    aiTitle: 'AI チャット',
    empty: '左のメニューからページを開くか、最初のメッセージを送信してください',
    placeholder: 'AI と会話する...',
    send: '送信'
  },
  dialogs: {
    payroll: '給与計算',
    todo: 'タスク',
    todoEmpty: '（準備中）',
    proofRequest: {
      title: '証明書申請',
      type: '証明書種別',
      customType: 'カスタム種別',
      useCustom: '自動入力',
      purpose: '用途・備考',
      language: '言語',
      email: '送信先メールアドレス',
      submit: '申請を送信',
      success: '申請を受け付けました',
      auto: '自動設定',
      reason: '用途・備考',
      resignReason: '退職理由'
    }
  },
  tables: {
    voucherList: {
      title: '会計伝票一覧',
      date: '伝票日付',
      type: '伝票種別',
      number: '伝票番号',
      summary: '摘要',
      actions: '操作',
      view: '表示',
      createdAt: '作成日時',
      createdBy: '作成者',
      updatedAt: '更新日時',
      updatedBy: '更新者'
    },
    accounts: {
      title: '勘定科目一覧',
      new: '科目登録',
      code: '科目コード',
      name: '科目名',
      bspl: 'BS/PL区分',
      tax: '消費税区分',
      openItem: '未収入金管理',
      bankCash: '銀行/現金',
      detail: '詳細',
      open: '有効',
      close: '無効',
      bank: '銀行',
      cash: '現金',
      none: 'なし',
      listEmpty: 'データがありません',
      save: '保存',
      saved: '保存しました',
      failed: '保存に失敗しました',
      bankDialog: '銀行を選択',
    branchDialog: '支店を選択',
    openItemBaseline: '消込基準',
    fieldRuleSection: '入力制御',
    bankCashSection: '銀行 / 現金',
    customerRule: '顧客入力制御',
    vendorRule: '仕入先入力制御',
    employeeRule: '従業員入力制御',
    departmentRule: '部門入力制御',
    paymentDateRule: '支払日入力制御',
    fieldRuleRequired: '必須',
    fieldRuleOptional: '任意',
    fieldRuleHidden: '非表示',
    baselineNone: '基準なし',
    baselineCustomer: '顧客',
    baselineVendor: '仕入先',
    baselineEmployee: '従業員',
    taxOptionNonTax: '非課税',
    taxOptionInput: '仕入税額',
    taxOptionOutput: '売上税額',
    taxOptionAccount: '消費税勘定',
    bankAccountFlag: '銀行科目',
    cashAccountFlag: '現金科目',
    selectBankButton: '銀行を選択',
    selectBranchButton: '支店を選択',
    bankName: '銀行',
    branchName: '支店',
    accountType: '口座種別',
    accountTypeOrdinary: '普通',
    accountTypeChecking: '当座',
    accountNo: '口座番号',
    holder: '名義人',
    currency: '通貨',
    currencyJpy: 'JPY',
    currencyUsd: 'USD',
    currencyCny: 'CNY',
    cashCurrency: '現金通貨',
      taxMap: {
        nonTax: '非課税',
        input: '仕入税額',
        output: '売上税額',
        account: '消費税勘定'
      },
      categoryMap: {
        bs: '貸借対照表科目',
        pl: '損益計算書科目'
      }
    },
    voucherDetail: {
      title: '伝票詳細',
      date: '伝票日付',
      type: '伝票種別',
      number: '伝票番号',
      summary: '摘要',
      customer: '顧客',
      vendor: '仕入先',
      department: '部門',
      employee: '従業員',
      createdAt: '作成日時',
      createdBy: '作成者',
      updatedAt: '更新日時',
      updatedBy: '更新者'
    },
    inventoryBalances: {
      material: '品目',
      warehouse: '倉庫',
      bin: '棚番',
      status: '在庫ステータス',
      batch: 'ロット',
      quantity: '数量'
    },
    partners: {
      title: '取引先一覧',
      new: '取引先登録',
      code: '取引先コード',
      name: '名称',
      customerVendor: '顧客/仕入先',
      customerTag: '顧客',
      vendorTag: '仕入先',
      status: 'ステータス'
    },
    contacts: {
      title: 'コンタクト一覧',
      new: 'コンタクト登録',
      code: 'コンタクトコード',
      name: '氏名',
      partner: '取引先コード',
      email: 'メール',
      status: 'ステータス'
    },
    deals: {
      title: '商談一覧',
      new: '商談登録',
      code: '商談コード',
      partner: '取引先コード',
      stage: 'ステージ',
      amount: '見込金額',
      closeDate: '予定締結日',
      source: '案件経路'
    },
    quotes: {
      title: '見積一覧',
      new: '見積登録',
      number: '見積番号',
      partner: '取引先コード',
      amount: '金額',
      validUntil: '有効期限',
      status: 'ステータス'
    },
    salesOrders: {
      title: '受注一覧',
      new: '受注登録',
      number: '受注番号',
      customer: '顧客コード',
      amount: '金額',
      status: 'ステータス',
      issueDate: '発行日'
    },
    activities: {
      title: '活動一覧',
      new: '活動登録',
      subject: '件名',
      type: '種類',
      dueDate: '期限',
      owner: '担当者',
      status: 'ステータス'
    },
    accountingPeriods: {
      title: '会計期間',
      new: '期間追加',
      newTitle: '会計期間の追加',
      editTitle: '会計期間の編集',
      periodStart: '開始日',
      periodEnd: '終了日',
      status: 'ステータス',
      open: '開',
      closed: '閉',
      memo: '備考',
      actions: '操作',
      edit: '編集',
      delete: '削除',
      save: '保存',
      cancel: 'キャンセル',
      createSuccess: '登録しました',
      updateSuccess: '更新しました',
      deleteSuccess: '削除しました',
      saveFailed: '保存に失敗しました',
      loadFailed: '会計期間の取得に失敗しました',
      deleteConfirm: 'この会計期間を削除しますか？',
      deleteTitle: '確認'
    }
  },
  columns: {
    drcr: '借方/貸方',
    amount: '金額'
  },
  dynamicForm: {
    addRow: '行を追加',
    removeRow: '削除',
    select: '選択',
    upload: '添付をアップロード',
    actions: '操作',
    button: '操作',
    value: '値'
  },
  schemaLabels: {
    code: 'コード',
    name: '名称',
    description: '説明',
    spec: '仕様',
    baseuom: '基準単位',
    batchmanagement: 'ロット管理',
    category: 'カテゴリ',
    category1: 'カテゴリ1',
    category2: 'カテゴリ2',
    subcategory: 'サブカテゴリ',
    origin: '原産地',
    originCountry: '原産国',
    countryOfOrigin: '原産国',
    country: '国',
    countryCode: '国コード',
    type: '種別',
    status: 'ステータス',
    warehousecode: '倉庫コード',
    warehousename: '倉庫名',
    warehouse: '倉庫',
    address: '住所',
    phone: '電話番号',
    contact: '連絡先',
    bincode: '棚番コード',
    binname: '棚番名称',
    bin: '棚番',
    level: '階層',
    remark: '備考',
    remarks: '備考',
    note: '備考',
    memo: 'メモ',
    material: '品目',
    materialcode: '品目コード',
    materialname: '品目名称',
    batch: 'ロット',
    batchno: 'ロット番号',
    quantity: '数量',
    uom: '単位',
    unit: '単位',
    statuscode: 'ステータスコード',
    statusname: 'ステータス名称',
    allownegative: 'マイナス在庫許可',
    movementtype: '移動区分',
    movementdate: '移動日',
    fromwarehouse: '出庫倉庫',
    frombin: '出庫棚番',
    towarehouse: '入庫倉庫',
    tobin: '入庫棚番',
    lines: '明細',
    line: '明細',
    attachments: '添付ファイル',
    createdat: '作成日時',
    updatedat: '更新日時',
    weight: '重量',
    length: '長さ',
    width: '幅',
    height: '高さ',
    cost: '原価',
    price: '価格',
    unitprice: '単価',
    leadtime: 'リードタイム',
    owner: '担当者',
    manager: '管理者',
    statuslabel: 'ステータス',
    effectivefrom: '開始日',
    effectiveto: '終了日',
    typename: '種別名',
    categoryname: 'カテゴリ名',
    category: 'カテゴリ',
    categoryLarge: 'カテゴリ(L)',
    categorySmall: 'カテゴリ(S)',
    brand: 'ブランド',
    model: '型番',
    color: 'カラー',
    material: '材質',
    originCountry: '原産国',
    janCode: 'JANコード',
    eanCode: 'EANコード',
    description: '説明',
    '__label__general': '基本情報',
    '__label__inventory': '在庫情報',
    '__label__logistics': '物流情報',
    '__label__dimensions': '寸法',
    '__label__pricing': '価格',
    '__label__additional': '補足情報',
    generalinfo: '基本情報',
    inventoryinfo: '在庫情報',
    logisticsinfo: '物流情報',
    dimension: '寸法',
    dimensions: '寸法',
    pricing: '価格',
    additional: '補足情報',
    'tab:lines': '明細',
    'tab:attachments': '添付ファイル',
    'section:general': '基本情報',
    'section:inventory': '在庫情報',
    'section:logistics': '物流情報',
    'section:dimensions': '寸法',
    'section:pricing': '価格',
    'section:additional': '補足情報',
    defaultwarehouse: '既定の倉庫',
    defaultbin: '既定の棚番',
    defaultstatus: '既定ステータス',
    defaultbatch: '既定ロット',
    fromstatus: '出庫ステータス',
    tostatus: '入庫ステータス',
    reference: '参照',
    movementid: '移動番号',
    lineno: '明細番号',
    reason: '理由',
    docstatus: '伝票ステータス',
    'option:product': '製品',
    'option:semiproduct': '半製品',
    'option:rawmaterial': '原材料',
    'option:consumable': '消耗品',
    'option:active': '有効',
    'option:inactive': '無効',
    'button:选择银行': '銀行を選択',
    'button:选择支店': '支店を選択',
    schedulerTask: 'スケジュールタスク'
  },
  schemaList: {
    create: '新規作成',
    refresh: '再読み込み',
    createTitle: '新規作成',
    loadFailed: 'スキーマの読み込みに失敗しました',
    layoutMissing: 'フォームレイアウトが設定されていません'
  },
  voucherForm: {
    title: '会計伝票',
    actions: {
      save: '保存',
      reset: 'リセット',
      addLine: '行を追加',
      deleteLine: '削除',
      verifyInvoice: '照会'
    },
    header: {
      companyCode: '会社コード',
      postingDate: '記帳日',
      voucherType: '伝票種別',
      currency: '通貨',
      summary: '摘要',
      invoiceRegistrationNo: 'インボイス登録番号'
    },
    lines: {
      account: '勘定科目',
      drcr: '借方/貸方',
      amount: '税込金額',
      taxRate: '消費税率',
      taxAmount: '消費税額',
      netAmount: '税抜金額',
      department: '部門',
      employee: '従業員',
      customer: '顧客',
      vendor: '仕入先',
      paymentDate: '支払日',
      note: '備考',
      actions: '操作'
    },
    totals: {
      prefix: '借方合計：{debit} / 貸方合計：{credit}',
      imbalance: '（不均衡）'
    },
    placeholders: {
      account: '科目名またはコードで検索',
      customer: '顧客名で検索',
      vendor: '仕入先名で検索',
      department: '部門名で検索',
      employee: '社員名で検索'
    },
    messages: {
      saved: '保存しました：{no}',
      error: '保存に失敗しました',
      posted: '{date} {type} 伝票を作成しました（番号 {no}）',
      missingInputTaxAccount: '入力消費税の仕訳先科目が設定されていません。会社設定で「仮払消費税」などの科目を指定してください。',
      missingOutputTaxAccount: '出力消費税の仕訳先科目が設定されていません。会社設定で「仮受消費税」などの科目を指定してください。',
      periodClosed: '会計期間が閉じています。摘要などのテキストのみ変更できます。',
      invoiceInvalid: 'インボイス登録番号は「T」+13桁の数字で入力してください。',
      invoiceNotFound: '登録番号 {no} は国税庁の公表データに存在しません。',
      invoiceInactive: '登録番号 {no} は {date} から有効です。',
      invoiceExpired: '登録番号 {no} は {date} に失効しています。',
      invoiceMatched: '登録番号 {no} は {name} として登録されています。',
      invoiceCheckFailed: 'インボイス登録番号の照会に失敗しました。',
      invoiceRequired: 'インボイス登録番号を入力してください。',
      invoiceUnchecked: 'インボイス登録番号を照会してください。'
    },
    typeOptions: {
      GL: '総勘定元帳',
      AP: '買掛金',
      AR: '売掛金',
      AA: '資産',
      SA: '売上',
      IN: '在庫',
      OT: 'その他'
    },
    drLabel: '借方',
    crLabel: '貸方'
  },
  buttons: {
    search: '検索',
    reset: 'リセット',
    close: '閉じる',
    refresh: '再読み込み'
  },
  common: {
    enabled: '有効',
    disabled: '無効',
    view: '表示',
    save: '保存',
    saved: '保存しました',
    saveFailed: '保存に失敗しました',
    close: '閉じる',
    loadFailed: '読み込みに失敗しました',
    backList: '一覧に戻る'
  },
  schemaEditor: {
    entity: '対象エンティティ',
    saveNew: '新しいバージョンとして保存',
    saving: '保存中...',
    saved: '保存しました'
  }
}

export const en: Messages = {
  appTitle: 'ERP Hub',
  nav: {
    chat: 'Chat Sessions',
    newSession: 'New Session',
    common: 'Shortcut Menu',
    vouchers: 'Vouchers',
    voucherNew: 'New Voucher',
    accounts: 'Chart of Accounts',
    accountNew: 'Create Account',
    bankReceipt: 'Bank Receipt',
    bankPayment: 'Bank Payment',
    bankPlanner: 'Allocation Planner (beta)',
    schemaEditor: 'Schema Manager',
    approvalsDesigner: 'Approval Rules',
    notifRuleRuns: 'Notification Rule Runs',
    notifLogs: 'Notification Logs',
    schedulerTasks: 'Task Scheduler',
    partners: 'Business Partners',
    partnerNew: 'New Partner',
    hrDept: 'Departments',
    hrEmps: 'Employees',
    hrEmpNew: 'New Employee',
    employmentTypes: 'Employment Types',
    policyEditor: 'Payroll Policy',
    payrollExecute: 'Payroll',
    timesheets: 'Timesheets',
    timesheetNew: 'New Timesheet',
    certRequest: 'Certificate Request',
    certList: 'My Requests',
    approvalsInbox: 'My Approvals',
    companySettings: 'Company Settings',
    accountingPeriods: 'Accounting Periods',
    inventory: 'Inventory',
    inventoryMaterials: 'Materials',
    inventoryMaterialNew: 'New Material',
    inventoryWarehouses: 'Warehouses',
    inventoryWarehouseNew: 'New Warehouse',
    inventoryBins: 'Bins',
    inventoryBinNew: 'New Bin',
    inventoryStatuses: 'Stock Statuses',
    inventoryBatches: 'Batches',
    inventoryBatchNew: 'New Batch',
    inventoryMovement: 'Movements',
    inventoryBalances: 'On-hand Balance',
    crm: 'CRM',
    crmContacts: 'Contacts',
    crmDeals: 'Deals',
    crmQuotes: 'Quotes',
    crmSalesOrders: 'Sales Orders',
    crmActivities: 'Activities',
    crmContactNew: 'New Contact',
    crmDealNew: 'New Deal',
    crmQuoteNew: 'New Quote',
    crmSalesOrderNew: 'New Sales Order',
    crmActivityNew: 'New Activity',
    recent: 'Recent'
  },
  chat: {
    aiTitle: 'AI Conversation',
    empty: 'Choose a page from the left or send the first message to get started.',
    placeholder: 'Chat with AI...',
    send: 'Send'
  },
  dialogs: {
    payroll: 'Payroll Calculation',
    todo: 'Tasks',
    todoEmpty: '(coming soon)',
    proofRequest: {
      title: 'Certificate Request',
      type: 'Certificate Type',
      customType: 'Custom Type',
      useCustom: 'Use Custom',
      purpose: 'Purpose & Notes',
      language: 'Language',
      email: 'Recipient Email',
      submit: 'Submit',
      success: 'Submitted successfully',
      auto: 'Auto fill',
      reason: 'Purpose & Notes',
      resignReason: 'Resignation Reason'
    }
  },
  tables: {
    voucherList: {
      title: 'Vouchers',
      date: 'Posting Date',
      type: 'Voucher Type',
      number: 'Voucher No.',
      summary: 'Summary',
      actions: 'Actions',
      view: 'View',
      createdAt: 'Created At',
      createdBy: 'Created By',
      updatedAt: 'Updated At',
      updatedBy: 'Updated By'
    },
    accounts: {
      title: 'Chart of Accounts',
      new: 'New Account',
      code: 'Code',
      name: 'Name',
      bspl: 'FS Category',
      tax: 'Tax Category',
      openItem: 'Open Item',
      bankCash: 'Bank / Cash',
      detail: 'Details',
      open: 'Enabled',
      close: 'Disabled',
      bank: 'Bank',
      cash: 'Cash',
      none: 'None',
      listEmpty: 'No data',
      save: 'Save',
      saved: 'Saved',
      failed: 'Save failed',
      bankDialog: 'Select Bank',
    branchDialog: 'Select Branch',
    openItemBaseline: 'Clearing Baseline',
    fieldRuleSection: 'Input Controls',
    bankCashSection: 'Bank / Cash',
    customerRule: 'Customer Control',
    vendorRule: 'Vendor Control',
    employeeRule: 'Employee Control',
    departmentRule: 'Department Control',
    paymentDateRule: 'Payment Date Control',
    fieldRuleRequired: 'Required',
    fieldRuleOptional: 'Optional',
    fieldRuleHidden: 'Hidden',
    baselineNone: 'None',
    baselineCustomer: 'Customer',
    baselineVendor: 'Vendor',
    baselineEmployee: 'Employee',
    taxOptionNonTax: 'Non-taxable',
    taxOptionInput: 'Input Tax',
    taxOptionOutput: 'Output Tax',
    taxOptionAccount: 'Tax Account',
    bankAccountFlag: 'Bank Account',
    cashAccountFlag: 'Cash Account',
    selectBankButton: 'Select Bank',
    selectBranchButton: 'Select Branch',
    bankName: 'Bank',
    branchName: 'Branch',
    accountType: 'Account Type',
    accountTypeOrdinary: 'Ordinary',
    accountTypeChecking: 'Checking',
    accountNo: 'Account No.',
    holder: 'Account Holder',
    currency: 'Currency',
    currencyJpy: 'JPY',
    currencyUsd: 'USD',
    currencyCny: 'CNY',
    cashCurrency: 'Cash Currency',
      taxMap: {
        nonTax: 'Non-taxable',
        input: 'Input Tax',
        output: 'Output Tax',
        account: 'Tax Account'
      },
      categoryMap: {
        bs: 'Balance Sheet',
        pl: 'P&L'
      }
    },
    voucherDetail: {
      title: 'Voucher Detail',
      date: 'Posting Date',
      type: 'Voucher Type',
      number: 'Voucher No.',
      summary: 'Summary',
      customer: 'Customer',
      vendor: 'Vendor',
      department: 'Department',
      employee: 'Employee',
      createdAt: 'Created At',
      createdBy: 'Created By',
      updatedAt: 'Updated At',
      updatedBy: 'Updated By'
    },
    inventoryBalances: {
      material: 'Material',
      warehouse: 'Warehouse',
      bin: 'Bin',
      status: 'Stock Status',
      batch: 'Batch',
      quantity: 'Quantity'
    },
    partners: {
      title: 'Business Partners',
      new: 'New Partner',
      code: 'Code',
      name: 'Name',
      customerVendor: 'Customer / Vendor',
      customerTag: 'Customer',
      vendorTag: 'Vendor',
      status: 'Status'
    },
    contacts: {
      title: 'Contacts',
      new: 'New Contact',
      code: 'Code',
      name: 'Name',
      partner: 'Partner Code',
      email: 'Email',
      status: 'Status'
    },
    deals: {
      title: 'Deals',
      new: 'New Deal',
      code: 'Deal Code',
      partner: 'Partner Code',
      stage: 'Stage',
      amount: 'Amount',
      closeDate: 'Expected Close',
      source: 'Source'
    },
    quotes: {
      title: 'Quotes',
      new: 'New Quote',
      number: 'Quote No.',
      partner: 'Partner Code',
      amount: 'Amount',
      validUntil: 'Valid Until',
      status: 'Status'
    },
    salesOrders: {
      title: 'Sales Orders',
      new: 'New Sales Order',
      number: 'Order No.',
      customer: 'Customer Code',
      amount: 'Amount',
      status: 'Status',
      issueDate: 'Issue Date'
    },
    activities: {
      title: 'Activities',
      new: 'New Activity',
      subject: 'Subject',
      type: 'Type',
      dueDate: 'Due Date',
      owner: 'Owner',
      status: 'Status'
    },
    accountingPeriods: {
      title: 'Accounting Periods',
      new: 'Add Period',
      newTitle: 'Add Accounting Period',
      editTitle: 'Edit Accounting Period',
      periodStart: 'Start Date',
      periodEnd: 'End Date',
      status: 'Status',
      open: 'Open',
      closed: 'Closed',
      memo: 'Memo',
      actions: 'Actions',
      edit: 'Edit',
      delete: 'Delete',
      save: 'Save',
      cancel: 'Cancel',
      createSuccess: 'Period created',
      updateSuccess: 'Period updated',
      deleteSuccess: 'Period deleted',
      saveFailed: 'Failed to save period',
      loadFailed: 'Failed to load accounting periods',
      deleteConfirm: 'Delete this accounting period?',
      deleteTitle: 'Confirm'
    }
  },
  columns: {
    drcr: 'Dr/Cr',
    amount: 'Amount'
  },
  dynamicForm: {
    addRow: 'Add Row',
    removeRow: 'Remove Row',
    select: 'Select',
    upload: 'Upload',
    actions: 'Actions',
    button: 'Button',
    value: 'Value'
  },
  schemaLabels: {
    code: 'Code',
    name: 'Name',
    description: 'Description',
    spec: 'Specification',
    baseuom: 'Base UOM',
    batchmanagement: 'Batch Control',
    category: 'Category',
    category1: 'Category 1',
    category2: 'Category 2',
    subcategory: 'Subcategory',
    origin: 'Origin',
    origincountry: 'Country of Origin',
    originCountry: 'Country of Origin',
    countryoforigin: 'Country of Origin',
    country: 'Country',
    countrycode: 'Country Code',
    countryCode: 'Country Code',
    type: 'Type',
    status: 'Status',
    warehousecode: 'Warehouse Code',
    warehousename: 'Warehouse Name',
    warehouse: 'Warehouse',
    address: 'Address',
    phone: 'Phone',
    contact: 'Contact',
    bincode: 'Bin Code',
    binname: 'Bin Name',
    bin: 'Bin',
    level: 'Level',
    remark: 'Remarks',
    remarks: 'Remarks',
    note: 'Note',
    memo: 'Memo',
    material: 'Material',
    materialcode: 'Material Code',
    materialname: 'Material Name',
    batch: 'Batch',
    batchno: 'Batch No.',
    quantity: 'Quantity',
    uom: 'UOM',
    unit: 'Unit',
    statuscode: 'Status Code',
    statusname: 'Status Name',
    allownegative: 'Allow Negative',
    movementtype: 'Movement Type',
    movementdate: 'Movement Date',
    fromwarehouse: 'From Warehouse',
    frombin: 'From Bin',
    towarehouse: 'To Warehouse',
    tobin: 'To Bin',
    lines: 'Lines',
    line: 'Line',
    attachments: 'Attachments',
    createdat: 'Created At',
    updatedat: 'Updated At',
    weight: 'Weight',
    length: 'Length',
    width: 'Width',
    height: 'Height',
    cost: 'Cost',
    price: 'Price',
    unitprice: 'Unit Price',
    leadtime: 'Lead Time',
    owner: 'Owner',
    manager: 'Manager',
    statuslabel: 'Status',
    effectivefrom: 'Effective From',
    effectiveto: 'Effective To',
    typename: 'Type Name',
    categoryname: 'Category Name',
    category: 'Category',
    categoryLarge: 'Category (L)',
    categorySmall: 'Category (S)',
    brand: 'Brand',
    model: 'Model',
    color: 'Color',
    material: 'Material',
    originCountry: 'Country of Origin',
    janCode: 'JAN Code',
    eanCode: 'EAN Code',
    description: 'Description',
    '__label__general': 'General',
    '__label__inventory': 'Inventory',
    '__label__logistics': 'Logistics',
    '__label__dimensions': 'Dimensions',
    '__label__pricing': 'Pricing',
    '__label__additional': 'Additional',
    generalinfo: 'General Info',
    inventoryinfo: 'Inventory Info',
    logisticsinfo: 'Logistics Info',
    dimension: 'Dimension',
    dimensions: 'Dimensions',
    pricing: 'Pricing',
    additional: 'Additional Info',
    'tab:lines': 'Lines',
    'tab:attachments': 'Attachments',
    'section:general': 'General',
    'section:inventory': 'Inventory',
    'section:logistics': 'Logistics',
    'section:dimensions': 'Dimensions',
    'section:pricing': 'Pricing',
    'section:additional': 'Additional',
    defaultwarehouse: 'Default Warehouse',
    defaultbin: 'Default Bin',
    defaultstatus: 'Default Status',
    defaultbatch: 'Default Batch',
    fromstatus: 'From Status',
    tostatus: 'To Status',
    reference: 'Reference',
    movementid: 'Movement ID',
    lineno: 'Line No.',
    reason: 'Reason',
    docstatus: 'Document Status',
    '__label__product': 'Product',
    '__label__semiproduct': 'Semi-product',
    '__label__rawmaterial': 'Raw Material',
    '__label__consumable': 'Consumable',
    '__label__active': 'Active',
    '__label__inactive': 'Inactive',
    'button:选择银行': 'Select Bank',
    'button:选择支店': 'Select Branch',
    schedulerTask: 'Schedule Task'
  },
  schemaList: {
    create: 'New',
    refresh: 'Refresh',
    createTitle: 'Create',
    loadFailed: 'Failed to load schema',
    layoutMissing: 'Form layout is empty'
  },
  voucherForm: {
    title: 'Voucher',
    actions: {
      save: 'Save',
      reset: 'Reset',
      addLine: 'Add Line',
      deleteLine: 'Delete',
      verifyInvoice: 'Verify'
    },
    header: {
      companyCode: 'Company Code',
      postingDate: 'Posting Date',
      voucherType: 'Voucher Type',
      currency: 'Currency',
      summary: 'Summary',
      invoiceRegistrationNo: 'Invoice Registration No.'
    },
    lines: {
      account: 'Account',
      drcr: 'Dr / Cr',
      amount: 'Amount (Tax Incl.)',
      taxRate: 'Tax Rate',
      taxAmount: 'Tax Amount',
      netAmount: 'Amount (Tax Excl.)',
      department: 'Department',
      employee: 'Employee',
      customer: 'Customer',
      vendor: 'Vendor',
      paymentDate: 'Payment Date',
      note: 'Note',
      actions: 'Actions'
    },
    totals: {
      prefix: 'Debit Total: {debit} / Credit Total: {credit}',
      imbalance: '(Not Balanced)'
    },
    placeholders: {
      account: 'Search by name or code',
      customer: 'Search customer name',
      vendor: 'Search vendor name',
      department: 'Search department name',
      employee: 'Search employee name'
    },
    messages: {
      saved: 'Saved: {no}',
      error: 'Save failed',
      posted: 'Posted voucher {no} on {date} ({type})',
      missingInputTaxAccount: 'Input tax account is not configured. Please set an account such as "Tax receivable" in Company Settings.',
      missingOutputTaxAccount: 'Output tax account is not configured. Please set an account such as "Tax payable" in Company Settings.',
      periodClosed: 'The accounting period is closed. Only text fields such as summary or notes can be updated.',
      invoiceInvalid: 'Invoice registration number must start with "T" followed by 13 digits.',
      invoiceNotFound: 'Registration number {no} was not found in the public registry.',
      invoiceInactive: 'Registration number {no} becomes valid on {date}.',
      invoiceExpired: 'Registration number {no} expired on {date}.',
      invoiceMatched: 'Registration number {no} is registered as {name}.',
      invoiceCheckFailed: 'Failed to verify invoice registration number.',
      invoiceRequired: 'Enter the invoice registration number.',
      invoiceUnchecked: 'Please verify the invoice registration number.'
    },
    typeOptions: {
      GL: 'General Ledger',
      AP: 'Accounts Payable',
      AR: 'Accounts Receivable',
      AA: 'Assets',
      SA: 'Sales',
      IN: 'Inventory',
      OT: 'Other'
    },
    drLabel: 'Debit',
    crLabel: 'Credit'
  },
  buttons: {
    search: 'Search',
    reset: 'Reset',
    close: 'Close',
    refresh: 'Refresh'
  },
  common: {
    enabled: 'Enabled',
    disabled: 'Disabled',
    view: 'View',
    save: 'Save',
    saved: 'Saved',
    saveFailed: 'Save failed',
    close: 'Close',
    loadFailed: 'Failed to load',
    backList: 'Back to List'
  },
  schemaEditor: {
    entity: 'Entity',
    saveNew: 'Save as New Version',
    saving: 'Saving...',
    saved: 'Saved'
  }
}

export const messages: Record<Lang, Messages> = { ja, en }


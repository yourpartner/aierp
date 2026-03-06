export type Lang = 'ja' | 'en' | 'zh'

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
    moneytreeTransactions: string
    financialReports: string
    financialDesigner: string
    consumptionTax: string
    cashLedger: string
    schemaEditor: string
    approvalsDesigner: string
    notifRuleRuns: string
    notifLogs: string
    schedulerTasks: string
    partners: string
    partnerNew: string
    hrDept: string
    hrOrg: string
    hrEmps: string
    hrEmpNew: string
    employmentTypes: string
    positionTypes: string
    policyEditor: string
    payrollExecute: string
    payrollHistory: string
    residentTax: string
    bonusCalc: string
    yearEndAdjustment: string
    timesheets: string
    timesheetNew: string
    certRequest: string
    certList: string
    companySettings: string
    userManagement: string
    roleManagement: string
    accountingPeriods: string
    monthlyClosing: string
    workflowRules: string
    agentScenarios: string
    agentRules: string
    agentSkills: string
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
    inventoryLedger: string
    inventoryCounts: string
    inventoryCountReport: string
    purchaseOrders: string
    purchaseOrderNew: string
    vendorInvoices: string
    vendorInvoiceNew: string
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
    crmOrderEntry: string
    orderMgmt: string
    crmDeliveryNotes: string
    crmSalesInvoices: string
    crmSalesInvoiceCreate: string
    crmSalesAnalytics: string
    crmSalesAlerts: string
    crmActivityNew: string
    recent: string
    groupFinance: string
    groupHR: string
    groupInventory: string
    groupOrders: string
    groupCRM: string
    groupSystem: string
    groupFixedAssets: string
    faClasses: string
    faList: string
    faDepreciation: string
    // 动态菜单翻译（后端 menu.xxx 对应）
    finance: string
    hr: string
    ai: string
    financialStatements: string
    financialNodes: string
    fixedAssets: string
    assetClasses: string
    assetsList: string
    depreciation: string
    cashFlow: string
    expenseClaims: string
    moneytree: string
    employees: string
    departments: string
    payroll: string
    system: string
    users: string
    roles: string
    notifications: string
    notificationRuns: string
    notificationLogs: string
    contacts: string
    deals: string
    quotes: string
    salesOrders: string
    activities: string
    businessPartners: string
    sales: string
    salesAnalytics: string
    salesAlerts: string
    salesInvoices: string
    deliveryNotes: string
    materials: string
    warehouses: string
    bins: string
    purchase: string
    // Staffing (人才派遣)
    staffing: string
    resourcePool: string
    staffingProjects: string
    staffingContracts: string
    staffingTimesheet: string
    staffingInvoices: string
    staffingAnalytics: string
    staffingEmail: string
    staffingEmailInbox: string
    staffingEmailTemplates: string
    staffingEmailRules: string
    staffingAi: string
    staffingAiMatching: string
    staffingAiMarket: string
    staffingAiAlerts: string
    // Portal
    portal: string
    portalDashboard: string
    portalTimesheet: string
    portalPayslip: string
    portalCertificates: string
    portalOrders: string
    portalInvoices: string
    portalPayments: string
  }
  login: {
    title: string
    companyCode: string
    employeeCode: string
    password: string
    submit: string
    required: string
    failed: string
    invalid: string
  }
  chat: {
    aiTitle: string
    empty: string
    placeholder: string
    send: string
    selectScenario: string
    scenarioApplied: string
    scenarioCleared: string
    scenarioInactive: string
    taskListTitle: string
    approvalTasksTitle: string
    approvalApprove: string
    approvalReject: string
    approvalDownload: string
    approvalApproveSuccess: string
    approvalApproveFailed: string
    approvalRejectSuccess: string
    approvalRejectFailed: string
    approvalDownloadFailed: string
    approvalEntityMap: Record<string, string>
    approvalStepLabel: string
    approvalApplicantLabel: string
    approvalApplicantNameLabel: string
    approvalApplicantCodeLabel: string
    approvalApplicantNoteLabel: string
    approvalApplicantResignReasonLabel: string
    approvalCreatedAtLabel: string
    approvalUserLabel: string
    approvalRemarkLabel: string
    generalTimelineTitle?: string
    generalModeButton?: string
    generalModeBanner?: string
    completedTasksTitle?: string
    completedTasksCount?: string
    taskStatus?: Record<string, string>
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
      detailDialog: string
      deleteConfirm: string
      deleteSuccess: string
      deleteFailed: string
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
      fsBalanceGroup: string
      fsProfitGroup: string
      fsGroupSection: string
      openItemBaseline: string
      fieldRuleSection: string
      bankCashSection: string
      customerRule: string
      vendorRule: string
      employeeRule: string
      departmentRule: string
      paymentDateRule: string
      assetRule: string
      fieldRuleRequired: string
      fieldRuleOptional: string
      fieldRuleHidden: string
      baselineNone: string
      baselineCustomer: string
      baselineVendor: string
      baselineEmployee: string
      taxOptionNonTax: string
      taxOptionInput: string
      taxOptionOutput: string
      taxOptionAccount: string
      bankAccountFlag: string
      cashAccountFlag: string
      selectBankButton: string
      selectBranchButton: string
      bankName: string
      branchName: string
      accountType: string
      accountTypeOrdinary: string
      accountTypeChecking: string
      accountNo: string
      holder: string
      currency: string
      currencyJpy: string
      currencyUsd: string
      currencyCny: string
      cashCurrency: string
      taxMap: Record<string, string>
      categoryMap: Record<string, string>
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
      paymentDate?: string
      note?: string
      invoiceRegistrationNo?: string
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
      shortName: string
      paymentTerm: string
      invoiceNo: string
      customerVendor: string
      customerTag: string
      vendorTag: string
      status: string
      contact: string
      postalCode: string
      address: string
      phone: string
      fax: string
      bankSection: string
      bankSelect: string
      branchSelect: string
      accountType: string
      accountNo: string
      accountHolderKana: string
      currency: string
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
      actions: string
      view: string
      detailTitle: string
      close: string
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
    workflowRules: {
      title: string
      new: string
      generatorTitle: string
      generatorPlaceholder: string
      generatorTip: string
      generateButton: string
      generateSuccess: string
      generateFail: string
      key: string
      titleCol: string
      description: string
      instructions: string
      priority: string
      active: string
      updated: string
      actions: string
      actionsField: string
      actionsPlaceholder: string
      actionsInvalid: string
      keyRequired: string
      editorCreate: string
      editorEdit: string
      test: string
      testFor: string
      testPayload: string
      testPayloadInvalid: string
      runTest: string
      testResult: string
      deleteConfirm: string
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
    monthlyClosing: {
      title: string
      detail: string
      start: string
      startSuccess: string
      startFailed: string
      loadFailed: string
      checkItems: string
      checkItem: string
      checkStatus: string
      checkMessage: string
      checkAction: string
      runChecks: string
      checkSuccess: string
      checkFailed: string
      checkConfirmed: string
      confirm: string
      manualCheck: string
      checkResult: string
      comment: string
      taxSummary: string
      outputTax: string
      inputTax: string
      netTax: string
      calcTax: string
      taxCalcSuccess: string
      taxCalcFailed: string
      submitApproval: string
      submitSuccess: string
      submitFailed: string
      close: string
      closeSuccess: string
      closeFailed: string
      closeConfirmTitle: string
      closeConfirmMessage: string
      closedMessage: string
      reopen: string
      reopenReason: string
      reopenReasonPlaceholder: string
      confirmReopen: string
      reopenSuccess: string
      reopenFailed: string
      steps: {
        checking: string
        adjusting: string
        approval: string
        closed: string
      }
      statusOpen: string
      statusChecking: string
      statusAdjusting: string
      statusPendingApproval: string
      statusClosed: string
      statusReopened: string
      statusPassed: string
      statusWarning: string
      statusFailed: string
      statusInfo: string
      statusPending: string
      statusSkipped: string
    }
    agentScenarios: {
      title: string
      new: string
      key: string
      titleCol: string
      description: string
      instructions: string
      executionSection: string
      executionHint: string
      executionThreshold: string
      executionThresholdPlaceholder: string
      executionLowMessage: string
      executionHighMessage: string
      executionMessagePlaceholder: string
      executionTokensHint: string
      templateCardTitle: string
      templateCardIntro: string
      templateCardItems: string[]
      tools: string
      toolsPlaceholder: string
      generatorTitle: string
      generatorPlaceholder: string
      generatorTip: string
      generateButton: string
      generateSuccess: string
      generateFail: string
      matcher: string
      matcherBoth: string
      matcherMessage: string
      matcherFile: string
      matcherMessageContains: string
      matcherMessageExcludes: string
      matcherMessageRegex: string
      matcherFileNameContains: string
      matcherMimeTypes: string
      matcherContentContains: string
      matcherAlways: string
      matcherScope: string
      matcherMatchAll: string
      priority: string
      active: string
      updated: string
      actions: string
      showAll: string
      editorCreate: string
      editorEdit: string
      metadata: string
      metadataTip: string
      metadataPlaceholder: string
      metadataInvalid: string
      context: string
      contextPlaceholder: string
      contextInvalid: string
      contextMessagesLabel: string
      contextAdd: string
      contextRemove: string
      keyRequired: string
      titleRequired: string
      deleteConfirm: string
      test: string
      testTitle: string
      testScenario: string
      testScenarioPlaceholder: string
      testMessage: string
      testMessagePlaceholder: string
      testFileName: string
      testFileNamePlaceholder: string
      testContentType: string
      testPreview: string
      testPreviewPlaceholder: string
      testMatched: string
      testSystemPrompt: string
      testContext: string
      contextMessages: string
      runTest: string
    }
    agentRules: {
      title: string
      titleCol: string
      keywords: string
      account: string
      priority: string
      active: string
      updated: string
      actions: string
      showInactive: string
      new: string
      formTitle: string
      formDescription: string
      formKeywords: string
      formKeywordsPlaceholder: string
      formAccountCode: string
      formAccountName: string
      formNote: string
      formOptions: string
      formOptionsPlaceholder: string
      editorCreate: string
      editorEdit: string
      createSuccess: string
      updateSuccess: string
      deleteSuccess: string
      deleteConfirm: string
      titleRequired: string
      optionsInvalid: string
    }
  }
  deliveryNotes: {
    deliveryNotes: string
    deliveryNotesSub: string
    generateFromOrders: string
    generateDialogTitle: string
    filterCustomer: string
    filterCustomerPlaceholder: string
    filterOrder: string
    filterOrderPlaceholder: string
    deliveryDate: string
    deliveryDatePlaceholder: string
    salesOrderNo: string
    customerCode: string
    customerName: string
    orderDate: string
    amountTotal: string
    actions: string
    generateSingle: string
    generateBatch: string
    selectedCount: string
    generateSuccess: string
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
      voucherNoRequired: string
      voucherTypeRequired: string
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
    edit: string
    save: string
    cancel: string
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
    edit: string
    delete: string
    deleted: string
    deleteFailed: string
    cancel: string
    logout: string
  }
  schemaEditor: {
    entity: string
    saveNew: string
    saving: string
    saved: string
  }
  financialCommon: {
    enabled: string
    disabled: string
    save: string
    saved: string
    close: string
    delete: string
    edit: string
    actions: string
    none: string
    confirmDelete: string
  }
  financialNodes: {
    title: string
    description: string
    add: string
    edit: string
    delete: string
    balanceSheet: string
    incomeStatement: string
    statement: string
    code: string
    nameJa: string
    nameEn: string
    parent: string
    parentPlaceholder: string
    order: string
    isSubtotal: string
    note: string
    saveSuccess: string
    deleteSuccess: string
    saveFailed: string
    deleteConfirm: string
  }
  financialReports: {
    title: string
    statement: string
    balanceSheet: string
    incomeStatement: string
    period: string
    periodRange: string
    periodRequired: string
    currency: string
    refreshBefore: string
    query: string
    exportPdf: string
    exportExcel: string
    noData: string
    name: string
    amount: string
    loadFailed: string
  }
}

export const ja: Messages = {
  appTitle: 'iTBank Sfin - シンプルファイナンス',
  nav: {
    chat: 'チャット会話',
    newSession: '新規会話',
    common: '共通メニュー',
    vouchers: '会計伝票一覧',
    voucherNew: '新規伝票',
    accounts: '勘定科目一覧',
    accountLedger: '勘定明細一覧',
    accountBalance: '勘定残高',
    trialBalance: '合計残高試算表',
    ledgerExport: '帳簿出力',
    accountNew: '科目登録',
    bankReceipt: '銀行入金',
    bankPayment: '銀行出金配分',
    fbPayment: '自動支払',
    bankPlanner: '銀行入金配分',
    moneytreeTransactions: '銀行明細',
    financialReports: '財務諸表',
    financialDesigner: '財務諸表構成',
    consumptionTax: '消費税申告書',
    cashLedger: '現金出納帳',
    schemaEditor: 'スキーマ管理',
    approvalsDesigner: '承認ルール',
    notifRuleRuns: '通知ルール実行履歴',
    notifLogs: '通知送信ログ',
    schedulerTasks: 'タスクスケジューラ',
    partners: '取引先一覧',
    partnerNew: '取引先登録',
    hrDept: '部門階層',
    hrOrg: '組織図',
    hrEmps: '社員一覧',
    hrEmpNew: '社員登録',
    employmentTypes: '雇用区分',
    positionTypes: '役職マスタ',
    policyEditor: '給与ポリシー',
    payrollExecute: '給与計算',
    payrollHistory: '給与履歴',
    residentTax: '住民税',
    bonusCalc: '賞与計算',
    yearEndAdjustment: '年末調整',
    timesheets: '工数一覧',
    timesheetNew: '工数入力',
    certRequest: '証明書申請',
    certList: '申請履歴',
    companySettings: '会社設定',
    userManagement: 'ユーザー管理',
    roleManagement: 'ロール管理',
    accountingPeriods: '会計期間',
    monthlyClosing: '月次締め',
    workflowRules: 'ワークフロールール',
    agentScenarios: 'エージェントシナリオ',
    agentRules: 'AI会計ルール',
    agentSkills: 'Agent Skill設定',
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
    inventoryLedger: '入出庫台帳',
    inventoryCounts: '棚卸',
    inventoryCountReport: '棚卸差異レポート',
    purchaseOrders: '発注一覧',
    purchaseOrderNew: '発注登録',
    vendorInvoices: '請求書一覧',
    vendorInvoiceNew: '請求書登録',
    crm: 'CRM',
    crmContacts: 'コンタクト一覧',
    crmDeals: '商談一覧',
    crmQuotes: '見積一覧',
    crmSalesOrders: '受注一覧',
    orderMgmt: '受注管理',
    crmDeliveryNotes: '納品書一覧',
    crmSalesInvoices: '請求書一覧',
    crmSalesInvoiceCreate: '請求書作成',
    crmSalesAnalytics: '販売分析',
    crmSalesAlerts: '販売アラート',
    crmActivities: '活動一覧',
    crmContactNew: 'コンタクト登録',
    crmDealNew: '商談登録',
    crmQuoteNew: '見積登録',
    crmSalesOrderNew: '受注登録',
    crmOrderEntry: '受注登録',
    crmActivityNew: '活動登録',
    recent: '最近のページ',
    groupFinance: '財務会計',
    groupHR: '人事管理',
    groupInventory: '在庫購買',
    groupOrders: '受注管理',
    groupCRM: 'CRM',
    groupSystem: 'システム設定',
    groupFixedAssets: '固定資産',
    faClasses: '資産クラス管理',
    faList: '固定資産',
    faDepreciation: '定期償却記帳',
    // 动态菜单翻译（后端 menu.xxx 对应，只包含缺失的 key）
    finance: '財務会計',
    hr: '人事管理',
    ai: 'AI',
    financialStatements: '財務諸表',
    financialNodes: '財務諸表構成',
    fixedAssets: '固定資産',
    assetClasses: '資産クラス管理',
    assetsList: '固定資産一覧',
    depreciation: '定期償却記帳',
    cashFlow: '資金繰り',
    expenseClaims: '経費精算一覧',
    moneytree: '銀行明細',
    employees: '社員一覧',
    departments: '部門階層',
    payroll: '給与管理',
    system: 'システム設定',
    users: 'ユーザー管理',
    roles: 'ロール管理',
    notifications: '通知',
    notificationRuns: '通知ルール実行履歴',
    notificationLogs: '通知送信ログ',
    contacts: 'コンタクト一覧',
    deals: '商談一覧',
    quotes: '見積一覧',
    salesOrders: '受注一覧',
    activities: '活動一覧',
    businessPartners: '取引先一覧',
    sales: '販売管理',
    salesAnalytics: '販売分析',
    salesAlerts: '販売アラート',
    salesInvoices: '請求書一覧',
    deliveryNotes: '納品書一覧',
    materials: '品目一覧',
    warehouses: '倉庫一覧',
    bins: '棚番一覧',
    purchase: '購買管理',
    // Staffing (人材派遣)
    staffing: '人材派遣',
    resourcePool: 'リソースプール',
    staffingProjects: '案件',
    staffingContracts: '契約',
    staffingTimesheet: '勤怠',
    staffingInvoices: '請求書',
    staffingAnalytics: '分析',
    staffingEmail: 'メール',
    staffingEmailInbox: '受信箱',
    staffingEmailTemplates: 'テンプレート',
    staffingEmailRules: '自動処理ルール',
    staffingAi: 'AIアシスタント',
    staffingAiMatching: 'マッチング',
    staffingAiMarket: '市場分析',
    staffingAiAlerts: 'アラート',
    // Portal
    portal: 'ポータル',
    portalDashboard: 'ダッシュボード',
    portalTimesheet: '勤怠入力',
    portalPayslip: '給与明細',
    portalCertificates: '証明書申請',
    portalOrders: '案件',
    portalInvoices: '請求書',
    portalPayments: '入金'
  },
  login: {
    title: 'ログイン',
    companyCode: '会社コード',
    employeeCode: 'アカウント',
    password: 'パスワード',
    submit: 'ログイン',
    required: '会社コード、社員コード、パスワードを入力してください',
    failed: 'ログインに失敗しました',
    invalid: '会社コード、社員コード、またはパスワードが正しくありません'
  },
  chat: {
    aiTitle: 'AI チャット',
    empty: '左のメニューからページを開くか、最初のメッセージを送信してください',
    placeholder: 'AI と会話する...',
    send: '送信',
    selectScenario: 'シナリオ',
    scenarioApplied: 'シナリオ「{name}」に切り替えました',
    scenarioCleared: 'シナリオを解除しました（デフォルト動作）',
    scenarioInactive: '（無効）',
    taskListTitle: 'タスク一覧',
    completedTasksTitle: '完了タスク',
    completedTasksCount: '完了タスク（{count}）',
    taskStatus: {
      pending: '未処理',
      in_progress: '処理中',
      completed: '完了',
      failed: '失敗',
      cancelled: 'キャンセル',
      approved: '承認済み',
      rejected: '却下'
    },
    salesOrderTaskLabel: '受注',
    salesOrderNoLabel: '受注番号',
    salesOrderCustomerLabel: '得意先',
    salesOrderOrderDateLabel: '受注日',
    salesOrderDeliveryDateLabel: '納期',
    salesOrderAmountLabel: '受注金額',
    salesOrderShipToLabel: '納品先',
    salesOrderLinesLabel: '明細',
    salesOrderLineNoLabel: '行',
    salesOrderLineItemLabel: '品目',
    salesOrderLineQtyLabel: '数量',
    salesOrderLineUnitPriceLabel: '単価',
    salesOrderLineAmountLabel: '金額',
    approvalTasksTitle: '承認待ちタスク',
    approvalApprove: '承認',
    approvalReject: '却下',
    approvalDownload: 'PDFプレビュー',
    approvalApproveSuccess: '承認しました',
    approvalApproveFailed: '承認に失敗しました',
    approvalRejectSuccess: '却下しました',
    approvalRejectFailed: '操作に失敗しました',
    approvalDownloadFailed: 'ダウンロードに失敗しました',
    approvalEntityMap: {
      certificate_request: '証明書申請承認'
    },
    approvalStepLabel: '承認ステップ',
    approvalApplicantLabel: '申請者',
    approvalApplicantNameLabel: '申請者氏名',
    approvalApplicantCodeLabel: '社員コード',
    approvalApplicantNoteLabel: '申請メモ',
    approvalApplicantResignReasonLabel: '退職理由',
    approvalCreatedAtLabel: '申請日',
    approvalUserLabel: 'ユーザー',
    approvalRemarkLabel: '備考',
    generalTimelineTitle: 'AI会話',
    generalModeButton: 'フリーチャット',
    generalModeBanner: 'フリーチャットモードです。メッセージはタスクに紐づきません。'
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
      openItem: '消込管理',
      bankCash: '銀行/現金',
      fsBalanceGroup: 'BSグループ',
      fsProfitGroup: 'PLグループ',
      fsGroupSection: '財務諸表グループ',
      detail: '詳細',
      detailDialog: '勘定科目詳細',
      deleteConfirm: 'この勘定科目を削除しますか？',
      deleteSuccess: '削除しました',
      deleteFailed: '削除に失敗しました',
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
    fieldRuleSection: '入力フィールド状態制御',
    bankCashSection: '銀行 / 現金',
    customerRule: '顧客入力制御',
    vendorRule: '仕入先入力制御',
    employeeRule: '従業員入力制御',
    departmentRule: '部門入力制御',
    paymentDateRule: '支払日入力制御',
    assetRule: '固定資産入力制御',
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
      updatedBy: '更新者',
      paymentDate: '支払日',
      note: '備考',
      invoiceRegistrationNo: 'インボイス登録番号'
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
      shortName: '略称',
      paymentTerm: '支払条件',
      invoiceNo: 'インボイス番号',
      customerVendor: '顧客/仕入先',
      customerTag: '顧客',
      vendorTag: '仕入先',
      status: 'ステータス',
      contact: '連絡情報',
      postalCode: '郵便番号',
      address: '住所',
      phone: '電話番号',
      fax: 'FAX',
      bankSection: '銀行口座',
      bankSelect: '銀行を選択',
      branchSelect: '支店を選択',
      accountType: '口座種別',
      accountNo: '口座番号',
      accountHolderKana: '名義人（半角カナ）',
      currency: '通貨'
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
      customer: '得意先',
      amount: '合計（税込）',
      status: 'ステータス',
      issueDate: '受注日',
      actions: '操作',
      view: '詳細',
      detailTitle: '受注詳細',
      close: '閉じる'
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
    workflowRules: {
      title: 'ワークフロールール',
      new: '新規ルール',
      generatorTitle: '自然言語からルールを生成',
      generatorPlaceholder: '例：レストラン領収書をアップロードしたら自動で会議費/現金で仕訳を起票',
      generatorTip: 'ルール化したいシナリオを日本語または中国語で入力してください',
      generateButton: 'AIで生成',
      generateSuccess: 'AI がルール案を生成しました',
      generateFail: 'AI 生成に失敗しました',
      key: 'ルールキー',
      titleCol: 'タイトル',
      description: '説明',
      instructions: '適用条件',
      priority: '優先度',
      active: '有効',
      updated: '更新日時',
      actions: '操作',
      actionsField: '実行アクション（JSON）',
      actionsPlaceholder: '{\n  "type": "voucher.autoCreate",\n  "params": { ... }\n}',
      actionsInvalid: 'アクションは JSON 配列で入力してください',
      keyRequired: 'ルールキーを入力してください',
      editorCreate: '新規ルール',
      editorEdit: 'ルール編集',
      test: 'テスト',
      testFor: 'テスト対象',
      testPayload: 'テスト用ペイロード(JSON)',
      testPayloadInvalid: 'テストペイロードの JSON 形式が不正です',
      runTest: 'テストを実行',
      testResult: 'テスト結果',
      deleteConfirm: 'このルールを無効化しますか？'
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
    },
    monthlyClosing: {
      title: '月次締め',
      detail: '詳細',
      start: '月次締め開始',
      startSuccess: '月次締めを開始しました',
      startFailed: '月次締めの開始に失敗しました',
      loadFailed: 'データの取得に失敗しました',
      checkItems: 'チェック項目',
      checkItem: '項目',
      checkStatus: 'ステータス',
      checkMessage: 'メッセージ',
      checkAction: '操作',
      runChecks: '全チェック実行',
      checkSuccess: 'チェックが完了しました',
      checkFailed: 'チェックに失敗しました',
      checkConfirmed: '確認を登録しました',
      confirm: '確認',
      manualCheck: '手動確認',
      checkResult: '確認結果',
      comment: 'コメント',
      taxSummary: '消費税集計',
      outputTax: '仮受消費税',
      inputTax: '仮払消費税',
      netTax: '差引',
      calcTax: '消費税集計',
      taxCalcSuccess: '消費税を集計しました',
      taxCalcFailed: '消費税の集計に失敗しました',
      submitApproval: '承認申請',
      submitSuccess: '承認申請しました',
      submitFailed: '承認申請に失敗しました',
      close: '月次締め確定',
      closeSuccess: '月次締めが完了しました',
      closeFailed: '月次締めに失敗しました',
      closeConfirmTitle: '確認',
      closeConfirmMessage: 'この月を締めますか？締め後は仕訳の追加・変更ができなくなります。',
      closedMessage: '月次締め済み',
      reopen: '締め解除',
      reopenReason: '解除理由',
      reopenReasonPlaceholder: '締めを解除する理由を入力してください',
      confirmReopen: '締め解除実行',
      reopenSuccess: '締めを解除しました',
      reopenFailed: '締め解除に失敗しました',
      steps: {
        checking: 'チェック',
        adjusting: '調整',
        approval: '承認',
        closed: '締め'
      },
      statusOpen: '未開始',
      statusChecking: '確認中',
      statusAdjusting: '調整中',
      statusPendingApproval: '承認待ち',
      statusClosed: '締め済み',
      statusReopened: '再開済み',
      statusPassed: 'OK',
      statusWarning: '警告',
      statusFailed: 'エラー',
      statusInfo: '情報',
      statusPending: '未確認',
      statusSkipped: 'スキップ'
    },
    agentScenarios: {
      title: 'エージェントシナリオ',
      new: 'シナリオ追加',
      key: 'シナリオキー',
      titleCol: 'タイトル',
      description: '説明',
      instructions: '指示',
      executionSection: '実行ヒント',
      executionHint: 'ここで設定したしきい値や文言は AI へのシステムメッセージとして利用されます。未入力の場合は既定値 (20,000 JPY) が適用されます。',
      executionThreshold: '正味金額のしきい値（JPY）',
      executionThresholdPlaceholder: '未入力で 20,000',
      executionLowMessage: 'しきい値未満時のメッセージ',
      executionHighMessage: 'しきい値以上時のメッセージ',
      executionMessagePlaceholder: '利用可能なプレースホルダー: {{netAmount}}, {{currency}}, {{threshold}}',
      executionTokensHint: 'プレースホルダー: {{netAmount}}, {{currency}}, {{threshold}}',
      templateCardTitle: '📌 飲食費インボイスの主要ルール',
      templateCardIntro: 'このテンプレートでは次の条件に基づいて自動仕訳を実行します。',
      templateCardItems: [
        '税抜金額が 20,000 円未満の場合は会議費(6200)で即時仕訳し、追加質問は行いません。',
        '税抜金額が 20,000 円以上の場合は人数と参加者氏名を確認します。',
        '人均金額 > 10,000 円の場合は交際費(6250)、≤ 10,000 円の場合は会議費(6200)を使用します。',
        'インボイス登録番号を検証し、伝票作成後は番号をユーザーに返します。'
      ],
      tools: '推奨ツール',
      toolsPlaceholder: 'ツール名を入力して Enter で追加',
      generatorTitle: 'AI シナリオ生成',
      generatorPlaceholder: '目的・トリガー・期待する処理などを自然言語で入力してください',
      generatorTip: '生成した下書きは保存前に編集できます。',
      generateButton: 'AI で下書きを生成',
      generateSuccess: 'AI が下書きを生成しました',
      generateFail: 'AI 生成に失敗しました',
      matcher: 'マッチ条件',
      matcherBoth: 'メッセージとファイル',
      matcherMessage: 'メッセージのみ',
      matcherFile: 'ファイルのみ',
      matcherMessageContains: 'メッセージに含めるキーワード',
      matcherMessageExcludes: 'メッセージから除外するキーワード',
      matcherMessageRegex: 'メッセージ正規表現',
      matcherFileNameContains: 'ファイル名に含めるキーワード',
      matcherMimeTypes: 'MIME タイプ',
      matcherContentContains: '内容に含めるキーワード',
      matcherAlways: '常に適用',
      matcherScope: '対象',
      matcherMatchAll: 'すべてのキーワード必須',
      modeLabel: '編集モード',
      modeSimple: 'かんたん設定',
      modeAdvanced: '詳細設定',
      simpleHint: 'キーワードだけで高速に設定できます。細かな条件が必要な場合は詳細設定に切り替えてください。',
      simpleMessage: 'チャットに含めるキーワード',
      simpleContent: 'ファイル内容に含めるキーワード',
      simpleFileTypes: '対象ファイル形式（MIME）',
      advancedHiddenTip: '詳細設定の内容が既に存在します。編集する場合は「詳細設定」モードに切り替えてください。',
      priority: '優先度',
      active: '有効',
      updated: '更新日時',
      actions: '操作',
      showAll: '無効を表示',
      sectionBasic: '基本情報',
      sectionMatcher: 'マッチ条件',
      sectionAdvanced: 'コンテキスト / 高度な設定',
      matcherHint: 'ここで設定したキーワードや MIME タイプに一致すると、このシナリオが自動的に選択されます。',
      contextHint: 'AI に毎回渡したい追加情報やメッセージを設定できます。JSON でのメタデータもここで管理します。',
      tagPlaceholder: '入力後 Enter で追加',
      quickIntro: '利用の流れ',
      quickStep1: '① 基本情報（シナリオキー・タイトル・説明）を入力します。',
      quickStep2: '② メッセージ/ファイルのキーワードや MIME タイプなどのマッチ条件を設定します。',
      quickStep3: '③ 必要に応じてコンテキストやメタデータを追加し、保存します。',
      metadata: 'メタデータ（JSON）',
      metadataTip: '上記以外に渡したい設定があれば JSON で追記できます。空欄でも問題ありません。',
      metadataPlaceholder: '追加メタデータを JSON 形式で入力（任意）',
      metadataInvalid: 'メタデータの JSON が不正です',
      context: 'ランタイムコンテキスト（JSON）',
      contextPlaceholder: 'シナリオ実行時に注入する追加情報（任意／JSON）',
      contextInvalid: 'コンテキストの JSON が不正です',
      contextMessagesLabel: 'コンテキストメッセージ',
      contextAdd: 'メッセージ追加',
      contextRemove: '削除',
      keyRequired: 'シナリオキーは必須です',
      titleRequired: 'タイトルは必須です',
      deleteConfirm: 'このシナリオを削除しますか？',
      test: 'テスト',
      testTitle: 'シナリオテスト',
      testScenario: '対象シナリオ',
      testScenarioPlaceholder: '（任意）特定のシナリオキーを指定',
      testMessage: 'メッセージ',
      testMessagePlaceholder: 'ユーザー入力を想定して記入',
      testFileName: 'ファイル名',
      testFileNamePlaceholder: '例：receipt.jpg',
      testContentType: 'コンテンツタイプ',
      testPreview: 'テキストプレビュー',
      testPreviewPlaceholder: 'OCR や抽出テキストを貼り付け',
      testMatched: 'マッチしたシナリオ',
      testSystemPrompt: '生成されたシステムプロンプト',
      testContext: '注入されたコンテキストメッセージ',
      runTest: 'テストを実行'
    },
    agentRules: {
      title: 'AI会計ルール',
      titleCol: 'タイトル',
      keywords: 'キーワード',
      account: '推奨科目',
      priority: '優先度',
      active: '有効',
      updated: '更新日時',
      actions: '操作',
      showInactive: '無効ルールを表示',
      new: 'ルール追加',
      formTitle: 'タイトル',
      formDescription: '説明',
      formKeywords: '一致キーワード',
      formKeywordsPlaceholder: 'キーワードを入力し Enter で追加',
      formAccountCode: '推奨科目コード',
      formAccountName: '推奨科目名',
      formNote: '推奨メモ',
      formOptions: '追加設定 (JSON)',
      formOptionsPlaceholder: '{ "taxRate": 0.1 }',
      editorCreate: 'ルール追加',
      editorEdit: 'ルール編集',
      createSuccess: 'ルールを作成しました',
      updateSuccess: 'ルールを更新しました',
      deleteSuccess: 'ルールを削除しました',
      deleteConfirm: 'このルールを削除しますか？',
      titleRequired: 'タイトルを入力してください',
      optionsInvalid: '追加設定の JSON 形式が正しくありません'
    }
  },
  columns: {
    drcr: '借方/貸方',
    amount: '金額'
  },
  bankPayment: {
    title: '銀行出金配分',
    clearingAccount: '消込科目',
    selectAccount: '科目を選択',
    partner: '取引先',
    optional: '任意',
    bankAccount: '出金口座',
    bankPlaceholder: '銀行/現金科目',
    paymentAmount: '出金金額',
    amount: '金額',
    paymentDate: '出金日',
    date: '日付',
    feeBearer: '手数料負担',
    vendorBears: '先方負担',
    companyBears: '当社負担',
    feeAmount: '手数料額',
    feeAccount: '手数料科目',
    account: '科目',
    bearer: '負担',
    noOpenItems: '未消込項目は見つかりません',
    docDate: '伝票日付',
    voucherNo: '伝票番号',
    originalAmount: '原金額',
    residualAmount: '未消込残高',
    applyAmount: '今回消込',
    remark: '摘要',
    clearingTotal: '消込金額',
    actualPayment: '実出金',
    fee: '手数料',
    mismatch: '不一致',
    execute: '出金実行',
    success: '出金完了',
    failed: '処理に失敗しました'
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
    isbatchmgmt: 'ロット管理',
    batchmgmt: 'ロット管理',
    isbatchmanagement: 'ロット管理',
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
    categoryLarge: 'カテゴリ(L)',
    categorySmall: 'カテゴリ(S)',
    brand: 'ブランド',
    model: '型番',
    color: 'カラー',
    janCode: 'JANコード',
    eanCode: 'EANコード',
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
    schedulerTask: 'スケジュールタスク',
    primaryimageurl: '商品画像',
    primaryimage: '商品画像',
    primaryimageblobname: '画像ファイル名',
    primaryimagecontenttype: '画像形式',
    primaryimagefilename: '画像ファイル名',
    imageurl: '画像URL',
    image: '画像',
    weightuom: '重量単位',
    weightunit: '重量単位',
    standardcost: '標準原価',
    standardprice: '標準単価',
    purchaseprice: '仕入単価',
    saleprice: '販売価格',
    sellingprice: '販売価格',
    reorderpoint: '発注点',
    reorderqty: '発注数量',
    reorderquantity: '発注数量',
    minstock: '最小在庫数',
    maxstock: '最大在庫数',
    minqty: '最小数量',
    maxqty: '最大数量',
    minquantity: '最小数量',
    maxquantity: '最大数量',
    shelflife: '賞味期限日数',
    expirydate: '有効期限',
    expirationdate: '有効期限',
    manufacturedate: '製造日',
    mfgdate: '製造日',
    productiondate: '製造日',
    batchdate: 'ロット日',
    barcode: 'バーコード',
    barcodes: 'バーコード',
    jancode: 'JANコード',
    eancode: 'EANコード',
    suppliercode: '仕入先コード',
    supplier: '仕入先',
    vendor: '仕入先',
    vendorcode: '仕入先コード',
    taxrate: '税率',
    taxcode: '税コード',
    accountcode: '勘定科目コード',
    glaccountcode: '勘定科目コード',
    glaccount: '勘定科目',
    stockaccount: '在庫勘定科目',
    active: '有効',
    enabled: '有効',
    disabled: '無効',
    sortorder: '表示順',
    displayorder: '表示順',
    parentcode: '親コード',
    parentname: '親名称'
  },
  deliveryNotes: {
    deliveryNotes: '納品書管理',
    deliveryNotesSub: '受注から納品書を作成・管理',
    generateFromOrders: '受注から生成',
    generateDialogTitle: '受注から納品書生成',
    filterCustomer: '得意先',
    filterCustomerPlaceholder: '得意先コード/名称を入力',
    filterOrder: '受注番号',
    filterOrderPlaceholder: '受注番号で検索',
    deliveryDate: '納品日',
    deliveryDatePlaceholder: '納品日を選択',
    salesOrderNo: '受注番号',
    customerCode: '得意先コード',
    customerName: '得意先名',
    orderDate: '受注日',
    amountTotal: '受注金額',
    actions: '操作',
    generateSingle: '生成',
    generateBatch: '選択分を生成',
    selectedCount: '選択中：{count} 件',
    generateSuccess: '納品書を生成しました'
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
      invoiceUnchecked: 'インボイス登録番号を照会してください。',
      voucherNoRequired: '伝票番号を入力してください。',
      voucherTypeRequired: '伝票種別を選択してください。'
    },
    typeOptions: {
      GL: '総勘定元帳',
      AP: '買掛金',
      AR: '売掛金',
      AA: '資産',
      SA: '給与',
      IN: '入金',
      OT: '出金'
    },
    drLabel: '借方',
    crLabel: '貸方'
  },
  buttons: {
    search: '検索',
    reset: 'リセット',
    close: '閉じる',
    refresh: '再読み込み',
    edit: '編集',
    save: '保存',
    cancel: 'キャンセル'
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
    backList: '一覧に戻る',
    edit: '編集',
    delete: '削除',
    deleted: '削除しました',
    deleteFailed: '削除に失敗しました',
    cancel: 'キャンセル',
    logout: 'ログアウト'
  },
  schemaEditor: {
    entity: '対象エンティティ',
    saveNew: '新しいバージョンとして保存',
    saving: '保存中...',
    saved: '保存しました'
  },
  financialCommon: {
    enabled: '有効',
    disabled: '無効',
    save: '保存',
    saved: '保存しました',
    close: '閉じる',
    delete: '削除',
    edit: '編集',
    actions: '操作',
    none: 'データがありません',
    confirmDelete: '削除してもよろしいですか？'
  },
  financialNodes: {
    title: '財務諸表構成',
    description: '貸借対照表・損益計算書に表示するグループと階層を定義します',
    add: 'グループ追加',
    edit: '編集',
    delete: '削除',
    balanceSheet: '貸借対照表',
    incomeStatement: '損益計算書',
    statement: '財務諸表',
    code: 'コード',
    nameJa: '名称（日本語）',
    nameEn: '名称（英語）',
    parent: '親グループ',
    parentPlaceholder: '親グループを選択',
    order: '表示順',
    isSubtotal: '小計行',
    note: '備考',
    saveSuccess: '保存しました',
    deleteSuccess: '削除しました',
    saveFailed: '保存に失敗しました',
    deleteConfirm: '{name} を削除しますか？'
  },
  financialReports: {
    title: '財務諸表',
    statement: '財務諸表',
    balanceSheet: '貸借対照表',
    incomeStatement: '損益計算書',
    period: '対象期間',
    periodRange: '期間範囲',
    periodRequired: '期間を指定してください',
    currency: '通貨',
    refreshBefore: '集計前に再計算',
    query: '集計',
    exportPdf: 'PDF出力',
    exportExcel: 'Excel出力',
    noData: 'データがありません',
    name: '勘定・項目',
    amount: '金額',
    loadFailed: '取得に失敗しました'
  }
}

export const en: Messages = {
  appTitle: 'iTBank Sfin - Simple Finance',
  nav: {
    chat: 'Chat Conversation',
    newSession: 'New Conversation',
    common: 'Shortcut Menu',
    vouchers: 'Vouchers',
    voucherNew: 'New Voucher',
    accounts: 'Chart of Accounts',
    accountLedger: 'Account Ledger',
    accountBalance: 'Account Balance',
    trialBalance: 'Trial Balance',
    ledgerExport: 'Ledger Export',
    accountNew: 'Create Account',
    bankReceipt: 'Bank Receipt',
    bankPayment: 'Bank Payment Allocation',
    fbPayment: 'Auto Payment',
    bankPlanner: 'Bank Receipt Allocation',
    moneytreeTransactions: 'Bank Transactions',
    financialReports: 'Financial Statements',
    financialDesigner: 'Statement Designer',
    consumptionTax: 'Consumption Tax Return',
    cashLedger: 'Cash Ledger',
    schemaEditor: 'Schema Manager',
    approvalsDesigner: 'Approval Rules',
    notifRuleRuns: 'Notification Rule Runs',
    notifLogs: 'Notification Logs',
    schedulerTasks: 'Task Scheduler',
    partners: 'Business Partners',
    partnerNew: 'New Partner',
    hrDept: 'Departments',
    hrOrg: 'Organization',
    hrEmps: 'Employees',
    hrEmpNew: 'New Employee',
    employmentTypes: 'Employment Types',
    positionTypes: 'Position Types',
    policyEditor: 'Payroll Policy',
    payrollExecute: 'Payroll',
    payrollHistory: 'Payroll History',
    residentTax: 'Resident Tax',
    bonusCalc: 'Bonus Calculation',
    yearEndAdjustment: 'Year-End Adjustment',
    timesheets: 'Timesheets',
    timesheetNew: 'New Timesheet',
    certRequest: 'Certificate Request',
    certList: 'My Requests',
    companySettings: 'Company Settings',
    userManagement: 'User Management',
    roleManagement: 'Role Management',
    accountingPeriods: 'Accounting Periods',
    monthlyClosing: 'Monthly Closing',
    workflowRules: 'Workflow Rules',
    agentScenarios: 'Agent Scenarios',
    agentRules: 'AI Accounting Rules',
    agentSkills: 'Agent Skill',
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
    inventoryLedger: 'In/Out Ledger',
    inventoryCounts: 'Stock Count',
    inventoryCountReport: 'Variance Report',
    purchaseOrders: 'Purchase Orders',
    purchaseOrderNew: 'New Purchase Order',
    vendorInvoices: 'Vendor Invoices',
    vendorInvoiceNew: 'New Vendor Invoice',
    crm: 'CRM',
    crmContacts: 'Contacts',
    crmDeals: 'Deals',
    crmQuotes: 'Quotes',
    crmSalesOrders: 'Sales Orders',
    orderMgmt: 'Order Management',
    crmDeliveryNotes: 'Delivery Notes',
    crmSalesInvoices: 'Sales Invoices',
    crmSalesInvoiceCreate: 'Create Invoice',
    crmSalesAnalytics: 'Sales Analytics',
    crmSalesAlerts: 'Sales Alerts',
    crmActivities: 'Activities',
    crmContactNew: 'New Contact',
    crmDealNew: 'New Deal',
    crmQuoteNew: 'New Quote',
    crmSalesOrderNew: 'New Sales Order',
    crmOrderEntry: 'Sales Order Entry',
    crmActivityNew: 'New Activity',
    recent: 'Recent',
    groupFinance: 'Finance & Accounting',
    groupHR: 'HR Management',
    groupInventory: 'Inventory & Purchasing',
    groupOrders: 'Order Management',
    groupCRM: 'CRM',
    groupSystem: 'System Settings',
    groupFixedAssets: '固定資産',
    faClasses: '資産クラス管理',
    faList: '固定資産一覧',
    faDepreciation: '定期償却記帳',
    // Dynamic menu translations (backend menu.xxx mapping)
    finance: 'Finance & Accounting',
    hr: 'HR Management',
    ai: 'AI',
    financialStatements: 'Financial Statements',
    financialNodes: 'Statement Designer',
    fixedAssets: '固定資産',
    assetClasses: '資産クラス管理',
    assetsList: '固定資産一覧',
    depreciation: '定期償却記帳',
    cashFlow: 'Cash Flow',
    expenseClaims: 'Expense Claims',
    moneytree: 'Bank Transactions',
    employees: 'Employees',
    departments: 'Departments',
    payroll: 'Payroll',
    system: 'System Settings',
    users: 'User Management',
    roles: 'Role Management',
    notifications: 'Notifications',
    notificationRuns: 'Notification Runs',
    notificationLogs: 'Notification Logs',
    contacts: 'Contacts',
    deals: 'Deals',
    quotes: 'Quotes',
    salesOrders: 'Sales Orders',
    activities: 'Activities',
    businessPartners: 'Business Partners',
    sales: 'Sales',
    salesAnalytics: 'Sales Analytics',
    salesAlerts: 'Sales Alerts',
    salesInvoices: 'Sales Invoices',
    deliveryNotes: 'Delivery Notes',
    materials: 'Materials',
    warehouses: 'Warehouses',
    bins: 'Bins',
    purchase: 'Purchasing',
    // Staffing
    staffing: 'Staffing',
    resourcePool: 'Resource Pool',
    staffingProjects: 'Projects',
    staffingContracts: 'Contracts',
    staffingTimesheet: 'Timesheets',
    staffingInvoices: 'Invoices',
    staffingAnalytics: 'Analytics',
    staffingEmail: 'Email',
    staffingEmailInbox: 'Inbox',
    staffingEmailTemplates: 'Templates',
    staffingEmailRules: 'Automation Rules',
    staffingAi: 'AI Assistant',
    staffingAiMatching: 'Matching',
    staffingAiMarket: 'Market Analysis',
    staffingAiAlerts: 'Alerts',
    // Portal
    portal: 'Portal',
    portalDashboard: 'Dashboard',
    portalTimesheet: 'Timesheet Entry',
    portalPayslip: 'Payslips',
    portalCertificates: 'Certificates',
    portalOrders: 'Orders',
    portalInvoices: 'Invoices',
    portalPayments: 'Payments'
  },
  login: {
    title: 'Login',
    companyCode: 'Company Code',
    employeeCode: 'Employee Code',
    password: 'Password',
    submit: 'Login',
    required: 'Please enter company code, employee code and password',
    failed: 'Login failed',
    invalid: 'Invalid company code, employee code or password'
  },
  chat: {
    aiTitle: 'AI Conversation',
    empty: 'Choose a page from the left or send the first message to get started.',
    placeholder: 'Chat with AI...',
    send: 'Send',
    selectScenario: 'Scenario',
    scenarioApplied: 'Scenario switched to {name}',
    scenarioCleared: 'Scenario cleared, using default mode',
    scenarioInactive: '(inactive)',
    taskListTitle: 'Tasks',
    completedTasksTitle: 'Completed Tasks',
    completedTasksCount: 'Completed Tasks ({count})',
    taskStatus: {
      pending: 'Pending',
      in_progress: 'In Progress',
      completed: 'Completed',
      failed: 'Failed',
      cancelled: 'Cancelled',
      approved: 'Approved',
      rejected: 'Rejected'
    },
    salesOrderTaskLabel: 'Sales Order',
    salesOrderNoLabel: 'Sales Order No.',
    salesOrderCustomerLabel: 'Customer',
    salesOrderOrderDateLabel: 'Order Date',
    salesOrderDeliveryDateLabel: 'Delivery Date',
    salesOrderAmountLabel: 'Order Amount',
    salesOrderShipToLabel: 'Ship To',
    salesOrderLinesLabel: 'Line Items',
    salesOrderLineNoLabel: 'Line',
    salesOrderLineItemLabel: 'Item',
    salesOrderLineQtyLabel: 'Quantity',
    salesOrderLineUnitPriceLabel: 'Unit Price',
    salesOrderLineAmountLabel: 'Amount',
    approvalTasksTitle: 'Approval Tasks',
    approvalApprove: 'Approve',
    approvalReject: 'Reject',
    approvalDownload: 'PDF Preview',
    approvalApproveSuccess: 'Request approved',
    approvalApproveFailed: 'Failed to approve',
    approvalRejectSuccess: 'Request rejected',
    approvalRejectFailed: 'Failed to reject',
    approvalDownloadFailed: 'Failed to download file',
    approvalEntityMap: {
      certificate_request: 'Certificate Approval'
    },
    approvalStepLabel: 'Approval Step',
    approvalApplicantLabel: 'Requester',
    approvalApplicantNameLabel: 'Applicant Name',
    approvalApplicantCodeLabel: 'Employee Code',
    approvalApplicantNoteLabel: 'Application Note',
    approvalApplicantResignReasonLabel: 'Resignation Reason',
    approvalCreatedAtLabel: 'Applied On',
    approvalUserLabel: 'User',
    approvalRemarkLabel: 'Remark',
    generalTimelineTitle: 'AI Conversation',
    generalModeButton: 'Free Chat',
    generalModeBanner: 'Free chat mode. Messages are not tied to any task.'
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
      openItem: 'Clearing Control',
      bankCash: 'Bank / Cash',
      fsBalanceGroup: 'BS Group',
      fsProfitGroup: 'PL Group',
      fsGroupSection: 'FS Group',
      detail: 'Details',
      detailDialog: 'Account Detail',
      deleteConfirm: 'Are you sure you want to delete this account?',
      deleteSuccess: 'Account deleted successfully',
      deleteFailed: 'Failed to delete account',
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
    assetRule: 'Fixed Asset Control',
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
      updatedBy: 'Updated By',
      paymentDate: 'Payment Date',
      note: 'Note',
      invoiceRegistrationNo: 'Invoice Registration No.'
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
      shortName: 'Short Name',
      paymentTerm: 'Payment Terms',
      invoiceNo: 'Invoice Number',
      customerVendor: 'Customer / Vendor',
      customerTag: 'Customer',
      vendorTag: 'Vendor',
      status: 'Status',
      contact: 'Contact Information',
      postalCode: 'Postal Code',
      address: 'Address',
      phone: 'Phone',
      fax: 'Fax',
      bankSection: 'Bank Account',
      bankSelect: 'Select Bank',
      branchSelect: 'Select Branch',
      accountType: 'Account Type',
      accountNo: 'Account Number',
      accountHolderKana: 'Account Holder (Hiragana)',
      currency: 'Currency'
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
      issueDate: 'Issue Date',
      actions: 'Actions',
      view: 'Details',
      detailTitle: 'Order Details',
      close: 'Close'
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
    workflowRules: {
      title: 'Workflow Rules',
      new: 'Add Rule',
      generatorTitle: 'Generate rule from natural language',
      generatorPlaceholder: 'Example: When a dining receipt is uploaded, automatically create a voucher using Meeting Expense / Cash',
      generatorTip: 'Describe the scenario in natural language (Japanese/Chinese/English)',
      generateButton: 'Generate with AI',
      generateSuccess: 'Draft generated by AI',
      generateFail: 'Failed to generate rule with AI',
      key: 'Rule Key',
      titleCol: 'Title',
      description: 'Description',
      instructions: 'Instructions',
      priority: 'Priority',
      active: 'Active',
      updated: 'Updated At',
      actions: 'Actions',
      actionsField: 'Execution actions (JSON)',
      actionsPlaceholder: '{\n  "type": "voucher.autoCreate",\n  "params": { ... }\n}',
      actionsInvalid: 'Actions must be a JSON array',
      keyRequired: 'Rule key is required',
      editorCreate: 'Create Rule',
      editorEdit: 'Edit Rule',
      test: 'Test',
      testFor: 'Test for',
      testPayload: 'Test payload (JSON)',
      testPayloadInvalid: 'Test payload must be valid JSON',
      runTest: 'Run Test',
      testResult: 'Result',
      deleteConfirm: 'Disable this rule?'
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
    },
    monthlyClosing: {
      title: 'Monthly Closing',
      detail: 'Details',
      start: 'Start Monthly Closing',
      startSuccess: 'Monthly closing started',
      startFailed: 'Failed to start monthly closing',
      loadFailed: 'Failed to load data',
      checkItems: 'Check Items',
      checkItem: 'Item',
      checkStatus: 'Status',
      checkMessage: 'Message',
      checkAction: 'Action',
      runChecks: 'Run All Checks',
      checkSuccess: 'Checks completed',
      checkFailed: 'Check failed',
      checkConfirmed: 'Confirmation saved',
      confirm: 'Confirm',
      manualCheck: 'Manual Confirmation',
      checkResult: 'Result',
      comment: 'Comment',
      taxSummary: 'Tax Summary',
      outputTax: 'Output Tax',
      inputTax: 'Input Tax',
      netTax: 'Net Tax',
      calcTax: 'Calculate Tax',
      taxCalcSuccess: 'Tax calculated',
      taxCalcFailed: 'Failed to calculate tax',
      submitApproval: 'Submit for Approval',
      submitSuccess: 'Submitted for approval',
      submitFailed: 'Failed to submit',
      close: 'Close Period',
      closeSuccess: 'Period closed',
      closeFailed: 'Failed to close period',
      closeConfirmTitle: 'Confirm',
      closeConfirmMessage: 'Close this period? No more entries can be added after closing.',
      closedMessage: 'Period Closed',
      reopen: 'Reopen',
      reopenReason: 'Reason',
      reopenReasonPlaceholder: 'Enter reason for reopening',
      confirmReopen: 'Confirm Reopen',
      reopenSuccess: 'Period reopened',
      reopenFailed: 'Failed to reopen',
      steps: {
        checking: 'Checking',
        adjusting: 'Adjusting',
        approval: 'Approval',
        closed: 'Closed'
      },
      statusOpen: 'Open',
      statusChecking: 'Checking',
      statusAdjusting: 'Adjusting',
      statusPendingApproval: 'Pending Approval',
      statusClosed: 'Closed',
      statusReopened: 'Reopened',
      statusPassed: 'Passed',
      statusWarning: 'Warning',
      statusFailed: 'Failed',
      statusInfo: 'Info',
      statusPending: 'Pending',
      statusSkipped: 'Skipped'
    },
    agentScenarios: {
      title: 'Agent Scenarios',
      new: 'New Scenario',
      key: 'Scenario Key',
      titleCol: 'Title',
      description: 'Description',
      instructions: 'Instructions',
      executionSection: 'Execution Hints',
      executionHint: 'Configure scenario-specific thresholds and system prompts. Leave fields empty to fall back to the default behaviour (20,000 JPY).',
      executionThreshold: 'Net amount threshold (JPY)',
      executionThresholdPlaceholder: 'Leave blank to use 20,000',
      executionLowMessage: 'System message when below threshold',
      executionHighMessage: 'System message when above threshold',
      executionMessagePlaceholder: 'Supports placeholders: {{netAmount}}, {{currency}}, {{threshold}}',
      executionTokensHint: 'Available placeholders: {{netAmount}}, {{currency}}, {{threshold}}',
      templateCardTitle: '📌 Key rules for dining invoices',
      templateCardIntro: 'This template automates the booking with the following rules.',
      templateCardItems: [
        'If the net amount is below JPY 20,000, post directly to Meeting Expenses without asking the user.',
        'If the net amount is JPY 20,000 or above, always confirm the number of diners and their names, then append “人数:n | 出席者:Name…” to the summary.',
        'Debits: Meeting Expenses plus Consumption Tax (when applicable); Credit: Cash for the total amount, ensuring the entry is balanced.',
        'Verify the invoice registration number and return the voucher number to the user after creation.'
      ],
      tools: 'Tool Hints',
      toolsPlaceholder: 'Type to add tool hints',
      generatorTitle: 'AI Scenario Generator',
      generatorPlaceholder: 'Describe the desired scenario in natural language (purpose, trigger, expected behaviour)…',
      generatorTip: 'The generated draft will open in the editor for review before saving.',
      generateButton: 'Generate Draft',
      generateSuccess: 'Draft generated successfully',
      generateFail: 'Failed to generate draft',
      quickIntro: 'Quick Start',
      quickStep1: '1. Fill in the basic information: scenario key, title and description.',
      quickStep2: '2. Configure the matching rules (message/file keywords, MIME types, etc.).',
      quickStep3: '3. (Optional) Add context messages or metadata, then save the scenario.',
      sectionBasic: 'Basic Information',
      sectionMatcher: 'Matching Rules',
      sectionAdvanced: 'Context & Advanced Settings',
      matcherHint: 'Define the keywords, MIME types and other rules that should trigger this scenario.',
      contextHint: 'Use this area to inject additional context or metadata when the scenario runs.',
      tagPlaceholder: 'Type and press Enter to add',
      modeLabel: 'Mode',
      modeSimple: 'Quick Setup',
      modeAdvanced: 'Advanced',
      simpleHint: 'Use keywords to match messages or files quickly. Switch to Advanced for fine-grained control.',
      simpleMessage: 'Message must contain',
      simpleContent: 'Content must contain',
      simpleFileTypes: 'Target file types (MIME)',
      advancedHiddenTip: 'This scenario already has advanced settings. Switch to Advanced mode to review or edit them.',
      matcher: 'Matcher',
      matcherBoth: 'Message and File',
      matcherMessage: 'Message',
      matcherFile: 'File',
      matcherMessageContains: 'Message must contain',
      matcherMessageExcludes: 'Message must not contain',
      matcherMessageRegex: 'Message regex',
      matcherFileNameContains: 'File name contains',
      matcherMimeTypes: 'MIME types',
      matcherContentContains: 'Content contains',
      matcherAlways: 'Always apply',
      matcherScope: 'Scope',
      matcherMatchAll: 'All keywords',
      priority: 'Priority',
      active: 'Active',
      updated: 'Updated At',
      actions: 'Actions',
      showAll: 'Show inactive',
      editorCreate: 'Create Scenario',
      editorEdit: 'Edit Scenario',
      metadata: 'Metadata (JSON)',
      metadataTip: 'Use this field to add additional metadata beyond the matcher/context configured above.',
      metadataPlaceholder: 'Add extra metadata in JSON format (optional)',
      metadataInvalid: 'Invalid metadata JSON',
      context: 'Runtime Context (JSON)',
      contextPlaceholder: 'Provide additional runtime context to inject when the scenario runs',
      contextInvalid: 'Invalid context JSON',
      contextMessagesLabel: 'Context Messages',
      contextAdd: 'Add message',
      contextRemove: 'Remove',
      keyRequired: 'Scenario key is required',
      titleRequired: 'Title is required',
      deleteConfirm: 'Delete this scenario?',
      test: 'Test',
      testTitle: 'Scenario Test',
      testScenario: 'Target scenario',
      testScenarioPlaceholder: '(Optional) Lock to a specific scenario key',
      testMessage: 'Message',
      testMessagePlaceholder: 'Simulate user input',
      testFileName: 'File Name',
      testFileNamePlaceholder: 'e.g. receipt.jpg',
      testContentType: 'Content Type',
      testPreview: 'Text Preview',
      testPreviewPlaceholder: 'Paste OCR or extracted text',
      testMatched: 'Matched Scenarios',
      testSystemPrompt: 'Generated system prompt',
      testContext: 'Injected Context Messages',
      contextMessages: 'Context Messages (JSON)',
      runTest: 'Run Test'
    },
    agentRules: {
      title: 'AI Accounting Rules',
      titleCol: 'Title',
      keywords: 'Keywords',
      account: 'Recommended Account',
      priority: 'Priority',
      active: 'Active',
      updated: 'Updated At',
      actions: 'Actions',
      showInactive: 'Show inactive rules',
      new: 'New Rule',
      formTitle: 'Title',
      formDescription: 'Description',
      formKeywords: 'Matching Keywords',
      formKeywordsPlaceholder: 'Type a keyword and press Enter',
      formAccountCode: 'Recommended Account Code',
      formAccountName: 'Recommended Account Name',
      formNote: 'Suggested Note',
      formOptions: 'Additional Options (JSON)',
      formOptionsPlaceholder: '{ "taxRate": 0.1 }',
      editorCreate: 'Create Rule',
      editorEdit: 'Edit Rule',
      createSuccess: 'Rule created successfully',
      updateSuccess: 'Rule updated successfully',
      deleteSuccess: 'Rule deleted successfully',
      deleteConfirm: 'Delete this rule?',
      titleRequired: 'Title is required',
      optionsInvalid: 'Options must be valid JSON'
    }
  },
  columns: {
    drcr: 'Dr/Cr',
    amount: 'Amount'
  },
  bankPayment: {
    title: 'Bank Payment Allocation',
    clearingAccount: 'Clearing Account',
    selectAccount: 'Select Account',
    partner: 'Partner',
    optional: 'Optional',
    bankAccount: 'Bank Account',
    bankPlaceholder: 'Bank/Cash Account',
    paymentAmount: 'Payment Amount',
    amount: 'Amount',
    paymentDate: 'Payment Date',
    date: 'Date',
    feeBearer: 'Fee Bearer',
    vendorBears: 'Vendor Bears',
    companyBears: 'Company Bears',
    feeAmount: 'Fee Amount',
    feeAccount: 'Fee Account',
    account: 'Account',
    bearer: 'Bearer',
    noOpenItems: 'No open items found',
    docDate: 'Doc Date',
    voucherNo: 'Voucher No',
    originalAmount: 'Original Amount',
    residualAmount: 'Residual Amount',
    applyAmount: 'Apply Amount',
    remark: 'Remark',
    clearingTotal: 'Clearing Total',
    actualPayment: 'Actual Payment',
    fee: 'Fee',
    mismatch: 'Mismatch',
    execute: 'Execute Payment',
    success: 'Payment completed',
    failed: 'Processing failed'
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
    categoryLarge: 'Category (L)',
    categorySmall: 'Category (S)',
    brand: 'Brand',
    model: 'Model',
    color: 'Color',
    janCode: 'JAN Code',
    eanCode: 'EAN Code',
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
  deliveryNotes: {
    deliveryNotes: 'Delivery Notes',
    deliveryNotesSub: 'Create delivery notes from sales orders',
    generateFromOrders: 'Generate from Sales Orders',
    generateDialogTitle: 'Generate Delivery Notes',
    filterCustomer: 'Customer',
    filterCustomerPlaceholder: 'Search customer code/name',
    filterOrder: 'Order No.',
    filterOrderPlaceholder: 'Search sales order number',
    deliveryDate: 'Delivery Date',
    deliveryDatePlaceholder: 'Select delivery date',
    salesOrderNo: 'Sales Order No.',
    customerCode: 'Customer Code',
    customerName: 'Customer Name',
    orderDate: 'Order Date',
    amountTotal: 'Order Amount',
    actions: 'Actions',
    generateSingle: 'Generate',
    generateBatch: 'Generate Selected',
    selectedCount: 'Selected: {count}',
    generateSuccess: 'Delivery notes generated'
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
      invoiceUnchecked: 'Please verify the invoice registration number.',
      voucherNoRequired: 'Please enter a voucher number.',
      voucherTypeRequired: 'Please select a voucher type.'
    },
    typeOptions: {
      GL: 'General Ledger',
      AP: 'Accounts Payable',
      AR: 'Accounts Receivable',
      AA: 'Assets',
      SA: 'Payroll',
      IN: 'Receipt',
      OT: 'Payment'
    },
    drLabel: 'Debit',
    crLabel: 'Credit'
  },
  buttons: {
    search: 'Search',
    reset: 'Reset',
    close: 'Close',
    refresh: 'Refresh',
    edit: 'Edit',
    save: 'Save',
    cancel: 'Cancel'
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
    backList: 'Back to List',
    edit: 'Edit',
    delete: 'Delete',
    deleted: 'Deleted',
    deleteFailed: 'Delete failed',
    cancel: 'Cancel',
    logout: 'Logout'
  },
  schemaEditor: {
    entity: 'Entity',
    saveNew: 'Save as New Version',
    saving: 'Saving...',
    saved: 'Saved'
  },
  financialCommon: {
    enabled: 'Enabled',
    disabled: 'Disabled',
    save: 'Save',
    saved: 'Saved',
    close: 'Close',
    delete: 'Delete',
    edit: 'Edit',
    actions: 'Actions',
    none: 'No data',
    confirmDelete: 'Are you sure you want to delete?'
  },
  financialNodes: {
    title: 'Statement Designer',
    description: 'Define statement groups and hierarchy for balance sheet and income statement.',
    add: 'Add Group',
    edit: 'Edit',
    delete: 'Delete',
    balanceSheet: 'Balance Sheet',
    incomeStatement: 'Income Statement',
    statement: 'Statement',
    code: 'Code',
    nameJa: 'Name (JP)',
    nameEn: 'Name (EN)',
    parent: 'Parent Group',
    parentPlaceholder: 'Select parent group',
    order: 'Sort Order',
    isSubtotal: 'Subtotal Row',
    note: 'Note',
    saveSuccess: 'Saved successfully',
    deleteSuccess: 'Deleted successfully',
    saveFailed: 'Save failed',
    deleteConfirm: 'Delete {name}?'
  },
  financialReports: {
    title: 'Financial Statements',
    statement: 'Statement',
    balanceSheet: 'Balance Sheet',
    incomeStatement: 'Income Statement',
    period: 'Period',
    periodRange: 'Period Range',
    periodRequired: 'Select period',
    currency: 'Currency',
    refreshBefore: 'Refresh materialized view before query',
    query: 'Run',
    exportPdf: 'Export PDF',
    exportExcel: 'Export Excel',
    noData: 'No data',
    name: 'Account / Group',
    amount: 'Amount',
    loadFailed: 'Failed to load'
  }
}

export const zh: Messages = JSON.parse(JSON.stringify(ja)) as Messages

zh.appTitle = 'iTBank Sfin - 简单财务'
zh.nav = {
  chat: '聊天会话',
  newSession: '新建会话',
  common: '通用菜单',
  vouchers: '会计凭证列表',
  voucherNew: '新建凭证',
  accounts: '科目列表',
  accountLedger: '科目明细账',
  accountBalance: '科目余额表',
  trialBalance: '合计残高试算表',
  ledgerExport: '帐簿导出',
  accountNew: '新增科目',
  bankReceipt: '银行收款',
  bankPayment: '银行出金配分',
  fbPayment: '自动支付',
  bankPlanner: '银行入金配分',
  moneytreeTransactions: '银行明细',
  financialReports: '财务报表',
  financialDesigner: '财务报表构成',
  consumptionTax: '消费税申报表',
  cashLedger: '现金出纳账',
  schemaEditor: '架构管理',
  approvalsDesigner: '审批规则',
  notifRuleRuns: '通知规则执行记录',
  notifLogs: '通知发送日志',
  schedulerTasks: '任务调度',
  partners: '往来单位列表',
  partnerNew: '新建往来单位',
  hrDept: '部门层级',
  hrOrg: '组织架构',
  hrEmps: '员工列表',
  hrEmpNew: '新建员工',
  employmentTypes: '雇佣类别',
  positionTypes: '职务类型',
  policyEditor: '薪酬策略',
  payrollExecute: '薪资计算',
  payrollHistory: '薪资历史',
  residentTax: '住民税',
  bonusCalc: '奖金计算',
  yearEndAdjustment: '年末调整',
  timesheets: '工时列表',
  timesheetNew: '录入工时',
  certRequest: '证明申请',
  certList: '申请记录',
  companySettings: '公司设置',
  userManagement: '用户管理',
  roleManagement: '角色管理',
  accountingPeriods: '会计期间',
  monthlyClosing: '月结',
  workflowRules: '工作流规则',
  agentScenarios: '智能代理场景',
  agentRules: 'AI 会计规则',
  agentSkills: 'Agent Skill設定',
  inventory: '库存管理',
  inventoryMaterials: '物料列表',
  inventoryMaterialNew: '新建物料',
  inventoryWarehouses: '仓库列表',
  inventoryWarehouseNew: '新建仓库',
  inventoryBins: '货位列表',
  inventoryBinNew: '新建货位',
  inventoryStatuses: '库存状态',
  inventoryBatches: '批次列表',
  inventoryBatchNew: '新建批次',
  inventoryMovement: '出入库/调拨',
  inventoryBalances: '库存余额',
  inventoryLedger: '出入库台账',
  inventoryCounts: '盘点',
  inventoryCountReport: '盘点差异报表',
  purchaseOrders: '采购订单列表',
  purchaseOrderNew: '新建采购订单',
  vendorInvoices: '供应商请求书',
  vendorInvoiceNew: '新建请求书',
  crm: 'CRM',
  crmContacts: '联系人列表',
  crmDeals: '商机列表',
  crmQuotes: '报价列表',
  crmSalesOrders: '订单列表',
  orderMgmt: '受注管理',
  crmDeliveryNotes: '纳品书列表',
  crmSalesInvoices: '请求书列表',
  crmSalesInvoiceCreate: '请求书创建',
  crmSalesAnalytics: '销售分析',
  crmSalesAlerts: '销售告警',
  crmActivities: '活动列表',
  crmContactNew: '新建联系人',
  crmDealNew: '新建商机',
  crmQuoteNew: '新建报价',
  crmSalesOrderNew: '新建订单',
  crmOrderEntry: '受注登记',
  crmActivityNew: '新建活动',
  recent: '最近访问',
  groupFinance: '财务会计',
  groupHR: '人事管理',
  groupInventory: '库存采购',
  groupOrders: '订单管理',
  groupCRM: 'CRM',
  groupSystem: '系统设置',
  groupFixedAssets: '固定资产',
  faClasses: '资产类别管理',
  faList: '固定资产',
  faDepreciation: '定期折旧记账',
  // 动态菜单翻译（后端 menu.xxx 对应）
  finance: '财务会计',
  hr: '人事管理',
  ai: 'AI',
  financialStatements: '财务报表',
  financialNodes: '报表设计器',
  fixedAssets: '固定资产',
  assetClasses: '资产类别管理',
  assetsList: '固定资产列表',
  depreciation: '定期折旧记账',
  cashFlow: '资金周转',
  expenseClaims: '经费报销一览',
  moneytree: '银行明细',
  employees: '员工列表',
  departments: '部门层级',
  payroll: '薪资管理',
  system: '系统设置',
  users: '用户管理',
  roles: '角色管理',
  notifications: '通知',
  notificationRuns: '通知规则执行历史',
  notificationLogs: '通知发送日志',
  contacts: '联系人列表',
  deals: '商机列表',
  quotes: '报价列表',
  salesOrders: '订单列表',
  activities: '活动列表',
  businessPartners: '业务伙伴',
  sales: '销售管理',
  salesAnalytics: '销售分析',
  salesAlerts: '销售预警',
  salesInvoices: '销售发票',
  deliveryNotes: '送货单',
  materials: '物料列表',
  warehouses: '仓库列表',
  bins: '库位列表',
  purchase: '采购管理',
  // Staffing (人才派遣)
  staffing: '人才派遣',
  resourcePool: '资源池',
  staffingProjects: '案件',
  staffingContracts: '合同',
  staffingTimesheet: '考勤',
  staffingInvoices: '账单',
  staffingAnalytics: '分析',
  staffingEmail: '邮件',
  staffingEmailInbox: '收件箱',
  staffingEmailTemplates: '模板',
  staffingEmailRules: '自动化规则',
  staffingAi: 'AI助手',
  staffingAiMatching: '智能匹配',
  staffingAiMarket: '市场分析',
  staffingAiAlerts: '预警',
  // Portal
  portal: '门户',
  portalDashboard: '仪表盘',
  portalTimesheet: '考勤录入',
  portalPayslip: '工资单',
  portalCertificates: '证明申请',
  portalOrders: '订单',
  portalInvoices: '账单',
  portalPayments: '付款'
}
zh.login = {
  title: '登录',
  companyCode: '公司代码',
  employeeCode: '员工编号',
  password: '密码',
  submit: '登录',
  required: '请输入公司代码、员工编号和密码',
  failed: '登录失败',
  invalid: '公司代码、员工编号或密码不正确'
}
zh.chat = {
  aiTitle: 'AI 聊天',
  empty: '请从左侧菜单打开页面，或发送第一条消息。',
  placeholder: '与 AI 对话...',
  send: '送信',
  selectScenario: '选择场景',
  scenarioApplied: '已切换到场景「{name}」',
  scenarioCleared: '已恢复默认场景配置',
  scenarioInactive: '（已停用）',
  taskListTitle: '我的任务',
  completedTasksTitle: '已完成任务',
  completedTasksCount: '已完成任务（{count}）',
  taskStatus: {
    pending: '待处理',
    in_progress: '处理中',
    completed: '已完成',
    failed: '失败',
    cancelled: '已取消',
    approved: '已完成',
    rejected: '已驳回'
  },
  salesOrderTaskLabel: '订单',
  salesOrderNoLabel: '订单编号',
  salesOrderCustomerLabel: '客户',
  salesOrderOrderDateLabel: '下单日期',
  salesOrderDeliveryDateLabel: '交货日期',
  salesOrderAmountLabel: '订单金额',
  salesOrderShipToLabel: '送货地址',
  salesOrderLinesLabel: '订单明细',
  salesOrderLineNoLabel: '行',
  salesOrderLineItemLabel: '品目',
  salesOrderLineQtyLabel: '数量',
  salesOrderLineUnitPriceLabel: '单价',
  salesOrderLineAmountLabel: '金额',
  approvalTasksTitle: '审批待办',
  approvalApprove: '同意',
  approvalReject: '驳回',
  approvalDownload: 'PDF预览',
  approvalApproveSuccess: '审批已同意',
  approvalApproveFailed: '审批失败',
  approvalRejectSuccess: '审批已驳回',
  approvalRejectFailed: '操作失败',
  approvalDownloadFailed: '下载失败',
  approvalEntityMap: {
    certificate_request: '证明申请审批'
  },
  approvalStepLabel: '审批步骤',
  approvalApplicantLabel: '申请人',
  approvalApplicantNameLabel: '申请人姓名',
  approvalApplicantCodeLabel: '员工编号',
  approvalApplicantNoteLabel: '申请备注',
  approvalApplicantResignReasonLabel: '退职理由',
  approvalCreatedAtLabel: '创建日期',
  approvalUserLabel: '用户',
  approvalRemarkLabel: '备注',
  generalTimelineTitle: 'AI 对话',
  generalModeButton: '自由对话',
  generalModeBanner: '当前为自由对话，消息不会关联到任务。'
}
zh.buttons = {
  search: '查询',
  reset: '重置',
  close: '关闭',
  refresh: '刷新',
  edit: '编辑',
  save: '保存',
  cancel: '取消'
}
zh.common = {
  enabled: '启用',
  disabled: '停用',
  view: '查看',
  save: '保存',
  saved: '已保存',
  saveFailed: '保存失败',
  close: '关闭',
  loadFailed: '加载失败',
  backList: '返回列表',
  edit: '编辑',
  delete: '删除',
  deleted: '已删除',
  deleteFailed: '删除失败',
  cancel: '取消',
  logout: '退出登录'
}
zh.bankPayment = {
  title: '银行出金配分',
  clearingAccount: '清账科目',
  selectAccount: '选择科目',
  partner: '往来单位',
  optional: '可选',
  bankAccount: '出金账户',
  bankPlaceholder: '银行/现金科目',
  paymentAmount: '出金金额',
  amount: '金额',
  paymentDate: '出金日期',
  date: '日期',
  feeBearer: '手续费承担',
  vendorBears: '对方承担',
  companyBears: '我方承担',
  feeAmount: '手续费金额',
  feeAccount: '手续费科目',
  account: '科目',
  bearer: '承担方',
  noOpenItems: '未找到未清项目',
  docDate: '单据日期',
  voucherNo: '凭证号',
  originalAmount: '原金额',
  residualAmount: '未清余额',
  applyAmount: '本次清账',
  remark: '摘要',
  clearingTotal: '清账金额',
  actualPayment: '实际出金',
  fee: '手续费',
  mismatch: '不匹配',
  execute: '执行出金',
  success: '出金完成',
  failed: '处理失败'
}
zh.dynamicForm = {
  addRow: '新增行',
  removeRow: '删除行',
  select: '选择',
  upload: '上传',
  actions: '操作',
  button: '按钮',
  value: '值'
}
zh.financialCommon = {
  enabled: '启用',
  disabled: '停用',
  save: '保存',
  saved: '已保存',
  close: '关闭',
  delete: '删除',
  edit: '编辑',
  actions: '操作',
  none: '无数据',
  confirmDelete: '确定删除？'
}
zh.schemaEditor = {
  entity: '实体',
  saveNew: '保存为新版本',
  saving: '保存中...',
  saved: '已保存'
}

zh.tables.agentScenarios = {
  title: '智能代理场景',
  new: '新增场景',
  key: '场景键',
  titleCol: '标题',
  description: '说明',
  instructions: '指引',
  executionSection: '执行提示',
  executionHint: '在此配置阈值和系统提示语，系统会在运行时注入给 Agent。不填写则沿用默认逻辑（20000 日元）。',
  executionThreshold: '净额阈值（日元）',
  executionThresholdPlaceholder: '留空使用 20000',
  executionLowMessage: '低于阈值时的系统提示',
  executionHighMessage: '高于阈值时的系统提示',
  executionMessagePlaceholder: '支持 {{netAmount}}、{{currency}}、{{threshold}} 占位符',
  executionTokensHint: '可用占位符：{{netAmount}}、{{currency}}、{{threshold}}',
  templateCardTitle: '📌 餐饮发票自动记账规则',
  templateCardIntro: '当前模板会按照以下规则自动生成会计凭证：',
  templateCardItems: [
    '净额低于 20000 日元时，直接记入“会议费”，无需再次向用户提问。',
    '净额大于或等于 20000 日元时，必须向用户确认就餐人数和姓名，并在摘要中追加“人数:n | 出席者:姓名…”信息。',
    '借方包含会议费和仮払消費税（视税额而定），贷方固定为现金，总额必须保持平衡。',
    '会校验发票登记号，并在记账成功后向用户返回凭证编号。'
  ],
  tools: '推荐工具',
  toolsPlaceholder: '输入后回车新增工具提示',
  generatorTitle: 'AI 场景生成',
  generatorPlaceholder: '用自然语言描述触发条件、要处理的动作、关键约束等',
  generatorTip: 'AI 会生成草案并打开编辑器，请在保存前进行确认和调整。',
  generateButton: '生成草案',
  generateSuccess: 'AI 已生成场景草案',
  generateFail: '场景生成失败',
  matcher: '匹配条件',
  matcherBoth: '消息与文件',
  matcherMessage: '消息',
  matcherFile: '文件',
  matcherMessageContains: '消息需包含',
  matcherMessageExcludes: '消息需排除',
  matcherMessageRegex: '消息正则',
  matcherFileNameContains: '文件名包含',
  matcherMimeTypes: 'MIME 类型',
  matcherContentContains: '内容包含',
  matcherAlways: '始终适用',
  matcherScope: '作用范围',
  matcherMatchAll: '匹配所有关键字',
  modeLabel: '编辑模式',
  modeSimple: '快速配置',
  modeAdvanced: '高级配置',
  simpleHint: '只需填写关键词即可快速完成场景配置，如需精细控制请切换到高级模式。',
  simpleMessage: '消息需包含的关键词',
  simpleContent: '内容需包含的关键词',
  simpleFileTypes: '目标文件类型（MIME）',
  advancedHiddenTip: '该场景已存在高级配置，如需调整请切换到"高级配置"模式。',
  sectionBasic: '基础信息',
  sectionMatcher: '匹配条件',
  sectionAdvanced: '上下文与高级设置',
  matcherHint: '指定关键字、MIME 类型等，让 AI 在接收到消息或文件时匹配到本场景。',
  contextHint: '可在此注入运行时需要的上下文或附加的 JSON 元数据。',
  tagPlaceholder: '输入后按回车添加',
  quickIntro: '快速上手',
  quickStep1: '1. 先填写场景键、标题、说明等基础信息。',
  quickStep2: '2. 配置消息/文件关键字、MIME 类型等触发条件。',
  quickStep3: '3. 如需附加上下文或 JSON 元数据，可在高级设置中补充，然后保存。',
  priority: '优先级',
  active: '启用',
  updated: '更新时间',
  actions: '操作',
  showAll: '显示已停用',
  editorCreate: '创建场景',
  editorEdit: '编辑场景',
  metadata: '附加元数据（JSON）',
  metadataTip: '除上述设置外，可在此追加自定义字段（JSON 格式）。',
  metadataPlaceholder: '在此填写额外的元数据 JSON，可留空',
  metadataInvalid: '元数据 JSON 格式无效',
  context: '运行时上下文（JSON）',
  contextPlaceholder: '需要注入到代理执行上下文的额外数据，JSON 格式，可留空',
  contextInvalid: '上下文 JSON 格式无效',
  contextMessagesLabel: '上下文消息',
  contextAdd: '新增消息',
  contextRemove: '删除',
  keyRequired: '场景键必填',
  titleRequired: '标题必填',
  deleteConfirm: '确定要删除该场景吗？',
  test: '测试',
  testTitle: '场景测试',
  testScenario: '目标场景',
  testScenarioPlaceholder: '（可选）指定要测试的场景键',
  testMessage: '消息',
  testMessagePlaceholder: '输入模拟的用户语句',
  testFileName: '文件名',
  testFileNamePlaceholder: '例如：receipt.jpg',
  testContentType: 'Content-Type',
  testPreview: '文本预览',
  testPreviewPlaceholder: '粘贴 OCR 或提取的文本',
  testMatched: '匹配到的场景',
  testSystemPrompt: '生成的系统提示词',
  testContext: '注入的上下文消息',
  contextMessages: '上下文消息（JSON）',
  runTest: '执行测试'
}

zh.tables.agentRules = {
  title: 'AI 会计规则',
  titleCol: '标题',
  keywords: '关键字',
  account: '推荐科目',
  priority: '优先级',
  active: '启用',
  updated: '更新时间',
  actions: '操作',
  showInactive: '显示已停用规则',
  new: '新增规则',
  formTitle: '标题',
  formDescription: '说明',
  formKeywords: '匹配关键字',
  formKeywordsPlaceholder: '输入后按回车添加关键字',
  formAccountCode: '推荐科目编码',
  formAccountName: '推荐科目名称',
  formNote: '建议备注',
  formOptions: '附加设置（JSON）',
  formOptionsPlaceholder: '{ "taxRate": 0.1 }',
  editorCreate: '新增规则',
  editorEdit: '编辑规则',
  createSuccess: '规则创建成功',
  updateSuccess: '规则更新成功',
  deleteSuccess: '规则删除成功',
  deleteConfirm: '确定删除该规则吗？',
  titleRequired: '标题必填',
  optionsInvalid: '附加设置 JSON 格式无效'
}

zh.tables.accounts = {
  title: '科目列表',
  new: '新增科目',
  code: '科目编码',
  name: '科目名称',
  bspl: 'BS/PL类别',
  tax: '税金类别',
  openItem: '清账管理',
  bankCash: '银行/现金',
  detail: '详情',
  detailDialog: '勘定科目详情',
  deleteConfirm: '确定要删除该会计科目吗？',
  deleteSuccess: '已成功删除科目',
  deleteFailed: '删除科目失败',
  open: '启用',
  close: '停用',
  bank: '银行',
  cash: '现金',
  none: '无',
  listEmpty: '暂无数据',
  save: '保存',
  saved: '已保存',
  failed: '保存失败',
  bankDialog: '选择银行',
  branchDialog: '选择支店',
  fsBalanceGroup: 'BS组',
  fsProfitGroup: 'PL组',
  fsGroupSection: '财务报表组',
  openItemBaseline: '清账基准',
  fieldRuleSection: '输入字段控制',
  bankCashSection: '银行 / 现金',
  customerRule: '客户输入控制',
  vendorRule: '供应商输入控制',
  employeeRule: '员工输入控制',
  departmentRule: '部门输入控制',
  paymentDateRule: '付款日期控制',
  assetRule: '资产输入控制',
  fieldRuleRequired: '必填',
  fieldRuleOptional: '选填',
  fieldRuleHidden: '隐藏',
  baselineNone: '无基准',
  baselineCustomer: '客户',
  baselineVendor: '供应商',
  baselineEmployee: '员工',
  taxOptionNonTax: '非课税',
  taxOptionInput: '进项税',
  taxOptionOutput: '销项税',
  taxOptionAccount: '税金科目',
  bankAccountFlag: '银行科目',
  cashAccountFlag: '现金科目',
  selectBankButton: '选择银行',
  selectBranchButton: '选择支店',
  bankName: '银行名称',
  branchName: '支店名称',
  accountType: '账户类型',
  accountTypeOrdinary: '普通',
  accountTypeChecking: '支票',
  accountNo: '账号',
  holder: '持有人',
  currency: '币种',
  currencyJpy: '日元',
  currencyUsd: '美元',
  currencyCny: '人民币',
  cashCurrency: '现金币种',
  taxMap: {
    NON_TAX: '非课税',
    INPUT_TAX: '进项税',
    OUTPUT_TAX: '销项税',
    TAX_ACCOUNT: '税金科目'
  },
  categoryMap: {
    BS: '资产负债表科目',
    PL: '损益表科目'
  }
}

zh.tables.salesOrders = {
  title: '销售订单列表',
  new: '新建订单',
  number: '订单号',
  customer: '客户代码',
  amount: '金额',
  status: '状态',
  issueDate: '下单日期',
  actions: '操作',
  view: '详情',
  detailTitle: '订单详情',
  close: '关闭'
}

zh.tables.monthlyClosing = {
  title: '月结',
  detail: '详情',
  start: '开始月结',
  startSuccess: '月结已开始',
  startFailed: '开始月结失败',
  loadFailed: '加载数据失败',
  checkItems: '检查项目',
  checkItem: '项目',
  checkStatus: '状态',
  checkMessage: '消息',
  checkAction: '操作',
  runChecks: '执行全部检查',
  checkSuccess: '检查完成',
  checkFailed: '检查失败',
  checkConfirmed: '确认已保存',
  confirm: '确认',
  manualCheck: '手动确认',
  checkResult: '确认结果',
  comment: '备注',
  taxSummary: '消费税汇总',
  outputTax: '销项税',
  inputTax: '进项税',
  netTax: '差额',
  calcTax: '计算消费税',
  taxCalcSuccess: '消费税计算完成',
  taxCalcFailed: '计算消费税失败',
  submitApproval: '提交审批',
  submitSuccess: '已提交审批',
  submitFailed: '提交失败',
  close: '确认月结',
  closeSuccess: '月结完成',
  closeFailed: '月结失败',
  closeConfirmTitle: '确认',
  closeConfirmMessage: '确定要关闭该月吗？关闭后将无法添加或修改凭证。',
  closedMessage: '已月结',
  reopen: '重新开放',
  reopenReason: '重开原因',
  reopenReasonPlaceholder: '请输入重新开放的原因',
  confirmReopen: '确认重开',
  reopenSuccess: '已重新开放',
  reopenFailed: '重开失败',
  steps: {
    checking: '检查',
    adjusting: '调整',
    approval: '审批',
    closed: '已结'
  },
  statusOpen: '未开始',
  statusChecking: '检查中',
  statusAdjusting: '调整中',
  statusPendingApproval: '待审批',
  statusClosed: '已结',
  statusReopened: '已重开',
  statusPassed: '通过',
  statusWarning: '警告',
  statusFailed: '失败',
  statusInfo: '信息',
  statusPending: '待确认',
  statusSkipped: '跳过'
}

export const messages: Record<Lang, Messages> = { ja, en, zh }


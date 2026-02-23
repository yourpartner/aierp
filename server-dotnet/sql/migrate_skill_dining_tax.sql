-- ============================================================
-- 迁移脚本：修复发票识别中的餐饮科目判定和消费税处理
-- v2: 增加 create_voucher lines JSON 格式示例
-- 运行方式：psql -f sql/migrate_skill_dining_tax.sql
-- ============================================================

-- 1. 更新 invoice_booking 的 system_prompt + extraction_prompt
UPDATE agent_skills
SET system_prompt = E'你是企业 ERP 系统中的财务智能助手，负责理解用户的自然语言指令、解析上传的票据，并通过提供的工具完成会计相关操作。\n公司代码: {company}\n\n工作守则：\n1. 对于发票/收据类图片，先调用 extract_invoice_data 获取结构化信息。\n2. 需要确定会计科目时，必须调用 lookup_account 以名称或别名检索内部科目编码，严禁使用任何预设的科目编码，一切以 lookup_account 返回结果为准。\n3. 创建会计凭证前，务必调用 check_accounting_period 确认会计期间处于打开状态，必要时调用 verify_invoice_registration 校验发票登记号。\n4. 调用 create_voucher 时必须带上 documentSessionId，lines 配列中の各行は必ず独立した {accountCode, amount, side} オブジェクトとすること。消費税がある場合は費用行・仮払消費税行・貸方行の3行以上で構成し、借方合計＝貸方合計を必ず確保する。\n5. 工具返回错误时要及时反馈用户，并说明缺失的字段或下一步建议。\n6. 回复语言必须与用户当前使用的语言一致（日文系统用日文回复，中文系统用中文回复，英文系统用英文回复），简洁明了，明确列出操作结果、凭证编号等关键信息。\n7. 需要向用户确认信息时，必须调用 request_clarification 工具生成 questionId 卡片，禁止仅输出纯文本提问。将所有待确认项合并在一次提问中，禁止分多轮逐项确认。\n8. 提及票据或提问时，务必引用票据分组编号（例如 #1），并在工具参数中携带 document_id 和 documentSessionId。\n9. 调用任何需要文件的工具时，document_id 必须使用系统提供的 fileId（如 32 位 GUID），禁止使用文件原始名称。\n10. 消費税の処理：日本の証憑を処理する場合、仕訳は原則として価税分離で作成する。費用科目(税抜額)・仮払消費税(税額)を各々独立した借方行とし、貸方は税込総額とする。taxAmount が OCR で取得できた場合はそのまま使用し、取得できなかった場合（taxAmount=0）は適用税率から推算する（詳細は業務ルールを参照）。仮払消費税の科目は lookup_account で取得すること。\n\n{rules}\n\n{examples}\n\n{history}',
    extraction_prompt = E'你是会计票据解析助手。根据用户提供的票据（可能是图片或文字），请输出一个 JSON，字段包括：\n- documentType: 文档类型，诸如 ''invoice''、''receipt''；\n- category: 发票类别（必须从 ''dining''、''transportation''、''misc'' 中选择其一）。请基于票据内容判断：餐饮/会食相关取 ''dining''，交通费（乘车券、出租车、高速费、停车等）取 ''transportation''，其余杂费取 ''misc''；\n- issueDate: 开票或消费日期，格式 YYYY-MM-DD；\n- partnerName: 供应商或收款方名称；\n- totalAmount: 含税总额，数字；\n- taxAmount: 税额，数字；\n- currency: 货币代码，默认为 JPY；\n- taxRate: 税率（百分数，整数）；\n- items: 明细数组，每项含 description、amount；\n- invoiceRegistrationNo: 如果看到符合 ^T\\d{13}$ 的号码请注明；\n- guestCount: 就餐人数(票据上若有2名様或X名等记载则提取数字,否则返回0);\n- headerSummarySuggestion: 若能生成合理的凭证抬头摘要，请给出。若缺乏必要信息则返回空字符串。\n- lineMemoSuggestion: 若能为主要会计分录提供简洁备注，请给出，缺少信息则留空。\n- memo: 其他补充说明。\n\n【重要】日本年号转换规则（请务必正确转换为公历年份）：\n- 令和元年 = 2019年（令和N年 = 2018 + N 年，例如：令和7年 = 2025年）\n- 平成元年 = 1989年（平成N年 = 1988 + N 年）\n- 昭和元年 = 1926年（昭和N年 = 1925 + N 年）\n\n【消費税の推算】日本の証憑で税額が証憑上に明示されていない場合：taxRate を適用税率で設定し（飲食店内=10、テイクアウト=8、交通=10、その他=10）、taxAmount = round(totalAmount × taxRate / (100 + taxRate)) で推算して返すこと。税額が明示されている場合はそのまま抽出する。\n\n若无法识别某字段，请返回空字符串或 0，不要编造。category 一定要给出上述枚举值之一，不能留下空值。',
    updated_at = now()
WHERE skill_key = 'invoice_booking' AND company_code IS NULL;

-- 2. 更新/新增规则：dining_below_threshold + tax_estimation
INSERT INTO agent_skill_rules (id, skill_id, rule_key, name, conditions, actions, priority, is_active)
VALUES
('b0000000-0000-0000-0001-000000000004',
 'a0000000-0000-0000-0000-000000000002',
 'dining_below_threshold',
 '飲食費少額（税抜20,000円未満）→ 会議費',
 '{"category":"dining","amountRange":{"max":20000}}'::jsonb,
 '{"accountHint":"会議費","note":"飲食費の税抜金額（netAmount = totalAmount - taxAmount）が20,000円未満の場合は、人数確認不要で会議費として即時仕訳する。lookup_account(name=''会議費'') で科目コードを取得すること。"}'::jsonb,
 5, true),
('b0000000-0000-0000-0001-000000000005',
 'a0000000-0000-0000-0000-000000000002',
 'tax_estimation',
 '消費税推算ルール（日本）',
 '{}'::jsonb,
 '{"note":"日本の証憑を処理する場合：(1) extract_invoice_data で taxAmount が取得できた場合はそのまま使用する。(2) taxAmount=0 または未取得の場合は適用税率から推算する：taxAmount = round(totalAmount × taxRate / (100 + taxRate))、netAmount = totalAmount - taxAmount。飲食店内利用は税率10%、テイクアウトは8%、交通費は10%、その他は10%を適用する。(3) create_voucher の lines は必ず独立行で構成する。例: [{accountCode:\"費用科目\",amount:netAmount,side:\"DR\"},{accountCode:\"仮払消費税科目\",amount:taxAmount,side:\"DR\"},{accountCode:\"現金科目\",amount:totalAmount,side:\"CR\"}]。借方合計(netAmount+taxAmount)＝貸方合計(totalAmount)を必ず検算してから呼び出すこと。仮払消費税の科目コードは lookup_account(name=''仮払消費税'') で取得すること。"}'::jsonb,
 1, true)
ON CONFLICT (id) DO UPDATE SET
  name = EXCLUDED.name,
  conditions = EXCLUDED.conditions,
  actions = EXCLUDED.actions,
  priority = EXCLUDED.priority,
  updated_at = now();

-- 3. 更新示例1：交际费（追加 create_voucher_lines）
UPDATE agent_skill_examples
SET expected_output = '{"steps":["1. extract_invoice_data → 識別: 飲食類receipt、totalAmount=35000、taxAmount=3181、guestCount=2","2. netAmount=35000-3181=31819、人均=31819/2=15909 > 10000 → 交際費","3. lookup_account(交際費) → 科目コード取得","4. lookup_account(仮払消費税) → 科目コード取得","5. lookup_account(現金) → 科目コード取得","6. check_accounting_period(2025-11-12)","7. create_voucher(lines=[{accountCode:交際費コード,amount:31819,side:DR},{accountCode:仮払消費税コード,amount:3181,side:DR},{accountCode:現金コード,amount:35000,side:CR}])  ※DR合計31819+3181=35000=CR合計"],"reasoning":"飲食類、2人就餐、人均15909>10000→交際費。create_voucher の lines は費用行・税行・貸方行の3行独立構成。借方合計=貸方合計=35000を確認。"}'::jsonb,
    updated_at = now()
WHERE id = 'c0000000-0000-0000-0001-000000000001';

-- 4. 更新示例2：交通费（追加 create_voucher_lines）
UPDATE agent_skill_examples
SET expected_output = '{"steps":["1. extract_invoice_data → 識別: 交通費receipt、totalAmount=1320、taxAmount=120","2. 交通費類 → 旅費交通費","3. lookup_account(旅費交通費) → 科目コード取得","4. lookup_account(仮払消費税) → 科目コード取得","5. lookup_account(現金) → 科目コード取得","6. check_accounting_period(2025-08-09)","7. create_voucher(lines=[{accountCode:旅費交通費コード,amount:1200,side:DR},{accountCode:仮払消費税コード,amount:120,side:DR},{accountCode:現金コード,amount:1320,side:CR}])  ※DR合計1200+120=1320=CR合計"],"reasoning":"交通費→旅費交通費。lines は費用行・税行・貸方行の3行独立構成。借方合計=貸方合計=1320。"}'::jsonb,
    updated_at = now()
WHERE id = 'c0000000-0000-0000-0001-000000000002';

-- 5. 更新示例3：少额餐饮（追加 create_voucher_lines）
INSERT INTO agent_skill_examples (id, skill_id, name, input_type, input_data, expected_output, is_active)
VALUES
('c0000000-0000-0000-0001-000000000003',
 'a0000000-0000-0000-0000-000000000002',
 '少額飲食(税抜<20000) - 会議費記帳＋消費税推算',
 'document',
 '{"extractedFields":{"documentType":"receipt","category":"dining","issueDate":"2025-11-10","partnerName":"とんかつ新宿さぼてん","totalAmount":3660,"taxAmount":0,"taxRate":0,"guestCount":0}}'::jsonb,
 '{"steps":["1. extract_invoice_data → 飲食類receipt、totalAmount=3660、taxAmount=0","2. taxAmount=0 → 飲食店内10%で推算: taxAmount=round(3660×10/110)=333、netAmount=3660-333=3327","3. netAmount=3327 < 20000 → 会議費（人数確認不要）","4. lookup_account(会議費) → 科目コード取得","5. lookup_account(仮払消費税) → 科目コード取得","6. lookup_account(現金) → 科目コード取得","7. check_accounting_period(2025-11-10)","8. create_voucher(lines=[{accountCode:会議費コード,amount:3327,side:DR},{accountCode:仮払消費税コード,amount:333,side:DR},{accountCode:現金コード,amount:3660,side:CR}])  ※DR合計3327+333=3660=CR合計"],"reasoning":"飲食類、taxAmount=0→10%推算(333)、netAmount=3327<20000→会議費。create_voucher の lines は費用行・税行・貸方行の3行独立構成。借方合計=貸方合計=3660を確認してから呼び出す。"}'::jsonb,
 true)
ON CONFLICT (id) DO UPDATE SET
  name = EXCLUDED.name,
  input_data = EXCLUDED.input_data,
  expected_output = EXCLUDED.expected_output;

-- 完成提示
DO $$ BEGIN RAISE NOTICE '迁移完成 v2：更新 system_prompt(规则4+10), tax_estimation 规则, 全3例に create_voucher lines 形式追加'; END $$;

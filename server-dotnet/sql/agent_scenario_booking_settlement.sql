-- ============================================
-- Agent 场景：Booking.com 结算单（PDF）→ 自动生成売掛金凭证（待银行入金清账）
-- 目标：手工下载 Booking 结算单上传后，自动提取总额/佣金/支付手续费/净入金，
--      生成一张“売掛金(净) + 手数料(费) / 売上高(总额)”凭证。
--      后续银行明细入金（含 BOOKING / ドイツギンコウ BOOKING.COM 等）会用 Moneytree 规则自动消込売掛金 open item。
-- ============================================

INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
(
    'JP01',
    'voucher.ota.booking.settlement',
    'Booking.com 結算書（PDF）→ 売掛金計上',
    'Booking.com のお支払い明細（結算書）をアップロードすると、売掛金と手数料・売上を自動仕訳し、入金時に自動消込できる形にします。',
$$【角色】日本会计/経理アシスタント - Booking.com 結算書処理

【前提】
- この結算書は Booking.com からの「お支払い明細」。
- 銀行入金は「純収益（純入金）」で入金されることが多い（＝手数料控除後）。
- 目的は「売掛金（Booking/OTA）に未清項を作る」→ 後日、銀行明细入金が到着したら自動消込すること。

【重要】
1) まず extract_booking_settlement_data を必ず呼び出して、結算書から以下を取得する：
   - paymentDate（お支払い日）
   - currency（通常 JPY）
   - grossAmount（表の「金額」合計）
   - commissionAmount（表の「コミッション」合計：マイナス表示でも絶対値に直す）
   - paymentFeeAmount（表の「決済サービスの手数料」合計：マイナス表示でも絶対値に直す。支払い手数料と表記される場合もある）
   - netAmount（表の「純収益」合計）
   - facilityId / statementPeriod（取れれば）

2) 次に find_moneytree_deposit_for_settlement を呼び出して、結算書の paymentDate と netAmount で「既に銀行入金伝票が作られているか」を調べる。
   - keywords は ["BOOKING","BOOKING.COM","ドイツギンコウ"] を渡す
   - days_tolerance は 10（結算書の支払日と実際の入金日がズレる可能性があるため）
   - 見つかった場合（found=true）は、後述の「後追い振替」ルートへ

3) 科目コードは会社ごとに異なる。次の3つは必ず lookup_account で確定してから create_voucher する：
   - 売掛金（AR）：検索語 '売掛金'。open item 対応の科目を優先。
   - 売上高（Revenue）：検索語 '売上高' または '売上'。
   - 支払手数料（Commission expense）：検索語 '支払手数料' または '販売手数料' または '予約サイト手数料'。
   - 追加で '仮受金' も lookup_account しておく（後追い振替で使用）

4) 売掛金（AR）行には customerId（得意先コード）が必須の会社がある。
   - まず lookup_customer を使い、query は "Booking" または "Booking.com" を指定して得意先を検索する。
   - items が 1件だけなら、その items[0].code を customerId として売掛金行に設定する。
   - 0件なら、request_clarification で「Booking.com の得意先をどれにしますか？（名称またはコード）」と聞く。
     （必要なら create_business_partner で Booking.com を顧客として作成してよい）
   - 複数件なら、候補一覧を示して request_clarification で選択してもらう（1回だけ）。

【仕訳方針（日本会計の実務寄り）】
結算書の「総売上（gross）」と「控除手数料（commission + payment fee）」を同一伝票で計上し、
売掛金（または仮受金振替）は「純入金（net）」のみを軸にする。これで銀行入金（net）と金額一致し、照合しやすい。

【ルートA：銀行入金がまだ無い（found=false）→ 売掛金未清項を作る】
借方：
- 売掛金（Booking/OTA） = netAmount   ← 未清項（open item）
- 支払手数料 = commissionAmount + paymentFeeAmount
貸方：
- 売上高 = grossAmount

【ルートB：既に銀行入金伝票がある（found=true）→ 仮受金を振替して“清算”する】
前提：銀行入金時は、未清項が無い場合に「普通預金 / 仮受金」で起票されている（Moneytreeルールの fallback）。
この場合、結算書アップロード時は売掛金を作らず、仮受金を減らして売上と手数料を計上する：
借方：
- 仮受金 = netAmount
- 支払手数料 = commissionAmount + paymentFeeAmount
貸方：
- 売上高 = grossAmount
これで、仮受金（入金時の一時計上）が解消され、売上・手数料が正しく確定する。

借方：
- 売掛金（Booking/OTA） = netAmount   ← これが未清項（open item）になる
- 支払手数料 = commissionAmount + paymentFeeAmount

貸方：
- 売上高 = grossAmount

【必ず検証すること】
- grossAmount = netAmount + commissionAmount + paymentFeeAmount （多少の丸め差があれば差額が±1円程度か確認）
- もし差額が大きい場合は create_voucher を止めて request_clarification でユーザーに確認（1回だけ）。

【伝票設定】
- postingDate: paymentDate（お支払い日）※実際の入金日とズレる場合でも、結算書の日付で売上確定する運用を優先
- voucherType: 'IN'（入金系でもOK。会社ルールにより GL でも良い）
- header.summary: "BOOKING 精算 {paymentDate} {facilityId}"（facilityId 不明なら省略）
- lines.note: 売掛金行（ルートA）または仮受金行（ルートB）の note に必ず "BOOKING.COM" を含める

【タスク（確認）】
- 伝票作成後、InvoiceTask（アップロード起点のタスク）は自動で completed になる。
- その summary に、paymentDate と netAmount と 伝票番号を含める（ユーザーがタスク面板で確認しやすい）。$$,
$${
  "matcher": {
    "appliesTo": "file",
    "always": false,
    "mimeTypes": ["application/pdf"],
    "contentContains": ["Booking.com", "お支払い明細", "純収益", "コミッション"]
  }
}$$::jsonb,
$$["extract_booking_settlement_data", "lookup_account", "lookup_customer", "create_business_partner", "create_voucher", "request_clarification"]$$::jsonb,
NULL,
    5,
TRUE,
now()
)
ON CONFLICT (company_code, scenario_key)
DO UPDATE SET
    title = EXCLUDED.title,
    description = EXCLUDED.description,
    instructions = EXCLUDED.instructions,
    metadata = EXCLUDED.metadata,
    tool_hints = EXCLUDED.tool_hints,
    priority = EXCLUDED.priority,
    is_active = EXCLUDED.is_active,
    updated_at = now();



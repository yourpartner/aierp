-- WEL01 payroll_sheet をオブジェクト形式からフラット配列形式に変換
-- 他社データへの影響なし (WHERE company_code='WEL01' AND jsonb_typeof(payroll_sheet)='object')
BEGIN;

UPDATE payroll_run_entries
SET
  -- payroll_sheet: earnings + deductions をフラット配列に変換
  payroll_sheet = (
    SELECT COALESCE(jsonb_agg(item ORDER BY sort_order), '[]'::jsonb)
    FROM (
      SELECT
        jsonb_build_object(
          'itemCode',        e->>'itemCode',
          'itemName',        e->>'itemName',
          'amount',          (e->>'amount')::numeric,
          'adjustment',      0,
          'isManuallyAdded', false,
          'adjustmentReason', '',
          'calculatedAmount',(e->>'amount')::numeric,
          'kind',            'earning'
        ) AS item,
        1 AS sort_order
      FROM jsonb_array_elements(payroll_sheet->'earnings') AS e
      WHERE (e->>'amount')::numeric != 0
        OR e->>'itemCode' = 'BaseSalary'  -- 基本給は0でも表示

      UNION ALL

      SELECT
        jsonb_build_object(
          'itemCode',        d->>'itemCode',
          'itemName',        d->>'itemName',
          'amount',          (d->>'amount')::numeric,
          'adjustment',      0,
          'isManuallyAdded', false,
          'adjustmentReason', '',
          'calculatedAmount',(d->>'amount')::numeric,
          'kind',            'deduction'
        ) AS item,
        2 AS sort_order
      FROM jsonb_array_elements(payroll_sheet->'deductions') AS d
      WHERE (d->>'amount')::numeric != 0  -- 0円控除は省略
    ) items
  ),
  -- metadata: 勤怠・期間情報などを保存
  metadata = COALESCE(metadata, '{}'::jsonb) || jsonb_strip_nulls(jsonb_build_object(
    'docNo',            payroll_sheet->>'docNo',
    'period',           payroll_sheet->>'period',
    'fromDate',         payroll_sheet->>'fromDate',
    'toDate',           payroll_sheet->>'toDate',
    'dueDate',          payroll_sheet->>'dueDate',
    'netPay',           (payroll_sheet->>'netPay')::numeric,
    'totalEarnings',    (payroll_sheet->>'totalEarnings')::numeric,
    'totalDeductions',  (payroll_sheet->>'totalDeductions')::numeric,
    'workRate',         (payroll_sheet->>'workRate')::numeric,
    'calendarWorkDays', (payroll_sheet->>'calendarWorkDays')::numeric,
    'actualWorkDays',   (payroll_sheet->>'actualWorkDays')::numeric,
    'actualMinutes',    (payroll_sheet->>'actualMinutes')::numeric,
    'otMinutes',        (payroll_sheet->>'otMinutes')::numeric,
    'hdMinutes',        (payroll_sheet->>'hdMinutes')::numeric,
    'rawSalaryItems',   payroll_sheet->'rawSalaryItems'
  ))
WHERE company_code = 'WEL01'
  AND jsonb_typeof(payroll_sheet) = 'object';

COMMIT;

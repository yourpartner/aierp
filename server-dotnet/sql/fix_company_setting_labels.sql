-- 修复 company_setting schema 的 UI 标签：中文 → 日语
-- 公司名称 → 会社名
-- 公司地址 → 会社住所
-- 上班(HH:mm) → 始業(HH:mm)
-- 下班(HH:mm) → 終業(HH:mm)
-- 午休(分钟) → 休憩(分)

UPDATE schemas
SET ui = (
  SELECT jsonb_set(
    ui,
    '{form,layout}',
    (
      SELECT jsonb_agg(
        CASE
          WHEN block ? 'cols' THEN
            jsonb_set(block, '{cols}', (
              SELECT jsonb_agg(
                CASE
                  WHEN col->>'label' = '公司名称' THEN jsonb_set(col, '{label}', '"会社名"')
                  WHEN col->>'label' = '公司地址' THEN jsonb_set(col, '{label}', '"会社住所"')
                  WHEN col->>'label' = '上班(HH:mm)' THEN jsonb_set(col, '{label}', '"始業(HH:mm)"')
                  WHEN col->>'label' = '下班(HH:mm)' THEN jsonb_set(col, '{label}', '"終業(HH:mm)"')
                  WHEN col->>'label' = '午休(分钟)' THEN jsonb_set(col, '{label}', '"休憩(分)"')
                  ELSE col
                END
              )
              FROM jsonb_array_elements(block->'cols') AS col
            ))
          ELSE block
        END
      )
      FROM jsonb_array_elements(ui->'form'->'layout') AS block
    )
  )
)
WHERE name = 'company_setting'
  AND ui IS NOT NULL
  AND ui->'form'->'layout' IS NOT NULL;

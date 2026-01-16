-- 更新入出库 schema 的 UI 配置，优化字段宽度和日语标签
UPDATE schemas
SET ui = '{
  "list": { "columns": ["movement_date","movement_type","reference_no"] },
  "form": {
    "layout": [
      { "type": "grid", "cols": [
        { "field": "movementType", "label": "移動タイプ", "span": 8, "widget": "select", "props": { "options": [
          { "label": "入庫 (IN)", "value": "IN" }, { "label": "出庫 (OUT)", "value": "OUT" }, { "label": "移動 (TRANSFER)", "value": "TRANSFER" }
        ] } },
        { "field": "movementDate", "label": "移動日", "span": 8, "widget": "date", "props": { "value-format":"YYYY-MM-DD" } },
        { "field": "referenceNo", "label": "参照番号", "span": 8 }
      ]},
      { "type": "grid", "cols": [
        { "field": "fromWarehouse", "label": "出庫倉庫", "span": 12, "widget":"select", "props": { "optionsUrl":"/inventory/warehouses", "optionValue":"warehouse_code", "optionLabel":"{name} ({warehouse_code})", "placeholder": "倉庫を選択" }, "visibleWhen": { "field": "movementType", "in": ["OUT","TRANSFER"] } },
        { "field": "fromBin", "label": "出庫棚番", "span": 12, "widget":"select", "props": { "optionsUrl":"/inventory/bins", "optionValue":"bin_code", "optionLabel":"{name} ({bin_code})", "filterBy": { "field":"warehouse_code", "equalsField":"fromWarehouse" }, "placeholder": "棚番を選択" }, "visibleWhen": { "field": "movementType", "in": ["OUT","TRANSFER"] } }
      ]},
      { "type": "grid", "cols": [
        { "field": "toWarehouse", "label": "入庫倉庫", "span": 12, "widget":"select", "props": { "optionsUrl":"/inventory/warehouses", "optionValue":"warehouse_code", "optionLabel":"{name} ({warehouse_code})", "placeholder": "倉庫を選択" }, "visibleWhen": { "field": "movementType", "in": ["IN","TRANSFER"] } },
        { "field": "toBin", "label": "入庫棚番", "span": 12, "widget":"select", "props": { "optionsUrl":"/inventory/bins", "optionValue":"bin_code", "optionLabel":"{name} ({bin_code})", "filterBy": { "field":"warehouse_code", "equalsField":"toWarehouse" }, "placeholder": "棚番を選択" }, "visibleWhen": { "field": "movementType", "in": ["IN","TRANSFER"] } }
      ]},
      { "type": "grid", "cols": [
        { "field": "lines", "label": "明細", "span": 24, "props": { "columns": [
          { "field": "lineNo", "label": "行", "inputType": "number", "width": 60 },
          { "field": "materialCode", "label": "品目", "width": 280, "widget":"select", "props": { "optionsUrl":"/inventory/materials", "optionValue":"material_code", "optionLabel":"{name} ({material_code})", "placeholder": "品目を選択" } },
          { "field": "quantity", "label": "数量", "inputType": "number", "width": 100 },
          { "field": "uom", "label": "単位", "width": 80 },
          { "field": "batchNo", "label": "ロット番号", "width": 120 },
          { "field": "statusCode", "label": "ステータス", "width": 100 }
        ] } }
      ]}
    ]
  }
}'::jsonb,
updated_at = now()
WHERE name = 'inventory_movement';

-- 显示更新结果
SELECT name, updated_at FROM schemas WHERE name = 'inventory_movement';


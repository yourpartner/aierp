-- 更新 inventory_movement 的 UI 配置
-- 1. 明细按钮显示"行を追加"文字
-- 2. 明细番号自动递增，不可编辑
-- 3. 单位和状态代码使用下拉列表

UPDATE schemas
SET ui = '{
  "list": { "columns": ["movement_date","movement_type","reference_no"] },
  "form": {
    "layout": [
      { "type": "grid", "cols": [
        { "field": "movementType", "label": "移動タイプ", "span": 8, "widget": "select", "props": { "options": [
          { "label": "入庫 (IN)", "value": "IN" }, 
          { "label": "出庫 (OUT)", "value": "OUT" }, 
          { "label": "移動 (TRANSFER)", "value": "TRANSFER" }
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
        { "field": "lines", "label": "明細", "span": 24, "props": { 
          "addRowText": "行を追加",
          "autoLineNo": true,
          "columns": [
            { "field": "lineNo", "label": "明細番号", "inputType": "number", "width": 80, "props": { "disabled": true } },
            { "field": "materialCode", "label": "品目コード", "width": 280, "widget":"select", "props": { "optionsUrl":"/inventory/materials", "optionValue":"material_code", "optionLabel":"{name} ({material_code})", "placeholder": "品目を選択" } },
            { "field": "quantity", "label": "数量", "inputType": "number", "width": 100, "props": { "min": 0.001, "precision": 3 } },
            { "field": "uom", "label": "単位", "width": 100, "widget": "select", "props": { "options": [
              { "label": "個", "value": "個" },
              { "label": "箱", "value": "箱" },
              { "label": "kg", "value": "kg" },
              { "label": "g", "value": "g" },
              { "label": "L", "value": "L" },
              { "label": "mL", "value": "mL" },
              { "label": "m", "value": "m" },
              { "label": "cm", "value": "cm" },
              { "label": "枚", "value": "枚" },
              { "label": "本", "value": "本" },
              { "label": "台", "value": "台" },
              { "label": "セット", "value": "セット" }
            ], "placeholder": "単位を選択" } },
            { "field": "batchNo", "label": "ロット番号", "width": 120 },
            { "field": "statusCode", "label": "ステータスコード", "width": 140, "widget": "select", "props": { "options": [
              { "label": "-", "value": "" },
              { "label": "良品 (GOOD)", "value": "GOOD" },
              { "label": "保留 (HOLD)", "value": "HOLD" },
              { "label": "破損 (DAMAGE)", "value": "DAMAGE" },
              { "label": "期限切れ (EXPIRED)", "value": "EXPIRED" },
              { "label": "検査中 (QC)", "value": "QC" }
            ], "placeholder": "ステータスを選択" } }
          ] 
        } }
      ]}
    ]
  }
}'::jsonb
WHERE name = 'inventory_movement';


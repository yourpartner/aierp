-- 固定资产模块测试数据 - 科目主数据
-- 在执行前请确认 company_code (这里使用 '1000' 作为示例)

-- 变量设置
DO $$
DECLARE
  v_company_code TEXT := '1000';
BEGIN

-- 资产科目（BS科目）
INSERT INTO accounts(company_code, payload) VALUES
  (v_company_code, '{"code": "211", "name": "建物", "category": "BS", "openItem": false}'),
  (v_company_code, '{"code": "216", "name": "工具器具備品", "category": "BS", "openItem": false}'),
  (v_company_code, '{"code": "265", "name": "ソフトウェア", "category": "BS", "openItem": false}'),
  (v_company_code, '{"code": "276", "name": "保証金", "category": "BS", "openItem": false}'),
  (v_company_code, '{"code": "279", "name": "長期前払", "category": "BS", "openItem": false}')
ON CONFLICT DO NOTHING;

-- 折旧费/除却科目（PL科目）
INSERT INTO accounts(company_code, payload) VALUES
  (v_company_code, '{"code": "845", "name": "減価償却", "category": "PL", "openItem": false}'),
  (v_company_code, '{"code": "869", "name": "雑費", "category": "PL", "openItem": false}'),
  (v_company_code, '{"code": "878", "name": "地代家賃", "category": "PL", "openItem": false}'),
  (v_company_code, '{"code": "924", "name": "雑損失", "category": "PL", "openItem": false}')
ON CONFLICT DO NOTHING;

-- 消费税科目（BS科目）
INSERT INTO accounts(company_code, payload) VALUES
  (v_company_code, '{"code": "189", "name": "仮払消費税", "category": "BS", "openItem": false, "taxType": "TAX_ACCOUNT"}')
ON CONFLICT DO NOTHING;

RAISE NOTICE '固定资产相关科目已创建';

END $$;

-- 创建示例资产类别
DO $$
DECLARE
  v_company_code TEXT := '1000';
BEGIN

-- 软件资产类别
INSERT INTO asset_classes(company_code, payload) VALUES
  (v_company_code, '{
    "className": "ソフトウェア",
    "isTangible": false,
    "acquisitionAccount": "265",
    "disposalAccount": "869",
    "depreciationExpenseAccount": "845",
    "accumulatedDepreciationAccount": "265",
    "includeTaxInDepreciation": true
  }')
ON CONFLICT DO NOTHING;

-- 保证金类别
INSERT INTO asset_classes(company_code, payload) VALUES
  (v_company_code, '{
    "className": "長期前払（保証金）",
    "isTangible": true,
    "acquisitionAccount": "276",
    "disposalAccount": "869",
    "depreciationExpenseAccount": "924",
    "accumulatedDepreciationAccount": "276",
    "includeTaxInDepreciation": false
  }')
ON CONFLICT DO NOTHING;

-- 办公室礼金类别
INSERT INTO asset_classes(company_code, payload) VALUES
  (v_company_code, '{
    "className": "長期前払（オフィス礼金）",
    "isTangible": true,
    "acquisitionAccount": "279",
    "disposalAccount": "869",
    "depreciationExpenseAccount": "878",
    "accumulatedDepreciationAccount": "279",
    "includeTaxInDepreciation": false
  }')
ON CONFLICT DO NOTHING;

-- 工具器具备品类别
INSERT INTO asset_classes(company_code, payload) VALUES
  (v_company_code, '{
    "className": "工具器具備品",
    "isTangible": true,
    "acquisitionAccount": "216",
    "disposalAccount": "869",
    "depreciationExpenseAccount": "845",
    "accumulatedDepreciationAccount": "216",
    "includeTaxInDepreciation": false
  }')
ON CONFLICT DO NOTHING;

-- 建物类别
INSERT INTO asset_classes(company_code, payload) VALUES
  (v_company_code, '{
    "className": "建物",
    "isTangible": true,
    "acquisitionAccount": "211",
    "disposalAccount": "924",
    "depreciationExpenseAccount": "845",
    "accumulatedDepreciationAccount": "211",
    "includeTaxInDepreciation": false
  }')
ON CONFLICT DO NOTHING;

RAISE NOTICE '固定资产类别已创建';

END $$;


-- 1. 在 permission_caps 表中注册住民税管理权限
INSERT INTO permission_caps (cap, name, description)
VALUES (
    'payroll:resident_tax', 
    '{"ja": "住民税管理", "en": "Resident Tax Management", "zh": "住民税管理"}', 
    '{"ja": "住民税データの管理とOCR読取", "en": "Management of resident tax data and OCR parsing", "zh": "住民税数据的管理与OCR识别"}'
)
ON CONFLICT (cap) DO UPDATE SET 
    name = EXCLUDED.name,
    description = EXCLUDED.description;

-- 2. 确保菜单项已正确关联该权限（之前已执行过，这里再次确认）
UPDATE permission_menus 
SET caps_required = ARRAY['payroll:resident_tax']
WHERE menu_key = 'hr.resident_tax';

-- 3. 检查并确认菜单显示顺序（确保在人事管理分组下）
UPDATE permission_menus
SET module_code = 'hr', display_order = 214
WHERE menu_key = 'hr.resident_tax';

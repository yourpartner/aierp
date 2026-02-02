-- 1. 在 permission_caps 表中注册住民税管理权限（使用正确的列名 cap_code, cap_name）
INSERT INTO permission_caps (cap_code, cap_name, module_code, description)
VALUES (
    'payroll:resident_tax', 
    '{"ja": "住民税管理", "en": "Resident Tax Management", "zh": "住民税管理"}', 
    'hr',
    '{"ja": "住民税数据的管理和OCR读取", "en": "Management of resident tax data and OCR parsing", "zh": "住民税数据的管理与OCR识别"}'
)
ON CONFLICT (cap_code) DO UPDATE SET 
    cap_name = EXCLUDED.cap_name,
    description = EXCLUDED.description;

-- 2. 确保 permission_caps 有 cap_code 的唯一约束（如果之前报错没有唯一约束的话）
-- ALTER TABLE permission_caps ADD CONSTRAINT permission_caps_cap_code_key UNIQUE (cap_code);

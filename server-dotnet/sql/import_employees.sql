-- 导入旧系统员工数据到新系统
-- 注意：需要根据实际旧数据调整

-- 先清空现有员工数据（如果有）
DELETE FROM employees WHERE company_code = 'JP01';

-- 定义一个临时函数来格式化日期
-- MySQL datetime 转 PostgreSQL date 格式

-- 员工1: 劉 峰 (ID=48)
INSERT INTO employees (company_code, payload) VALUES (
  'JP01',
  '{
    "code": "E0048",
    "nameKanji": "劉 峰",
    "nameKana": "リュウ ホウ",
    "gender": "M",
    "birthDate": "1970-03-12",
    "nationality": "CN",
    "arriveJPDate": null,
    "myNumber": null,
    "taxNo": null,
    "contact": {
      "phone": null,
      "email": null,
      "postalCode": null,
      "address": null
    },
    "insurance": {
      "hireInsuranceNo": null,
      "endowNo": null,
      "healthNo": null,
      "endowBaseNo": null,
      "joinDate": null,
      "quitDate": null
    },
    "contracts": [],
    "departments": [],
    "bankAccounts": [],
    "emergencies": [],
    "attachments": []
  }'::jsonb
);

-- 员工2: 李 樹
INSERT INTO employees (company_code, payload) VALUES (
  'JP01',
  '{
    "code": "E0049",
    "nameKanji": "李 樹",
    "nameKana": "リ シュ",
    "gender": "M",
    "birthDate": "1985-06-15",
    "nationality": "CN",
    "arriveJPDate": null,
    "myNumber": null,
    "taxNo": null,
    "contact": {
      "phone": null,
      "email": null,
      "postalCode": null,
      "address": null
    },
    "insurance": {
      "hireInsuranceNo": null,
      "endowNo": null,
      "healthNo": null,
      "endowBaseNo": null,
      "joinDate": null,
      "quitDate": null
    },
    "contracts": [],
    "departments": [],
    "bankAccounts": [],
    "emergencies": [],
    "attachments": []
  }'::jsonb
);

-- 请等待，我需要更多信息来完成完整的导入脚本
-- 运行以下查询可以查看导入结果
SELECT id, employee_code, payload->>'nameKanji' as name FROM employees WHERE company_code = 'JP01';


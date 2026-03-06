-- Add employee:mynumber capability for viewing My Number (マイナンバー)
INSERT INTO permission_caps (cap_code, cap_name, module_code, cap_type, is_sensitive, description, display_order)
VALUES (
  'employee:mynumber',
  '{"ja":"マイナンバー閲覧","zh":"查看个人编号","en":"View My Number"}',
  'hr',
  'action',
  true,
  '{"ja":"従業員のマイナンバーを閲覧・編集する権限（敏感）","zh":"查看和编辑员工个人编号的权限（敏感）","en":"Permission to view and edit employee My Number (sensitive)"}',
  15
)
ON CONFLICT (cap_code) DO NOTHING;

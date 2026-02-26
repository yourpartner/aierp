INSERT INTO permission_caps(cap_code, cap_name, module_code, cap_type, is_sensitive, description, display_order)
VALUES (
  'timesheet:proxy',
  '{"ja":"勤怠代理入力","zh":"代理录入工时","en":"Proxy Timesheet Entry"}',
  'hr',
  'action',
  false,
  '{"ja":"他の社員の勤怠を代理入力する権限","zh":"允许代替其他员工录入工时","en":"Permission to enter timesheets on behalf of other employees"}',
  12
) ON CONFLICT (cap_code) DO NOTHING;

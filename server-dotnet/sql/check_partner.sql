-- 检查イメガ的UUID
SELECT id, partner_code, name FROM businesspartners WHERE partner_code = 'BP000034';

-- 检查open_item中partner_id 64f02ccd-... 对应的取引先
SELECT id, partner_code, name FROM businesspartners WHERE id = '64f02ccd-6fdd-4c96-9d89-8e793a8707a2';


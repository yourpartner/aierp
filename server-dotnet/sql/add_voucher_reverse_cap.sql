-- Add voucher:reverse capability
INSERT INTO permission_caps (cap_code, cap_name, module_code, cap_type, is_sensitive, description, display_order) 
VALUES (
    'voucher:reverse', 
    '{"ja":"反対仕訳","zh":"冲销凭证","en":"Reverse Voucher"}', 
    'finance', 
    'action', 
    true, 
    '{"ja":"反対仕訳を作成する権限","zh":"允许创建冲销分录","en":"Permission to create reversal voucher"}', 
    5
) ON CONFLICT (cap_code) DO NOTHING;


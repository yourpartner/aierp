-- 企业微信AI客服相关表
-- 用于支持微信消息接收、客户关联、对话管理和AI学习

-- 1. 微信用户与ERP客户映射表
CREATE TABLE IF NOT EXISTS wecom_customer_mappings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code VARCHAR(20) NOT NULL,
    
    -- 企业微信用户标识
    wecom_user_id VARCHAR(100),        -- 企业微信内部成员 userid
    external_user_id VARCHAR(100),     -- 外部联系人 external_userid（客户）
    wecom_name VARCHAR(200),           -- 微信昵称/备注名
    wecom_avatar TEXT,                 -- 头像URL
    
    -- ERP客户关联
    partner_code VARCHAR(50),          -- 关联的取引先コード
    partner_name VARCHAR(200),         -- 取引先名
    partner_id UUID,                   -- 关联的 businesspartners 表ID
    
    -- 映射状态
    mapping_type VARCHAR(20) NOT NULL DEFAULT 'auto',  -- auto/manual/confirmed
    confidence NUMERIC(3,2) DEFAULT 0,  -- 自动匹配的置信度
    is_confirmed BOOLEAN DEFAULT FALSE, -- 是否已确认
    
    -- 元数据
    last_message_at TIMESTAMPTZ,       -- 最后消息时间
    message_count INTEGER DEFAULT 0,    -- 消息总数
    order_count INTEGER DEFAULT 0,      -- 通过微信下单次数
    
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    
    UNIQUE(company_code, external_user_id),
    UNIQUE(company_code, wecom_user_id)
);

-- 索引
CREATE INDEX IF NOT EXISTS idx_wecom_customer_mappings_partner ON wecom_customer_mappings(company_code, partner_code);
CREATE INDEX IF NOT EXISTS idx_wecom_customer_mappings_wecom_name ON wecom_customer_mappings(company_code, wecom_name);

-- 2. 企业微信对话会话表
CREATE TABLE IF NOT EXISTS wecom_chat_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code VARCHAR(20) NOT NULL,
    
    -- 会话参与者
    user_id VARCHAR(100) NOT NULL,      -- 用户标识（external_user_id 或 wecom_user_id）
    user_type VARCHAR(20) NOT NULL,     -- external（客户）/internal（员工）/group（群消息）
    chat_id VARCHAR(100),               -- 群聊ID（群消息时使用）
    
    -- 关联客户
    partner_code VARCHAR(50),
    partner_name VARCHAR(200),
    mapping_id UUID REFERENCES wecom_customer_mappings(id),
    
    -- 会话状态
    status VARCHAR(20) DEFAULT 'active', -- active/completed/cancelled
    intent VARCHAR(50),                  -- 识别出的主意图：order/inquiry/complaint/other
    
    -- 订单相关（如果是下单会话）
    pending_order_data JSONB,           -- 正在收集的订单信息
    sales_order_id UUID,                -- 创建的销售订单ID
    sales_order_no VARCHAR(50),         -- 销售订单号
    
    -- 统计
    message_count INTEGER DEFAULT 0,
    ai_response_count INTEGER DEFAULT 0,
    
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    completed_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_wecom_chat_sessions_user ON wecom_chat_sessions(company_code, user_id, status);
CREATE INDEX IF NOT EXISTS idx_wecom_chat_sessions_chat ON wecom_chat_sessions(company_code, chat_id);

-- 3. 对话消息历史表
CREATE TABLE IF NOT EXISTS wecom_chat_messages (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID REFERENCES wecom_chat_sessions(id),
    company_code VARCHAR(20) NOT NULL,
    
    -- 消息内容
    msg_id VARCHAR(100),                -- 企业微信消息ID
    msg_type VARCHAR(20) NOT NULL,      -- text/voice/image/event
    direction VARCHAR(10) NOT NULL,     -- in（收到）/out（发送）
    
    -- 发送者信息
    sender_id VARCHAR(100),             -- 发送者ID
    sender_name VARCHAR(200),           -- 发送者名称
    
    -- 消息内容
    content TEXT,                       -- 文本内容
    media_id VARCHAR(200),              -- 媒体文件ID（语音/图片）
    voice_text TEXT,                    -- 语音转文字结果
    
    -- 引用消息（群消息回复时使用）
    reply_to_msg_id VARCHAR(100),
    
    -- AI分析结果
    ai_analysis JSONB,                  -- AI分析结果：意图、提取的实体等
    
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_wecom_chat_messages_session ON wecom_chat_messages(session_id);
CREATE INDEX IF NOT EXISTS idx_wecom_chat_messages_msg ON wecom_chat_messages(msg_id);

-- 4. AI学习反馈表（用于自我进化）
CREATE TABLE IF NOT EXISTS wecom_ai_feedback (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code VARCHAR(20) NOT NULL,
    session_id UUID REFERENCES wecom_chat_sessions(id),
    message_id UUID REFERENCES wecom_chat_messages(id),
    
    -- 反馈类型
    feedback_type VARCHAR(20) NOT NULL, -- success/failure/correction/user_feedback
    
    -- 原始输入和AI输出
    user_input TEXT,
    ai_output TEXT,
    ai_intent VARCHAR(50),
    ai_entities JSONB,
    
    -- 修正后的结果
    corrected_intent VARCHAR(50),
    corrected_entities JSONB,
    corrected_by VARCHAR(100),
    
    -- 评分
    accuracy_score NUMERIC(3,2),        -- 准确度评分 0-1
    user_satisfaction INTEGER,          -- 用户满意度 1-5
    
    -- 备注
    note TEXT,
    
    -- 是否已用于训练
    used_for_training BOOLEAN DEFAULT FALSE,
    trained_at TIMESTAMPTZ,
    
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_wecom_ai_feedback_type ON wecom_ai_feedback(company_code, feedback_type, used_for_training);

-- 5. AI学习样本表（存储优质样本用于提示词优化）
CREATE TABLE IF NOT EXISTS wecom_ai_training_samples (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code VARCHAR(20) NOT NULL,
    
    -- 样本类型
    sample_type VARCHAR(30) NOT NULL,   -- order_complete/order_partial/inquiry/greeting
    
    -- 输入输出对
    input_text TEXT NOT NULL,           -- 用户输入
    expected_intent VARCHAR(50),        -- 期望识别的意图
    expected_entities JSONB,            -- 期望提取的实体
    expected_response TEXT,             -- 期望的回复
    
    -- 样本质量
    quality_score NUMERIC(3,2) DEFAULT 1.0,
    usage_count INTEGER DEFAULT 0,
    success_rate NUMERIC(3,2),
    
    -- 来源
    source VARCHAR(20),                 -- manual/auto_generated/user_confirmed
    source_session_id UUID,
    
    is_active BOOLEAN DEFAULT TRUE,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_wecom_ai_training_samples_type ON wecom_ai_training_samples(company_code, sample_type, is_active);

-- 6. 商品别名表（用于模糊匹配商品）
CREATE TABLE IF NOT EXISTS product_aliases (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code VARCHAR(20) NOT NULL,
    
    material_code VARCHAR(50) NOT NULL,  -- 品目コード
    material_name VARCHAR(200),          -- 正式品名
    
    alias VARCHAR(200) NOT NULL,         -- 别名/简称/俗称
    alias_type VARCHAR(20) DEFAULT 'common', -- common/customer_specific/abbreviation
    customer_code VARCHAR(50),           -- 特定客户使用的别名
    
    -- 匹配权重
    priority INTEGER DEFAULT 0,
    match_count INTEGER DEFAULT 0,       -- 成功匹配次数
    
    is_active BOOLEAN DEFAULT TRUE,
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_product_aliases_alias ON product_aliases(company_code, alias);
CREATE INDEX IF NOT EXISTS idx_product_aliases_material ON product_aliases(company_code, material_code);

-- 插入一些示例训练样本
INSERT INTO wecom_ai_training_samples (company_code, sample_type, input_text, expected_intent, expected_entities, expected_response, source)
VALUES
-- 完整订单
('JP01', 'order_complete', '老张家要100箱矿泉水，明天送', 'create_order', 
 '{"customer": "老张家", "items": [{"name": "矿泉水", "quantity": 100, "unit": "箱"}], "delivery_date": "明天"}',
 '好的，已为老张家创建订单：矿泉水100箱，明天送达。请确认订单信息是否正确？',
 'manual'),

-- 不完整订单（缺少数量）
('JP01', 'order_partial', '给便利店送点酱油', 'create_order',
 '{"customer": "便利店", "items": [{"name": "酱油"}]}',
 '好的，请问便利店需要多少酱油呢？',
 'manual'),

-- 不完整订单（缺少客户）
('JP01', 'order_partial', '50箱可乐，今天要', 'create_order',
 '{"items": [{"name": "可乐", "quantity": 50, "unit": "箱"}], "delivery_date": "今天"}',
 '请问这是给哪位客户的订单呢？',
 'manual'),

-- 询问类
('JP01', 'inquiry', '上次订单送到了吗？', 'inquiry_delivery',
 '{}',
 '让我查一下您最近的订单状态...',
 'manual'),

-- 问候
('JP01', 'greeting', '你好', 'greeting',
 '{}',
 '您好！我是AI客服小助手，可以帮您下单或查询订单。请问有什么可以帮您的？',
 'manual')
ON CONFLICT DO NOTHING;

-- 创建更新时间触发器
CREATE OR REPLACE FUNCTION update_wecom_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS wecom_customer_mappings_updated_at ON wecom_customer_mappings;
CREATE TRIGGER wecom_customer_mappings_updated_at
    BEFORE UPDATE ON wecom_customer_mappings
    FOR EACH ROW EXECUTE FUNCTION update_wecom_updated_at();

DROP TRIGGER IF EXISTS wecom_chat_sessions_updated_at ON wecom_chat_sessions;
CREATE TRIGGER wecom_chat_sessions_updated_at
    BEFORE UPDATE ON wecom_chat_sessions
    FOR EACH ROW EXECUTE FUNCTION update_wecom_updated_at();

DROP TRIGGER IF EXISTS wecom_ai_training_samples_updated_at ON wecom_ai_training_samples;
CREATE TRIGGER wecom_ai_training_samples_updated_at
    BEFORE UPDATE ON wecom_ai_training_samples
    FOR EACH ROW EXECUTE FUNCTION update_wecom_updated_at();

COMMENT ON TABLE wecom_customer_mappings IS '微信用户与ERP客户映射表';
COMMENT ON TABLE wecom_chat_sessions IS '企业微信AI客服对话会话表';
COMMENT ON TABLE wecom_chat_messages IS '对话消息历史表';
COMMENT ON TABLE wecom_ai_feedback IS 'AI学习反馈表，用于收集修正和反馈';
COMMENT ON TABLE wecom_ai_training_samples IS 'AI训练样本表，存储优质对话样本';
COMMENT ON TABLE product_aliases IS '商品别名表，用于模糊匹配';



-- 鑷嫊鏀墪 FB 銉曘偂銈ゃ儷绠＄悊
CREATE TABLE IF NOT EXISTS fb_payment_files (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  file_name TEXT NOT NULL,
  file_content TEXT,
  record_type TEXT NOT NULL DEFAULT '21',
  bank_code TEXT,
  bank_name TEXT,
  branch_code TEXT,
  branch_name TEXT,
  payment_date DATE NOT NULL,
  deposit_type TEXT,
  account_number TEXT,
  account_holder TEXT,
  total_count INTEGER NOT NULL DEFAULT 0,
  total_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  line_items JSONB,
  voucher_ids JSONB,
  status TEXT NOT NULL DEFAULT 'created',
  created_by TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_fb_payment_files_company ON fb_payment_files(company_code, payment_date DESC);
CREATE INDEX IF NOT EXISTS idx_fb_payment_files_status ON fb_payment_files(company_code, status);


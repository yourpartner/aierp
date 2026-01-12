-- ============================================================
-- Staffing (人才派遣) payload tables migration
-- Goal: align staffing entities with schema-driven /objects CRUD (payload-as-source-of-truth)
-- This migration is SAFE for Standard edition: it only creates new tables with stf_* prefix.
-- ============================================================

CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- Helper: jsonb -> numeric (safe)
CREATE OR REPLACE FUNCTION fn_jsonb_numeric(p jsonb, key text)
RETURNS numeric
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN (p ->> key) ~ '^[-+]?[0-9]+(\.[0-9]+)?$'
      THEN (p ->> key)::numeric
    ELSE NULL
  END;
$$;

-- Helper: jsonb -> date (YYYY-MM-DD only; safe)
CREATE OR REPLACE FUNCTION fn_jsonb_date(p jsonb, key text)
RETURNS date
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN (p ->> key) ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
      THEN (p ->> key)::date
    ELSE NULL
  END;
$$;

-- Helper: jsonb -> uuid (safe)
CREATE OR REPLACE FUNCTION fn_jsonb_uuid(p jsonb, key text)
RETURNS uuid
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN (p ->> key) ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
      THEN (p ->> key)::uuid
    ELSE NULL
  END;
$$;

-- Helper: jsonb -> boolean (safe)
CREATE OR REPLACE FUNCTION fn_jsonb_bool(p jsonb, key text)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN lower(COALESCE(p ->> key, '')) IN ('true','1','yes','y') THEN true
    WHEN lower(COALESCE(p ->> key, '')) IN ('false','0','no','n') THEN false
    ELSE NULL
  END;
$$;

-- Helper: jsonb -> timestamptz (ISO 8601 only; safe)
CREATE OR REPLACE FUNCTION fn_jsonb_timestamptz(p jsonb, key text)
RETURNS timestamptz
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN (p ->> key) ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T'
      THEN (p ->> key)::timestamptz
    ELSE NULL
  END;
$$;

-- ============================================================
-- Sequences (shared with staffing modules; safe if already exists)
-- ============================================================
CREATE SEQUENCE IF NOT EXISTS seq_resource_code START 1;
CREATE SEQUENCE IF NOT EXISTS seq_project_code START 1;
CREATE SEQUENCE IF NOT EXISTS seq_contract_code START 1;
CREATE SEQUENCE IF NOT EXISTS seq_staffing_invoice START 1;
CREATE SEQUENCE IF NOT EXISTS seq_purchase_order START 1;
CREATE SEQUENCE IF NOT EXISTS seq_freelancer_invoice START 1;

-- ============================================================
-- Core staffing entities (payload tables + generated columns for indexing/joins)
-- ============================================================

-- Resource Pool
CREATE TABLE IF NOT EXISTS stf_resources (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  resource_code TEXT GENERATED ALWAYS AS (payload->>'resource_code') STORED,
  display_name TEXT GENERATED ALWAYS AS (payload->>'display_name') STORED,
  resource_type TEXT GENERATED ALWAYS AS (payload->>'resource_type') STORED,
  availability_status TEXT GENERATED ALWAYS AS (payload->>'availability_status') STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED,
  employee_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'employee_id')) STORED,
  supplier_partner_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'supplier_partner_id')) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_stf_resources_company_code ON stf_resources(company_code, resource_code);
CREATE INDEX IF NOT EXISTS idx_stf_resources_company_type ON stf_resources(company_code, resource_type);
CREATE INDEX IF NOT EXISTS idx_stf_resources_company_avail ON stf_resources(company_code, availability_status);
CREATE INDEX IF NOT EXISTS idx_stf_resources_company_name ON stf_resources(company_code, display_name);

-- Projects
CREATE TABLE IF NOT EXISTS stf_projects (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  project_code TEXT GENERATED ALWAYS AS (payload->>'project_code') STORED,
  project_name TEXT GENERATED ALWAYS AS (payload->>'project_name') STORED,
  client_partner_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'client_partner_id')) STORED,
  contract_type TEXT GENERATED ALWAYS AS (payload->>'contract_type') STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_stf_projects_company_code ON stf_projects(company_code, project_code);
CREATE INDEX IF NOT EXISTS idx_stf_projects_company_status ON stf_projects(company_code, status);
CREATE INDEX IF NOT EXISTS idx_stf_projects_company_client ON stf_projects(company_code, client_partner_id);

-- Project candidates
CREATE TABLE IF NOT EXISTS stf_project_candidates (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  project_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'project_id')) STORED,
  resource_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'resource_id')) STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED,
  proposed_rate NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'proposed_rate')) STORED,
  final_rate NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'final_rate')) STORED,
  recommended_at TIMESTAMPTZ GENERATED ALWAYS AS (fn_jsonb_timestamptz(payload, 'recommended_at')) STORED
);
CREATE INDEX IF NOT EXISTS idx_stf_candidates_company_project ON stf_project_candidates(company_code, project_id);
CREATE INDEX IF NOT EXISTS idx_stf_candidates_company_resource ON stf_project_candidates(company_code, resource_id);

-- Contracts
CREATE TABLE IF NOT EXISTS stf_contracts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  contract_no TEXT GENERATED ALWAYS AS (payload->>'contract_no') STORED,
  project_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'project_id')) STORED,
  resource_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'resource_id')) STORED,
  client_partner_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'client_partner_id')) STORED,
  contract_type TEXT GENERATED ALWAYS AS (payload->>'contract_type') STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED,
  start_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'start_date')) STORED,
  end_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'end_date')) STORED,
  billing_rate NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'billing_rate')) STORED,
  cost_rate NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'cost_rate')) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_stf_contracts_company_no ON stf_contracts(company_code, contract_no);
CREATE INDEX IF NOT EXISTS idx_stf_contracts_company_status ON stf_contracts(company_code, status);
CREATE INDEX IF NOT EXISTS idx_stf_contracts_company_resource ON stf_contracts(company_code, resource_id);
CREATE INDEX IF NOT EXISTS idx_stf_contracts_company_client ON stf_contracts(company_code, client_partner_id);
CREATE INDEX IF NOT EXISTS idx_stf_contracts_company_dates ON stf_contracts(company_code, start_date, end_date);

-- Timesheet summaries (month)
CREATE TABLE IF NOT EXISTS stf_timesheet_summaries (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  contract_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'contract_id')) STORED,
  resource_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'resource_id')) STORED,
  year_month TEXT GENERATED ALWAYS AS (payload->>'year_month') STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED,
  scheduled_hours NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'scheduled_hours')) STORED,
  actual_hours NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'actual_hours')) STORED,
  overtime_hours NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'overtime_hours')) STORED,
  total_billing_amount NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'total_billing_amount')) STORED,
  total_cost_amount NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'total_cost_amount')) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_stf_timesheets_company_contract_month ON stf_timesheet_summaries(company_code, contract_id, year_month);
CREATE INDEX IF NOT EXISTS idx_stf_timesheets_company_month ON stf_timesheet_summaries(company_code, year_month);
CREATE INDEX IF NOT EXISTS idx_stf_timesheets_company_status ON stf_timesheet_summaries(company_code, status);

-- Invoices
CREATE TABLE IF NOT EXISTS stf_invoices (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  invoice_no TEXT GENERATED ALWAYS AS (payload->>'invoice_no') STORED,
  client_partner_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'client_partner_id')) STORED,
  billing_year_month TEXT GENERATED ALWAYS AS (payload->>'billing_year_month') STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED,
  total_amount NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'total_amount')) STORED,
  paid_amount NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'paid_amount')) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_stf_invoices_company_no ON stf_invoices(company_code, invoice_no);
CREATE INDEX IF NOT EXISTS idx_stf_invoices_company_client ON stf_invoices(company_code, client_partner_id);
CREATE INDEX IF NOT EXISTS idx_stf_invoices_company_period ON stf_invoices(company_code, billing_year_month);
CREATE INDEX IF NOT EXISTS idx_stf_invoices_company_status ON stf_invoices(company_code, status);

-- Invoice lines
CREATE TABLE IF NOT EXISTS stf_invoice_lines (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  invoice_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'invoice_id')) STORED,
  contract_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'contract_id')) STORED,
  resource_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'resource_id')) STORED,
  timesheet_summary_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload, 'timesheet_summary_id')) STORED,
  line_no INTEGER GENERATED ALWAYS AS (COALESCE((fn_jsonb_numeric(payload,'line_no'))::int, 0)) STORED
);
CREATE INDEX IF NOT EXISTS idx_stf_invoice_lines_company_invoice ON stf_invoice_lines(company_code, invoice_id);
CREATE INDEX IF NOT EXISTS idx_stf_invoice_lines_company_contract ON stf_invoice_lines(company_code, contract_id);
CREATE INDEX IF NOT EXISTS idx_stf_invoice_lines_company_timesheet ON stf_invoice_lines(company_code, timesheet_summary_id);

-- ============================================================
-- Email / Portal entities (payload tables; used by staffing modules)
-- ============================================================
CREATE TABLE IF NOT EXISTS stf_email_accounts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  account_name TEXT GENERATED ALWAYS AS (payload->>'account_name') STORED,
  email_address TEXT GENERATED ALWAYS AS (payload->>'email_address') STORED,
  account_type TEXT GENERATED ALWAYS AS (payload->>'account_type') STORED,
  is_default BOOLEAN GENERATED ALWAYS AS (COALESCE(fn_jsonb_bool(payload,'is_default'), false)) STORED,
  is_active BOOLEAN GENERATED ALWAYS AS (COALESCE(fn_jsonb_bool(payload,'is_active'), true)) STORED
);
CREATE INDEX IF NOT EXISTS idx_stf_email_accounts_company ON stf_email_accounts(company_code);

CREATE TABLE IF NOT EXISTS stf_email_templates (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  template_code TEXT GENERATED ALWAYS AS (payload->>'template_code') STORED,
  template_name TEXT GENERATED ALWAYS AS (payload->>'template_name') STORED,
  category TEXT GENERATED ALWAYS AS (payload->>'category') STORED,
  is_active BOOLEAN GENERATED ALWAYS AS (COALESCE(fn_jsonb_bool(payload,'is_active'), true)) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_stf_email_templates_company_code ON stf_email_templates(company_code, template_code);

CREATE TABLE IF NOT EXISTS stf_email_messages (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  folder TEXT GENERATED ALWAYS AS (payload->>'folder') STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED,
  received_at TIMESTAMPTZ GENERATED ALWAYS AS (fn_jsonb_timestamptz(payload,'received_at')) STORED
);
CREATE INDEX IF NOT EXISTS idx_stf_email_messages_company_folder ON stf_email_messages(company_code, folder);
CREATE INDEX IF NOT EXISTS idx_stf_email_messages_company_status ON stf_email_messages(company_code, status);

CREATE TABLE IF NOT EXISTS stf_email_queue (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED,
  scheduled_at TIMESTAMPTZ GENERATED ALWAYS AS (fn_jsonb_timestamptz(payload,'scheduled_at')) STORED
);
CREATE INDEX IF NOT EXISTS idx_stf_email_queue_company_status ON stf_email_queue(company_code, status);

CREATE TABLE IF NOT EXISTS stf_email_rules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  trigger_type TEXT GENERATED ALWAYS AS (payload->>'trigger_type') STORED,
  is_active BOOLEAN GENERATED ALWAYS AS (COALESCE(fn_jsonb_bool(payload,'is_active'), true)) STORED
);
CREATE INDEX IF NOT EXISTS idx_stf_email_rules_company_trigger ON stf_email_rules(company_code, trigger_type, is_active);

CREATE TABLE IF NOT EXISTS stf_purchase_orders (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  order_no TEXT GENERATED ALWAYS AS (payload->>'order_no') STORED,
  resource_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload,'resource_id')) STORED,
  contract_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload,'contract_id')) STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_stf_po_company_no ON stf_purchase_orders(company_code, order_no);
CREATE INDEX IF NOT EXISTS idx_stf_po_company_status ON stf_purchase_orders(company_code, status);

CREATE TABLE IF NOT EXISTS stf_freelancer_invoices (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  invoice_no TEXT GENERATED ALWAYS AS (payload->>'invoice_no') STORED,
  resource_id UUID GENERATED ALWAYS AS (fn_jsonb_uuid(payload,'resource_id')) STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED,
  total_amount NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload,'total_amount')) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_stf_fi_company_no ON stf_freelancer_invoices(company_code, invoice_no);
CREATE INDEX IF NOT EXISTS idx_stf_fi_company_status ON stf_freelancer_invoices(company_code, status);

-- ============================================================
-- Minimal schema seeds (global, idempotent). Required so /objects/{entity} works.
-- ============================================================

-- resource
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'resource',
  1,
  TRUE,
  '{
    "type":"object",
    "required":["resource_code","display_name","resource_type"],
    "properties":{
      "resource_code":{"type":"string","maxLength":20},
      "display_name":{"type":"string","maxLength":100},
      "display_name_kana":{"type":["string","null"],"maxLength":100},
      "resource_type":{"type":"string","enum":["employee","freelancer","bp","candidate"]},
      "employee_id":{"type":["string","null"],"format":"uuid"},
      "supplier_partner_id":{"type":["string","null"],"format":"uuid"},
      "email":{"type":["string","null"],"format":"email"},
      "phone":{"type":["string","null"]},
      "skills":{"type":"array","items":{"type":"string"}},
      "experience_years":{"type":["integer","null"],"minimum":0},
      "default_billing_rate":{"type":["number","null"]},
      "default_cost_rate":{"type":["number","null"]},
      "rate_type":{"type":["string","null"],"enum":["hourly","daily","monthly"]},
      "availability_status":{"type":["string","null"],"enum":["available","assigned","ending_soon","unavailable"],"default":"available"},
      "available_from":{"type":["string","null"],"format":"date"},
      "status":{"type":["string","null"],"enum":["active","archived"],"default":"active"}
    }
  }'::jsonb,
  '{"title":"Resource","listColumns":["resource_code","display_name","resource_type","availability_status","updated_at"]}'::jsonb,
  '{"allowedFilters":["resource_type","availability_status","display_name","resource_code"],"defaultSort":"-updated_at"}'::jsonb,
  '["resource_code","display_name","resource_type","availability_status"]'::jsonb,
  NULL,
  '{"prefix":"RS-","sequence":"seq_resource_code","padding":4,"field":"resource_code"}'::jsonb,
  '{"description":"Staffing resource pool"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- staffing_project
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'staffing_project',
  1,
  TRUE,
  '{
    "type":"object",
    "required":["project_code","project_name","client_partner_id","contract_type"],
    "properties":{
      "project_code":{"type":"string","maxLength":30},
      "project_name":{"type":"string","maxLength":200},
      "client_partner_id":{"type":"string","format":"uuid"},
      "contract_type":{"type":"string","enum":["dispatch","ses","contract"]},
      "required_skills":{"type":"array","items":{"type":"string"}},
      "headcount":{"type":["integer","null"],"minimum":1,"default":1},
      "status":{"type":["string","null"],"enum":["draft","open","matching","filled","closed","cancelled"],"default":"open"}
    }
  }'::jsonb,
  '{"title":"Project","listColumns":["project_code","project_name","contract_type","status","updated_at"]}'::jsonb,
  '{"allowedFilters":["status","contract_type","client_partner_id"],"defaultSort":"-updated_at"}'::jsonb,
  '["project_code","project_name","client_partner_id","contract_type","status"]'::jsonb,
  NULL,
  '{"prefix":"PJ-","sequence":"seq_project_code","padding":4,"field":"project_code"}'::jsonb,
  '{"description":"Staffing project"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- staffing_project_candidate
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'staffing_project_candidate',
  1,
  TRUE,
  '{
    "type":"object",
    "required":["project_id","resource_id"],
    "properties":{
      "project_id":{"type":"string","format":"uuid"},
      "resource_id":{"type":"string","format":"uuid"},
      "status":{"type":["string","null"]},
      "proposed_rate":{"type":["number","null"]},
      "final_rate":{"type":["number","null"]},
      "recommended_at":{"type":["string","null"],"format":"date-time"},
      "notes":{"type":["string","null"]}
    }
  }'::jsonb,
  '{"title":"Candidate","listColumns":["project_id","resource_id","status","recommended_at"]}'::jsonb,
  '{"allowedFilters":["project_id","resource_id","status"],"defaultSort":"-recommended_at"}'::jsonb,
  '["project_id","resource_id","status"]'::jsonb,
  NULL,
  NULL,
  '{"description":"Project candidate"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- staffing_contract
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'staffing_contract',
  1,
  TRUE,
  '{
    "type":"object",
    "required":["contract_no","client_partner_id","contract_type","start_date","billing_rate"],
    "properties":{
      "contract_no":{"type":"string","maxLength":40},
      "project_id":{"type":["string","null"],"format":"uuid"},
      "resource_id":{"type":["string","null"],"format":"uuid"},
      "client_partner_id":{"type":"string","format":"uuid"},
      "contract_type":{"type":"string","enum":["dispatch","ses","contract"]},
      "status":{"type":["string","null"],"enum":["draft","active","ended","terminated"],"default":"active"},
      "start_date":{"type":"string","format":"date"},
      "end_date":{"type":["string","null"],"format":"date"},
      "billing_rate":{"type":"number","minimum":0},
      "billing_rate_type":{"type":["string","null"],"enum":["hourly","daily","monthly"],"default":"monthly"},
      "cost_rate":{"type":["number","null"],"minimum":0},
      "settlement_type":{"type":["string","null"],"enum":["range","fixed"],"default":"range"},
      "settlement_lower_hours":{"type":["number","null"]},
      "settlement_upper_hours":{"type":["number","null"]}
    }
  }'::jsonb,
  '{"title":"Contract","listColumns":["contract_no","contract_type","status","start_date","end_date","billing_rate"]}'::jsonb,
  '{"allowedFilters":["status","contract_type","client_partner_id","resource_id"],"defaultSort":"-updated_at"}'::jsonb,
  '["contract_no","client_partner_id","contract_type","start_date","billing_rate","status"]'::jsonb,
  NULL,
  '{"prefix":"CT-","sequence":"seq_contract_code","padding":4,"field":"contract_no"}'::jsonb,
  '{"description":"Staffing contract"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- staffing_timesheet_summary
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'staffing_timesheet_summary',
  1,
  TRUE,
  '{
    "type":"object",
    "required":["contract_id","resource_id","year_month"],
    "properties":{
      "contract_id":{"type":"string","format":"uuid"},
      "resource_id":{"type":"string","format":"uuid"},
      "year_month":{"type":"string","pattern":"^\\\\d{4}-\\\\d{2}$"},
      "scheduled_hours":{"type":["number","null"]},
      "actual_hours":{"type":["number","null"]},
      "overtime_hours":{"type":["number","null"]},
      "billable_hours":{"type":["number","null"]},
      "settlement_hours":{"type":["number","null"]},
      "total_billing_amount":{"type":["number","null"]},
      "total_cost_amount":{"type":["number","null"]},
      "status":{"type":["string","null"],"enum":["open","submitted","confirmed","invoiced"],"default":"open"},
      "submitted_at":{"type":["string","null"],"format":"date-time"},
      "confirmed_at":{"type":["string","null"],"format":"date-time"},
      "notes":{"type":["string","null"]}
    }
  }'::jsonb,
  '{"title":"Timesheet Summary","listColumns":["year_month","status","actual_hours","overtime_hours","total_billing_amount","updated_at"]}'::jsonb,
  '{"allowedFilters":["year_month","status","contract_id","resource_id"],"defaultSort":"-updated_at"}'::jsonb,
  '["contract_id","resource_id","year_month","status"]'::jsonb,
  NULL,
  NULL,
  '{"description":"Monthly/period timesheet summary for staffing billing"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- staffing_invoice
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'staffing_invoice',
  1,
  TRUE,
  '{
    "type":"object",
    "required":["invoice_no","client_partner_id","billing_year_month"],
    "properties":{
      "invoice_no":{"type":"string","maxLength":40},
      "client_partner_id":{"type":"string","format":"uuid"},
      "billing_year_month":{"type":"string","pattern":"^\\\\d{4}-\\\\d{2}$"},
      "invoice_date":{"type":["string","null"],"format":"date"},
      "due_date":{"type":["string","null"],"format":"date"},
      "subtotal":{"type":["number","null"]},
      "tax_rate":{"type":["number","null"]},
      "tax_amount":{"type":["number","null"]},
      "total_amount":{"type":["number","null"]},
      "paid_amount":{"type":["number","null"]},
      "status":{"type":["string","null"],"enum":["draft","confirmed","issued","partial_paid","paid","cancelled"],"default":"draft"}
    }
  }'::jsonb,
  '{"title":"Invoice","listColumns":["invoice_no","billing_year_month","status","total_amount","paid_amount","updated_at"]}'::jsonb,
  '{"allowedFilters":["billing_year_month","status","client_partner_id"],"defaultSort":"-updated_at"}'::jsonb,
  '["invoice_no","client_partner_id","billing_year_month","status"]'::jsonb,
  NULL,
  '{"prefix":"SI","sequence":"seq_staffing_invoice","padding":4,"field":"invoice_no"}'::jsonb,
  '{"description":"Staffing invoice header"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- staffing_invoice_line
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'staffing_invoice_line',
  1,
  TRUE,
  '{
    "type":"object",
    "required":["invoice_id","line_no"],
    "properties":{
      "invoice_id":{"type":"string","format":"uuid"},
      "line_no":{"type":"integer","minimum":1},
      "contract_id":{"type":["string","null"],"format":"uuid"},
      "resource_id":{"type":["string","null"],"format":"uuid"},
      "timesheet_summary_id":{"type":["string","null"],"format":"uuid"},
      "description":{"type":["string","null"]},
      "quantity":{"type":["number","null"]},
      "unit":{"type":["string","null"]},
      "unit_price":{"type":["number","null"]},
      "line_amount":{"type":["number","null"]}
    }
  }'::jsonb,
  '{"title":"Invoice Line","listColumns":["invoice_id","line_no","description","line_amount"]}'::jsonb,
  '{"allowedFilters":["invoice_id","contract_id","resource_id"],"defaultSort":"line_no"}'::jsonb,
  '["invoice_id","line_no","contract_id","resource_id"]'::jsonb,
  NULL,
  NULL,
  '{"description":"Staffing invoice line"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- staffing_email_account
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'staffing_email_account',
  1,
  TRUE,
  '{
    "type":"object",
    "required":["account_name","email_address"],
    "properties":{
      "account_name":{"type":"string"},
      "email_address":{"type":"string"},
      "account_type":{"type":["string","null"]},
      "imap_host":{"type":["string","null"]},
      "imap_port":{"type":["integer","null"]},
      "smtp_host":{"type":["string","null"]},
      "smtp_port":{"type":["integer","null"]},
      "username":{"type":["string","null"]},
      "password_enc":{"type":["string","null"]},
      "is_default":{"type":["boolean","null"],"default":false},
      "is_active":{"type":["boolean","null"],"default":true}
    }
  }'::jsonb,
  '{"title":"Email Account","listColumns":["account_name","email_address","is_default","is_active","updated_at"]}'::jsonb,
  '{"allowedFilters":["is_active","is_default"],"defaultSort":"-updated_at"}'::jsonb,
  '["account_name","email_address","is_default","is_active"]'::jsonb,
  NULL,
  NULL,
  '{"description":"Staffing email account settings (password stored encrypted)"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- staffing_email_template
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'staffing_email_template',
  1,
  TRUE,
  '{
    "type":"object",
    "required":["template_code","template_name","subject_template","body_template"],
    "properties":{
      "template_code":{"type":"string"},
      "template_name":{"type":"string"},
      "category":{"type":["string","null"]},
      "subject_template":{"type":"string"},
      "body_template":{"type":"string"},
      "variables":{"type":["array","null"],"items":{"type":"string"}},
      "is_active":{"type":["boolean","null"],"default":true}
    }
  }'::jsonb,
  '{"title":"Email Template","listColumns":["template_code","template_name","category","is_active","updated_at"]}'::jsonb,
  '{"allowedFilters":["category","is_active"],"defaultSort":"template_code"}'::jsonb,
  '["template_code","template_name","category","is_active"]'::jsonb,
  NULL,
  NULL,
  '{"description":"Staffing email templates"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- staffing_email_message
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'staffing_email_message',
  1,
  TRUE,
  '{
    "type":"object",
    "properties":{
      "message_id":{"type":["string","null"]},
      "folder":{"type":["string","null"]},
      "from_address":{"type":["string","null"]},
      "to_addresses":{"type":["string","null"]},
      "subject":{"type":["string","null"]},
      "received_at":{"type":["string","null"],"format":"date-time"},
      "status":{"type":["string","null"]},
      "is_read":{"type":["boolean","null"]},
      "parsed_intent":{"type":["string","null"]},
      "parsed_data":{"type":["object","null"]}
    }
  }'::jsonb,
  '{"title":"Email Message","listColumns":["folder","from_address","subject","received_at","status","is_read"]}'::jsonb,
  '{"allowedFilters":["folder","status","is_read"],"defaultSort":"-received_at"}'::jsonb,
  '["folder","from_address","subject","received_at","status"]'::jsonb,
  NULL,
  NULL,
  '{"description":"Inbound email messages"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- staffing_email_queue
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'staffing_email_queue',
  1,
  TRUE,
  '{
    "type":"object",
    "required":["to_addresses","subject"],
    "properties":{
      "to_addresses":{"type":"string"},
      "cc_addresses":{"type":["string","null"]},
      "subject":{"type":"string"},
      "body_html":{"type":["string","null"]},
      "status":{"type":["string","null"],"default":"pending"},
      "scheduled_at":{"type":["string","null"],"format":"date-time"},
      "sent_at":{"type":["string","null"],"format":"date-time"},
      "error_message":{"type":["string","null"]}
    }
  }'::jsonb,
  '{"title":"Email Queue","listColumns":["to_addresses","subject","status","scheduled_at","sent_at"]}'::jsonb,
  '{"allowedFilters":["status"],"defaultSort":"-created_at"}'::jsonb,
  '["to_addresses","subject","status"]'::jsonb,
  NULL,
  NULL,
  '{"description":"Outbound email queue"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- staffing_email_rule
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'staffing_email_rule',
  1,
  TRUE,
  '{
    "type":"object",
    "required":["rule_name","trigger_type","action_type"],
    "properties":{
      "rule_name":{"type":"string"},
      "trigger_type":{"type":"string"},
      "trigger_conditions":{"type":["object","null"]},
      "action_type":{"type":"string"},
      "action_config":{"type":["object","null"]},
      "template_id":{"type":["string","null"],"format":"uuid"},
      "is_active":{"type":["boolean","null"],"default":true}
    }
  }'::jsonb,
  '{"title":"Email Rule","listColumns":["rule_name","trigger_type","action_type","is_active","updated_at"]}'::jsonb,
  '{"allowedFilters":["trigger_type","action_type","is_active"],"defaultSort":"rule_name"}'::jsonb,
  '["rule_name","trigger_type","action_type","is_active"]'::jsonb,
  NULL,
  NULL,
  '{"description":"Email automation rules"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- staffing_purchase_order
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'staffing_purchase_order',
  1,
  TRUE,
  '{
    "type":"object",
    "required":["order_no","resource_id"],
    "properties":{
      "order_no":{"type":"string"},
      "resource_id":{"type":"string","format":"uuid"},
      "contract_id":{"type":["string","null"],"format":"uuid"},
      "order_date":{"type":["string","null"],"format":"date"},
      "period_start":{"type":["string","null"],"format":"date"},
      "period_end":{"type":["string","null"],"format":"date"},
      "unit_price":{"type":["number","null"]},
      "settlement_type":{"type":["string","null"]},
      "status":{"type":["string","null"],"default":"draft"}
    }
  }'::jsonb,
  '{"title":"Purchase Order","listColumns":["order_no","status","order_date","period_start","period_end","updated_at"]}'::jsonb,
  '{"allowedFilters":["status","resource_id"],"defaultSort":"-updated_at"}'::jsonb,
  '["order_no","resource_id","status"]'::jsonb,
  NULL,
  '{"prefix":"PO-","sequence":"seq_purchase_order","padding":4,"field":"order_no"}'::jsonb,
  '{"description":"Freelancer purchase order"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- staffing_freelancer_invoice
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'staffing_freelancer_invoice',
  1,
  TRUE,
  '{
    "type":"object",
    "required":["invoice_no","resource_id","total_amount"],
    "properties":{
      "invoice_no":{"type":"string"},
      "resource_id":{"type":"string","format":"uuid"},
      "period_start":{"type":["string","null"],"format":"date"},
      "period_end":{"type":["string","null"],"format":"date"},
      "subtotal":{"type":["number","null"]},
      "tax_rate":{"type":["number","null"]},
      "tax_amount":{"type":["number","null"]},
      "total_amount":{"type":"number"},
      "status":{"type":["string","null"],"default":"draft"},
      "submitted_at":{"type":["string","null"],"format":"date-time"},
      "approved_at":{"type":["string","null"],"format":"date-time"},
      "paid_at":{"type":["string","null"],"format":"date-time"},
      "paid_amount":{"type":["number","null"]}
    }
  }'::jsonb,
  '{"title":"Freelancer Invoice","listColumns":["invoice_no","status","total_amount","paid_amount","updated_at"]}'::jsonb,
  '{"allowedFilters":["status","resource_id"],"defaultSort":"-updated_at"}'::jsonb,
  '["invoice_no","resource_id","status","total_amount"]'::jsonb,
  NULL,
  '{"prefix":"FI-","sequence":"seq_freelancer_invoice","padding":4,"field":"invoice_no"}'::jsonb,
  '{"description":"Freelancer invoice submitted by contractor"}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;



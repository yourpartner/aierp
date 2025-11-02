CREATE OR REPLACE FUNCTION fn_jsonb_period_start(p jsonb)
RETURNS date
AS '
  SELECT CASE
    WHEN (p->>''periodStart'') ~ ''^[0-9]{4}-[0-9]{2}-[0-9]{2}$''
      THEN (p->>''periodStart'')::date
    ELSE NULL
  END;
' LANGUAGE sql IMMUTABLE;

CREATE OR REPLACE FUNCTION fn_jsonb_period_end(p jsonb)
RETURNS date
AS '
  SELECT CASE
    WHEN (p->>''periodEnd'') ~ ''^[0-9]{4}-[0-9]{2}-[0-9]{2}$''
      THEN (p->>''periodEnd'')::date
    ELSE NULL
  END;
' LANGUAGE sql IMMUTABLE;

CREATE OR REPLACE FUNCTION fn_jsonb_period_is_open(p jsonb)
RETURNS boolean
AS '
  SELECT CASE
    WHEN (p ? ''isOpen'') THEN COALESCE((p->>''isOpen'')::boolean, true)
    ELSE TRUE
  END;
' LANGUAGE sql IMMUTABLE;

CREATE TABLE IF NOT EXISTS accounting_periods (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  period_start DATE GENERATED ALWAYS AS (fn_jsonb_period_start(payload)) STORED,
  period_end DATE GENERATED ALWAYS AS (fn_jsonb_period_end(payload)) STORED,
  is_open BOOLEAN GENERATED ALWAYS AS (fn_jsonb_period_is_open(payload)) STORED
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_accounting_periods_company_range
  ON accounting_periods(company_code, period_start, period_end);
CREATE INDEX IF NOT EXISTS idx_accounting_periods_company_dates
  ON accounting_periods(company_code, period_start, period_end);

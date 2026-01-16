-- Fix missing business partner codes
UPDATE businesspartners 
SET payload = jsonb_set(payload, '{code}', '"BP000006"')
WHERE id = '3ede49dd-a586-4017-a7b2-c5aafacbbb42';

UPDATE businesspartners 
SET payload = jsonb_set(payload, '{code}', '"BP000007"')
WHERE id = '6bb68e6f-9360-45c1-82b9-bb8f757035b8';

-- Update sequence
INSERT INTO bp_sequences (company_code, last_number) 
VALUES ('JP01', 7) 
ON CONFLICT (company_code) DO UPDATE SET last_number = 7;

-- Verify
SELECT id, name, partner_code FROM businesspartners ORDER BY partner_code;


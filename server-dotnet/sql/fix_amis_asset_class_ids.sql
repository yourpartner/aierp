-- Fix AMIS company fixed assets: replace numeric class IDs with actual UUID references
-- Mapping (based on asset name patterns):
--   950 → 建物 (a65fa161-0948-4602-aff0-c53f3a55ada5)
--   807 → 建物付属設備 (1db6a5aa-caf5-4c74-9aec-cf5cc019e528)
--   811 → 工具器具備品 (496b3fa3-ab96-4a2a-81dc-fc6afeae4234)
--   812 → 車両運搬具 (2d4ba7c5-a977-400e-899c-2fa3e34fe922)

UPDATE fixed_assets
SET payload = jsonb_set(payload, '{assetClassId}', '"a65fa161-0948-4602-aff0-c53f3a55ada5"')
WHERE company_code = 'AMIS' AND payload->>'assetClassId' = '950';

UPDATE fixed_assets
SET payload = jsonb_set(payload, '{assetClassId}', '"1db6a5aa-caf5-4c74-9aec-cf5cc019e528"')
WHERE company_code = 'AMIS' AND payload->>'assetClassId' = '807';

UPDATE fixed_assets
SET payload = jsonb_set(payload, '{assetClassId}', '"496b3fa3-ab96-4a2a-81dc-fc6afeae4234"')
WHERE company_code = 'AMIS' AND payload->>'assetClassId' = '811';

UPDATE fixed_assets
SET payload = jsonb_set(payload, '{assetClassId}', '"2d4ba7c5-a977-400e-899c-2fa3e34fe922"')
WHERE company_code = 'AMIS' AND payload->>'assetClassId' = '812';

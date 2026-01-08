# -*- coding: utf-8 -*-
import re

text = open(r'D:\yanxia\server-dotnet\pdf_text.txt', 'r', encoding='utf-8').read()

# 匹配明细行：JPY 金額 コミッション 手数料 純収益
row_re = re.compile(r'JPY\s*\n?([0-9][0-9,]*(?:\.[0-9]{1,2})?)\s*\n?(-?[0-9][0-9,]*)\s*\n?(-?[0-9][0-9,]*)\s*\n?([0-9][0-9,]*(?:\.[0-9]{1,2})?)')

matches = row_re.findall(text)
print(f'Found {len(matches)} detail rows')

gross_sum = 0
comm_sum = 0
fee_sum = 0
net_sum = 0

for i, m in enumerate(matches):
    g = float(m[0].replace(',',''))
    c = float(m[1].replace(',',''))
    f = float(m[2].replace(',',''))
    n = float(m[3].replace(',',''))
    gross_sum += g
    comm_sum += abs(c)
    fee_sum += abs(f)
    net_sum += n
    print(f'{i+1:2d}. gross={g:>10.0f}  comm={c:>7.0f}  fee={f:>6.0f}  net={n:>10.0f}')

print(f'\n=== TOTALS ({len(matches)} rows) ===')
print(f'金額 (gross)     = {gross_sum:,.0f}')
print(f'コミッション     = {comm_sum:,.0f}')
print(f'決済サービス手数料 = {fee_sum:,.0f}')
print(f'純収益 (net)     = {net_sum:,.0f}')
print(f'\n=== VERIFICATION ===')
print(f'gross - comm - fee = {gross_sum - comm_sum - fee_sum:,.0f}')
print(f'Expected net       = {net_sum:,.0f}')
print(f'Match: {abs(gross_sum - comm_sum - fee_sum - net_sum) < 1}')





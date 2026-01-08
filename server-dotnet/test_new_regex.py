# -*- coding: utf-8 -*-
import re

# Read PdfPig output (no spaces between values)
text = open(r'D:\yanxia\server-dotnet\pdfpig_output.txt', 'r', encoding='utf-8').read()

# New regex matching the backend change
# JPY[space?]gross[space?](-comm)[space?](-fee)[space?]net
new_row_re = re.compile(
    r'JPY\s*([0-9][0-9,]*(?:\.[0-9]{1,2})?)\s*(-[0-9][0-9,]*)\s*(-[0-9][0-9,]*)\s*([0-9][0-9,]*(?:\.[0-9]{1,2})?)',
    re.IGNORECASE
)

matches = new_row_re.findall(text)
print(f'Found {len(matches)} rows with new regex')

gross_sum = 0
comm_sum = 0
fee_sum = 0
net_sum = 0

for i, m in enumerate(matches):
    g = float(m[0].replace(',',''))
    c = float(m[1].replace(',',''))  # negative
    f = float(m[2].replace(',',''))  # negative
    n = float(m[3].replace(',',''))
    gross_sum += g
    comm_sum += abs(c)
    fee_sum += abs(f)
    net_sum += n
    if i < 10:
        print(f'{i+1:2d}. gross={g:>10.0f}  comm={c:>7.0f}  fee={f:>6.0f}  net={n:>10.0f}')

print(f'...')
print(f'\n=== TOTALS ({len(matches)} rows) ===')
print(f'金額 (gross)     = {gross_sum:,.0f}')
print(f'コミッション     = {comm_sum:,.0f}')
print(f'決済サービス手数料 = {fee_sum:,.0f}')
print(f'純収益 (net)     = {net_sum:,.0f}')
print(f'\nVerification: gross - comm - fee = {gross_sum - comm_sum - fee_sum:,.0f}')
print(f'Match: {abs(gross_sum - comm_sum - fee_sum - net_sum) < 1}')





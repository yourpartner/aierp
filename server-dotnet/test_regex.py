# -*- coding: utf-8 -*-
import re

text = open(r'D:\yanxia\server-dotnet\pdf_text.txt', 'r', encoding='utf-8').read()

# 找合计行 - 但PDF文本里合計和数字是分行的
print("=== Looking for totals ===")
# 合計\n¥335,295\n¥-49,218\n¥-7,713\n¥279,437
total_re = re.compile(r'合計\s*[¥￥]?([0-9,]+)\s*[¥￥]?(-?[0-9,]+)\s*[¥￥]?(-?[0-9,]+)\s*[¥￥]?(-?[0-9,]+)', re.IGNORECASE | re.DOTALL)
m = total_re.search(text)
if m:
    g = m.group(1).replace(',','')
    c = m.group(2).replace(',','')
    f = m.group(3).replace(',','')
    n = m.group(4).replace(',','')
    print(f'Matched totals: gross={g} comm={c} fee={f} net={n}')
else:
    print('No totals match found with first regex')
    # 尝试更宽松的匹配
    total_re2 = re.compile(r'合計[\s\n]*[¥￥]([0-9,]+)[\s\n]*[¥￥](-?[0-9,]+)[\s\n]*[¥￥](-?[0-9,]+)[\s\n]*[¥￥](-?[0-9,]+)', re.IGNORECASE)
    m = total_re2.search(text)
    if m:
        g = m.group(1).replace(',','')
        c = m.group(2).replace(',','')
        f = m.group(3).replace(',','')
        n = m.group(4).replace(',','')
        print(f'Matched totals (regex2): gross={g} comm={c} fee={f} net={n}')
    else:
        print('No totals match with regex2 either')

# 试着看当前代码里的正则能不能匹配明细行
print("\n=== Looking for detail rows ===")
# 当前代码的正则
row_re = re.compile(r'(?:\bJPY\b\s+)?([0-9][0-9,]*(?:\.[0-9]{1,2})?)\s+(-?[0-9][0-9,]*(?:\.[0-9]{1,2})?)\s+(-?[0-9][0-9,]*(?:\.[0-9]{1,2})?)\s+([0-9][0-9,]*(?:\.[0-9]{1,2})?)')

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
    if i < 30:
        print(f'  {i+1:2d}. {g:>12.2f} {c:>10.2f} {f:>8.2f} {n:>12.2f}')

print(f'\n=== Summed totals ({len(matches)} rows): ===')
print(f'  gross = {gross_sum:.2f}')
print(f'  comm  = {comm_sum:.2f}')
print(f'  fee   = {fee_sum:.2f}')
print(f'  net   = {net_sum:.2f}')
print(f'\n=== Expected (from PDF 合計 line): ===')
print(f'  gross = 335295')
print(f'  comm  = 49218')
print(f'  fee   = 7713')
print(f'  net   = 279437')


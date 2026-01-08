# -*- coding: utf-8 -*-
import re

text = open(r'D:\yanxia\server-dotnet\pdf_text.txt', 'r', encoding='utf-8').read()

# 后端代码目前的正则（Python版本）
# 注意：\s 在Python里默认不包含 \n，需要用 re.DOTALL 或显式写 [\s\n]
backend_row_re = re.compile(
    r'(?:\bJPY\b\s+)?([0-9][0-9,]*(?:\.[0-9]{1,2})?)\s+(-?[0-9][0-9,]*(?:\.[0-9]{1,2})?)\s+(-?[0-9][0-9,]*(?:\.[0-9]{1,2})?)\s+([0-9][0-9,]*(?:\.[0-9]{1,2})?)',
    re.IGNORECASE
)

# 修复后的正则：用 [\s\n]+ 代替 \s+，或者干脆用更宽松的匹配
fixed_row_re = re.compile(
    r'JPY[\s\n]+([0-9][0-9,]*(?:\.[0-9]{1,2})?)[\s\n]+(-?[0-9][0-9,]*)[\s\n]+(-?[0-9][0-9,]*)[\s\n]+([0-9][0-9,]*(?:\.[0-9]{1,2})?)',
    re.IGNORECASE
)

print("=== Backend regex (current) ===")
matches1 = backend_row_re.findall(text)
print(f"Found {len(matches1)} rows")

print("\n=== Fixed regex ===")
matches2 = fixed_row_re.findall(text)
print(f"Found {len(matches2)} rows")

if matches2:
    gross_sum = sum(float(m[0].replace(',','')) for m in matches2)
    net_sum = sum(float(m[3].replace(',','')) for m in matches2)
    print(f"\ngross sum = {gross_sum:,.0f}")
    print(f"net sum   = {net_sum:,.0f}")





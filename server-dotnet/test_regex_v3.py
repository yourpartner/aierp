# -*- coding: utf-8 -*-
import re

text = open(r'D:\yanxia\server-dotnet\pdfpig_output.txt', 'r', encoding='utf-8').read()

# Key insight for "JPY18900.00-2835-43515630.00":
# - gross = 18900.00 (ends with .00)
# - comm = -2835 (negative integer)
# - fee = -435 (negative integer, 2-4 digits typically)
# - net = 15630.00 (ends with .00)

# The trick: fee is followed by net which starts with 4+ digits then .00
# Use lookahead: fee = -\d{2,4} followed by (\d{4,}\.\d{2})
regex_correct = re.compile(
    r'JPY(\d+\.\d{2})(-\d{3,5})(-\d{2,4})(\d{4,}\.\d{2})',
    re.IGNORECASE
)

matches = regex_correct.findall(text)
print(f"Found {len(matches)} rows")

if matches:
    for i, m in enumerate(matches[:5]):
        print(f"{i+1}. gross={m[0]}, comm={m[1]}, fee={m[2]}, net={m[3]}")
    
    gross_sum = sum(float(m[0]) for m in matches)
    comm_sum = sum(abs(float(m[1])) for m in matches)
    fee_sum = sum(abs(float(m[2])) for m in matches)
    net_sum = sum(float(m[3]) for m in matches)
    print(f"\n=== TOTALS ===")
    print(f"gross = {gross_sum:,.0f}")
    print(f"comm  = {comm_sum:,.0f}")
    print(f"fee   = {fee_sum:,.0f}")
    print(f"net   = {net_sum:,.0f}")
    print(f"\nVerification: gross - comm - fee = {gross_sum - comm_sum - fee_sum:,.0f}")
    print(f"Expected net: {net_sum:,.0f}")
    print(f"Match: {abs(gross_sum - comm_sum - fee_sum - net_sum) < 10}")





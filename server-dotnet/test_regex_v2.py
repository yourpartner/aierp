# -*- coding: utf-8 -*-
import re

text = open(r'D:\yanxia\server-dotnet\pdfpig_output.txt', 'r', encoding='utf-8').read()

# Key insight: gross and net have decimal ".00", comm and fee are integers
# Pattern: JPY[gross with .00][comm negative int][fee negative int][net with .00]
# Use lookbehind/lookahead to properly separate fee from net

# Version 2: Fee must be followed by a digit (start of net), and net ends with .00
# Use non-greedy matching for comm/fee, or use explicit decimal constraint

# Approach: comm and fee are INTEGERS (no decimal), net HAS decimal
# So pattern is: JPY + (digits.digits) + (-digits) + (-digits) + (digits.digits)
# But we need to ensure fee doesn't eat net's leading digits

# Solution: Match net's decimal explicitly: (\d+\.\d{2})
# For fee: it must be followed by net (positive number starting with digit, not -)
regex_v2 = re.compile(
    r'JPY(\d+\.\d{2})(-\d+)(-\d+?)(\d+\.\d{2})',  # non-greedy fee
    re.IGNORECASE
)

# Better approach: fee is followed by net which starts with digit (not -)
# Use lookahead: fee = -\d+ but must be followed by \d (start of net)
regex_v3 = re.compile(
    r'JPY(\d+\.\d{2})(-\d+)(-\d+)(?=\d+\.\d{2})(\d+\.\d{2})',
    re.IGNORECASE
)

# Actually the issue is that regex tries to match as much as possible
# Let's try: fee must NOT be followed by more digits that form .00
# Key: net's decimal point is the separator!
regex_v4 = re.compile(
    r'JPY(\d+\.\d{2})(-\d+)(-\d+)(\d+\.\d{2})',
    re.IGNORECASE
)

print("=== Testing different regex versions ===")
for name, rx in [("v2 non-greedy", regex_v2), ("v3 lookahead", regex_v3), ("v4 basic", regex_v4)]:
    matches = rx.findall(text)
    print(f"\n{name}: Found {len(matches)} rows")
    if matches:
        # Just show first 3
        for i, m in enumerate(matches[:3]):
            print(f"  {m}")

# The real solution: comm and fee are both negative integers without decimal
# Gross and net both have .NN decimal
# So we need to split on the LAST minus sign before a decimal number
# Pattern: number must end with .NN for gross/net

# Better idea: use word boundaries or length constraints
# Typical: gross=5-6 digits, comm=4 digits, fee=3 digits, net=5-6 digits
# -2835 (4 digits), -435 (3 digits), 15630.00 (7 chars)

# Final approach: exploit that fee is typically 3-4 digits, not 8+
regex_final = re.compile(
    r'JPY(\d{4,6}\.\d{2})(-\d{3,5})(-\d{2,4})(\d{4,6}\.\d{2})',
    re.IGNORECASE
)
matches = regex_final.findall(text)
print(f"\n=== FINAL regex (with digit count constraints): Found {len(matches)} rows ===")
if matches:
    gross_sum = sum(float(m[0]) for m in matches)
    comm_sum = sum(abs(float(m[1])) for m in matches)
    fee_sum = sum(abs(float(m[2])) for m in matches)
    net_sum = sum(float(m[3]) for m in matches)
    print(f"gross = {gross_sum:,.0f}")
    print(f"comm  = {comm_sum:,.0f}")
    print(f"fee   = {fee_sum:,.0f}")
    print(f"net   = {net_sum:,.0f}")
    print(f"Verification: gross - comm - fee = {gross_sum - comm_sum - fee_sum:,.0f}")





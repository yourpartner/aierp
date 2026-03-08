using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using UglyToad.PdfPig;

var path = @"D:\yanxia\server-dotnet\test_booking.pdf";
using var doc = PdfDocument.Open(path);

// Use GetWords() to get positioned words
Console.WriteLine("=== Using GetWords() for geometry-based extraction ===\n");

decimal grossSum = 0, commSum = 0, feeSum = 0, netSum = 0;
int rowCount = 0;

foreach (var page in doc.GetPages())
{
    var words = page.GetWords().ToList();
    Console.WriteLine($"Page {page.Number}: {words.Count} words");
    
    // Find header row positions by looking for column labels
    // Headers: 金額 | コミッション | 決済サービスの手数料 | 純収益
    double? xGross = null, xComm = null, xFee = null, xNet = null;
    
    foreach (var w in words)
    {
        var txt = w.Text;
        if (txt.Contains("金額") || txt.Contains("⾦額")) 
            xGross = w.BoundingBox.Left + w.BoundingBox.Width / 2;
        if (txt.Contains("コミッション")) 
            xComm = w.BoundingBox.Left + w.BoundingBox.Width / 2;
        if (txt.Contains("決済") || txt.Contains("⼿数料"))
            xFee = w.BoundingBox.Left + w.BoundingBox.Width / 2;
        if (txt.Contains("純収益") || txt.Contains("純収"))
            xNet = w.BoundingBox.Left + w.BoundingBox.Width / 2;
    }
    
    Console.WriteLine($"  Header positions: gross={xGross:F0} comm={xComm:F0} fee={xFee:F0} net={xNet:F0}");
    
    if (xGross == null || xComm == null || xNet == null) continue;
    xFee ??= (xComm.Value + xNet.Value) / 2;
    
    // Group words by Y (row)
    var rowGroups = words
        .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 10) * 10) // 10pt buckets
        .OrderByDescending(g => g.Key);
    
    foreach (var rowGroup in rowGroups)
    {
        var rowWords = rowGroup.OrderBy(w => w.BoundingBox.Left).ToList();
        var rowText = string.Join(" ", rowWords.Select(w => w.Text));
        
        // Skip header rows
        if (rowText.Contains("金額") || rowText.Contains("コミッション") || rowText.Contains("純収益")) continue;
        
        // Find numeric tokens in each column zone
        string? tokGross = null, tokComm = null, tokFee = null, tokNet = null;
        
        double b1 = (xGross.Value + xComm.Value) / 2;
        double b2 = (xComm.Value + xFee.Value) / 2;
        double b3 = (xFee.Value + xNet.Value) / 2;
        
        foreach (var w in rowWords)
        {
            var t = w.Text.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            
            // Skip non-numeric tokens
            if (!char.IsDigit(t[0]) && t[0] != '-' && t[0] != '.') continue;
            
            var x = w.BoundingBox.Left + w.BoundingBox.Width / 2;
            
            if (x < b1) tokGross = t;
            else if (x < b2) tokComm = t;
            else if (x < b3) tokFee = t;
            else tokNet = t;
        }
        
        // Parse and sum
        if (tokGross != null && tokComm != null && tokFee != null && tokNet != null)
        {
            if (TryParseMoney(tokGross, out var g) && 
                TryParseMoney(tokComm, out var c) && 
                TryParseMoney(tokFee, out var f) && 
                TryParseMoney(tokNet, out var n))
            {
                grossSum += g;
                commSum += Math.Abs(c);
                feeSum += Math.Abs(f);
                netSum += n;
                rowCount++;
                if (rowCount <= 5)
                    Console.WriteLine($"    Row {rowCount}: g={g} c={c} f={f} n={n}");
            }
        }
    }
}

Console.WriteLine($"\n=== TOTALS ({rowCount} rows) ===");
Console.WriteLine($"gross = {grossSum:N0}");
Console.WriteLine($"comm  = {commSum:N0}");
Console.WriteLine($"fee   = {feeSum:N0}");
Console.WriteLine($"net   = {netSum:N0}");
Console.WriteLine($"\nVerification: gross - comm - fee = {grossSum - commSum - feeSum:N0}");

static bool TryParseMoney(string raw, out decimal value)
{
    value = 0m;
    if (string.IsNullOrWhiteSpace(raw)) return false;
    var cleaned = raw.Trim().Replace(",", "").Replace("¥", "").Replace("￥", "");
    return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}


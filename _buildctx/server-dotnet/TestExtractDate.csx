#r "bin\Debug\net8.0\PdfPig.dll"
using UglyToad.PdfPig;
using System.Text.RegularExpressions;

var path = @"D:\yanxia\server-dotnet\bin\Debug\net8.0\uploads\ai-files\2b5361e1da2f46948db3b45a30b072c8.pdf";
using var doc = PdfDocument.Open(path);
var allText = string.Join("\n", doc.GetPages().Select(p => p.Text));

Console.WriteLine($"Total text length: {allText.Length}");
Console.WriteLine();

// Search for payment date keywords
var keywords = new[] { "お支払い日", "支払日", "支払予定日", "お支払い予定日", "振込日" };
foreach (var kw in keywords)
{
    var idx = allText.IndexOf(kw);
    if (idx >= 0)
    {
        var start = Math.Max(0, idx - 30);
        var end = Math.Min(allText.Length, idx + 100);
        var context = allText.Substring(start, end - start).Replace("\n", "\\n").Replace("\r", "");
        Console.WriteLine($"=== Found: {kw} ===");
        Console.WriteLine(context);
        Console.WriteLine();
    }
}

// Try regex match
var text = allText;
var m = Regex.Match(
    text,
    @"(?:お支払い日|支払日|支払予定日|お支払い予定日|振込日)\s*[:：]?\s*([0-9０-９]{4})\s*年\s*([0-9０-９]{1,2})\s*月\s*([0-9０-９]{1,2})\s*日",
    RegexOptions.IgnoreCase);
Console.WriteLine($"Regex match 1 (年月日): {m.Success}");
if (m.Success) Console.WriteLine($"  Matched: {m.Value}");

m = Regex.Match(
    text,
    @"(?:お支払い日|支払日|支払予定日|お支払い予定日|振込日)\s*[:：]?\s*([0-9０-９]{4})\s*[-/\.]\s*([0-9０-９]{1,2})\s*[-/\.]\s*([0-9０-９]{1,2})",
    RegexOptions.IgnoreCase);
Console.WriteLine($"Regex match 2 (slash/hyphen): {m.Success}");
if (m.Success) Console.WriteLine($"  Matched: {m.Value}");


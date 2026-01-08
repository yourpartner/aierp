using UglyToad.PdfPig;
using System;
using System.Linq;

var path = @"D:\yanxia\docs\9鏈?9鏃ュ叆閲戞笀銇?booking 閫€鎴挎棩0918鏃ュ埌24鏃?pdf";
using var doc = PdfDocument.Open(path);

Console.WriteLine($"Pages: {doc.NumberOfPages}");
foreach (var page in doc.GetPages())
{
    var text = string.Join("", page.Letters.Select(l => l.Value));
    Console.WriteLine($"--- Page {page.Number} (chars: {text.Length}) ---");
    Console.WriteLine(text.Substring(0, Math.Min(3000, text.Length)));
    Console.WriteLine("...");
}

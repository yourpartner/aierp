using System.Text.Json;
using Npgsql;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Server.Infrastructure;

namespace Server.Modules;

public sealed class SalesPdfService
{
    private readonly AzureBlobService _blobService;

    public SalesPdfService(AzureBlobService blobService)
    {
        _blobService = blobService;
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public record CompanyInfo(string Name, string PostalCode, string Address, string Tel, string? RegistrationNo);
    public record InvoiceLine(int LineNo, string MaterialCode, string MaterialName, decimal Qty, string Uom, decimal UnitPrice, decimal Amount, decimal TaxRate, decimal TaxAmount);
    public record InvoiceData(string InvoiceNo, string InvoiceDate, string DueDate, string CustomerCode, string CustomerName, decimal AmountTotal, decimal TaxAmount, string? Note, List<InvoiceLine> Lines);
    public record DeliveryLine(int LineNo, string MaterialCode, string MaterialName, decimal Qty, string Uom);
    public record DeliveryData(string DeliveryNo, string DeliveryDate, string? SalesOrderNo, string CustomerCode, string CustomerName, string? Note, List<DeliveryLine> Lines);

    static CompanyInfo GetCompanyFallback(string cc) => new($"会社 {cc}", "100-0001", "東京都千代田区", "03-0000-0000", null);

    public async Task<string> GenerateInvoicePdfAsync(string companyCode, CompanyInfo company, InvoiceData inv, CancellationToken ct)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginTop(30);
                page.MarginBottom(25);
                page.MarginHorizontal(35);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Yu Gothic").FontColor(Colors.Grey.Darken4));
                page.Content().Column(col =>
                {
                    RenderInvoiceContent(col, company, inv);
                });
            });
        });

        using var ms = new MemoryStream();
        doc.GeneratePdf(ms);
        ms.Position = 0;

        var now = DateTime.UtcNow;
        var blobName = $"{companyCode}/sales-invoices/{now:yyyy}/{now:MM}/請求書_{inv.InvoiceNo}.pdf";
        await _blobService.UploadAsync(ms, blobName, "application/pdf", ct);
        return blobName;
    }

    public async Task<string> GenerateDeliveryNotePdfAsync(string companyCode, CompanyInfo company, DeliveryData dn, CancellationToken ct)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginTop(30);
                page.MarginBottom(25);
                page.MarginHorizontal(35);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Yu Gothic").FontColor(Colors.Grey.Darken4));
                page.Content().Column(col =>
                {
                    RenderDeliveryNoteContent(col, company, dn);
                });
            });
        });

        using var ms = new MemoryStream();
        doc.GeneratePdf(ms);
        ms.Position = 0;

        var now = DateTime.UtcNow;
        var blobName = $"{companyCode}/delivery-notes/{now:yyyy}/{now:MM}/納品書_{dn.DeliveryNo}.pdf";
        await _blobService.UploadAsync(ms, blobName, "application/pdf", ct);
        return blobName;
    }

    public string GetReadUri(string blobName) => _blobService.GetReadUri(blobName);

    #region Invoice PDF Layout

    static void RenderInvoiceContent(ColumnDescriptor col, CompanyInfo company, InvoiceData inv)
    {
        // Title
        col.Item().PaddingBottom(8).AlignCenter().Text("請 求 書").FontSize(20).Bold().FontColor(Colors.Grey.Darken3);

        // Date & Number row
        col.Item().Row(row =>
        {
            row.RelativeItem().Text($"請求書番号: {inv.InvoiceNo}").FontSize(9);
            row.RelativeItem().AlignRight().Text($"発行日: {inv.InvoiceDate}").FontSize(9);
        });

        col.Item().PaddingVertical(6).LineHorizontal(1.5f).LineColor(Colors.Blue.Darken2);

        // Customer + Company side by side
        col.Item().PaddingBottom(10).Row(row =>
        {
            // Left: Customer
            row.RelativeItem(5).Column(c =>
            {
                c.Item().PaddingBottom(2).Text($"{inv.CustomerName} 御中").FontSize(13).Bold();
                c.Item().PaddingBottom(6).Text($"取引先コード: {inv.CustomerCode}").FontSize(8).FontColor(Colors.Grey.Medium);
                c.Item().PaddingTop(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten1).PaddingBottom(4)
                    .Text(t =>
                    {
                        t.Span("ご請求金額: ").FontSize(11);
                        t.Span($"¥{inv.AmountTotal:#,0}").FontSize(16).Bold().FontColor(Colors.Blue.Darken2);
                        t.Span("（税込）").FontSize(9).FontColor(Colors.Grey.Medium);
                    });
                c.Item().PaddingTop(4).Text($"お支払期限: {inv.DueDate}").FontSize(9);
            });

            row.ConstantItem(20);

            // Right: Company
            row.RelativeItem(4).AlignRight().Column(c =>
            {
                c.Item().AlignRight().Text(company.Name).FontSize(10).Bold();
                c.Item().AlignRight().Text($"〒{company.PostalCode}").FontSize(8);
                c.Item().AlignRight().Text(company.Address).FontSize(8);
                c.Item().AlignRight().Text($"TEL: {company.Tel}").FontSize(8);
                if (!string.IsNullOrEmpty(company.RegistrationNo))
                    c.Item().AlignRight().PaddingTop(2).Text($"登録番号: {company.RegistrationNo}").FontSize(7).FontColor(Colors.Grey.Medium);
            });
        });

        // Line items table
        col.Item().PaddingTop(4).Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(28);   // #
                cd.ConstantColumn(80);   // Code
                cd.RelativeColumn(3);    // Name
                cd.ConstantColumn(50);   // Qty
                cd.ConstantColumn(35);   // Uom
                cd.ConstantColumn(70);   // UnitPrice
                cd.ConstantColumn(80);   // Amount
                cd.ConstantColumn(35);   // Tax%
                cd.ConstantColumn(65);   // Tax
            });

            // Header
            table.Header(h =>
            {
                var hStyle = TextStyle.Default.FontSize(8).Bold().FontColor(Colors.White);
                void HCell(IContainer c, string t, bool right = false)
                {
                    var cell = c.Background(Colors.Blue.Darken2).Padding(4);
                    if (right) cell.AlignRight().Text(t).Style(hStyle);
                    else cell.Text(t).Style(hStyle);
                }
                HCell(h.Cell(), "#");
                HCell(h.Cell(), "品目コード");
                HCell(h.Cell(), "品目名");
                HCell(h.Cell(), "数量", true);
                HCell(h.Cell(), "単位");
                HCell(h.Cell(), "単価", true);
                HCell(h.Cell(), "金額", true);
                HCell(h.Cell(), "税率", true);
                HCell(h.Cell(), "税額", true);
            });

            var altBg = Colors.Blue.Lighten5;
            for (int i = 0; i < inv.Lines.Count; i++)
            {
                var line = inv.Lines[i];
                var bg = i % 2 == 1 ? altBg : Colors.White;
                void DCell(IContainer c, string t, bool right = false)
                {
                    var cell = c.Background(bg).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3);
                    if (right) cell.AlignRight().Text(t).FontSize(8);
                    else cell.Text(t).FontSize(8);
                }
                DCell(table.Cell(), line.LineNo.ToString());
                DCell(table.Cell(), line.MaterialCode);
                DCell(table.Cell(), line.MaterialName);
                DCell(table.Cell(), $"{line.Qty:#,0.##}", true);
                DCell(table.Cell(), line.Uom);
                DCell(table.Cell(), $"¥{line.UnitPrice:#,0}", true);
                DCell(table.Cell(), $"¥{line.Amount:#,0}", true);
                DCell(table.Cell(), $"{line.TaxRate}%", true);
                DCell(table.Cell(), $"¥{line.TaxAmount:#,0}", true);
            }
        });

        // Totals
        col.Item().PaddingTop(6).AlignRight().Width(220).Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.RelativeColumn();
                cd.ConstantColumn(100);
            });
            void TRow(IContainer label, IContainer val, string l, string v, bool bold = false)
            {
                label.Padding(3).Text(l).FontSize(9);
                var vt = val.Padding(3).AlignRight().Text(v).FontSize(9);
                if (bold) vt.Bold().FontColor(Colors.Blue.Darken2);
            }

            var subTotal = inv.AmountTotal - inv.TaxAmount;
            TRow(table.Cell(), table.Cell(), "小計（税抜）", $"¥{subTotal:#,0}");
            TRow(table.Cell(), table.Cell(), "消費税", $"¥{inv.TaxAmount:#,0}");
            table.Cell().ColumnSpan(2).PaddingVertical(1).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
            TRow(table.Cell(), table.Cell(), "合計（税込）", $"¥{inv.AmountTotal:#,0}", true);
        });

        // Note
        if (!string.IsNullOrWhiteSpace(inv.Note))
        {
            col.Item().PaddingTop(12).Column(c =>
            {
                c.Item().Text("備考").FontSize(8).Bold();
                c.Item().PaddingTop(2).Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(6).MinHeight(40)
                    .Text(inv.Note).FontSize(8).FontColor(Colors.Grey.Darken2);
            });
        }

        // Footer
        col.Item().PaddingTop(14).AlignCenter().Text("上記の通りご請求申し上げます。").FontSize(8).FontColor(Colors.Grey.Medium);
    }

    #endregion

    #region Delivery Note PDF Layout

    static void RenderDeliveryNoteContent(ColumnDescriptor col, CompanyInfo company, DeliveryData dn)
    {
        col.Item().PaddingBottom(8).AlignCenter().Text("納 品 書").FontSize(20).Bold().FontColor(Colors.Grey.Darken3);

        col.Item().Row(row =>
        {
            row.RelativeItem().Text($"納品書番号: {dn.DeliveryNo}").FontSize(9);
            row.RelativeItem().AlignRight().Text($"納品日: {dn.DeliveryDate}").FontSize(9);
        });

        col.Item().PaddingVertical(6).LineHorizontal(1.5f).LineColor(Colors.Green.Darken2);

        col.Item().PaddingBottom(10).Row(row =>
        {
            row.RelativeItem(5).Column(c =>
            {
                c.Item().PaddingBottom(2).Text($"{dn.CustomerName} 御中").FontSize(13).Bold();
                c.Item().Text($"取引先コード: {dn.CustomerCode}").FontSize(8).FontColor(Colors.Grey.Medium);
                if (!string.IsNullOrEmpty(dn.SalesOrderNo))
                    c.Item().PaddingTop(4).Text($"受注番号: {dn.SalesOrderNo}").FontSize(9);
            });

            row.ConstantItem(20);

            row.RelativeItem(4).AlignRight().Column(c =>
            {
                c.Item().AlignRight().Text(company.Name).FontSize(10).Bold();
                c.Item().AlignRight().Text($"〒{company.PostalCode}").FontSize(8);
                c.Item().AlignRight().Text(company.Address).FontSize(8);
                c.Item().AlignRight().Text($"TEL: {company.Tel}").FontSize(8);
            });
        });

        col.Item().PaddingTop(4).Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(30);   // #
                cd.ConstantColumn(100);  // Code
                cd.RelativeColumn(4);    // Name
                cd.ConstantColumn(70);   // Qty
                cd.ConstantColumn(50);   // Uom
            });

            table.Header(h =>
            {
                var hStyle = TextStyle.Default.FontSize(8).Bold().FontColor(Colors.White);
                void HCell(IContainer c, string t, bool right = false)
                {
                    var cell = c.Background(Colors.Green.Darken2).Padding(4);
                    if (right) cell.AlignRight().Text(t).Style(hStyle);
                    else cell.Text(t).Style(hStyle);
                }
                HCell(h.Cell(), "#");
                HCell(h.Cell(), "品目コード");
                HCell(h.Cell(), "品目名");
                HCell(h.Cell(), "数量", true);
                HCell(h.Cell(), "単位");
            });

            var altBg = Colors.Green.Lighten5;
            for (int i = 0; i < dn.Lines.Count; i++)
            {
                var line = dn.Lines[i];
                var bg = i % 2 == 1 ? altBg : Colors.White;
                void DCell(IContainer c, string t, bool right = false)
                {
                    var cell = c.Background(bg).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3);
                    if (right) cell.AlignRight().Text(t).FontSize(8);
                    else cell.Text(t).FontSize(8);
                }
                DCell(table.Cell(), line.LineNo.ToString());
                DCell(table.Cell(), line.MaterialCode);
                DCell(table.Cell(), line.MaterialName);
                DCell(table.Cell(), $"{line.Qty:#,0.##}", true);
                DCell(table.Cell(), line.Uom);
            }
        });

        if (!string.IsNullOrWhiteSpace(dn.Note))
        {
            col.Item().PaddingTop(12).Column(c =>
            {
                c.Item().Text("備考").FontSize(8).Bold();
                c.Item().PaddingTop(2).Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(6).MinHeight(40)
                    .Text(dn.Note).FontSize(8).FontColor(Colors.Grey.Darken2);
            });
        }

        col.Item().PaddingTop(14).AlignCenter().Text("上記の通り納品致します。ご査収ください。").FontSize(8).FontColor(Colors.Grey.Medium);
    }

    #endregion

    #region Helpers to extract data from JsonElement

    public static InvoiceData ExtractInvoiceData(JsonElement payload, string invoiceNo)
    {
        var header = payload.TryGetProperty("header", out var h) ? h : payload;
        var lines = new List<InvoiceLine>();

        if (payload.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in linesEl.EnumerateArray())
            {
                lines.Add(new InvoiceLine(
                    LineNo: Jint(l, "lineNo"),
                    MaterialCode: Jstr(l, "materialCode"),
                    MaterialName: Jstr(l, "materialName"),
                    Qty: Jdec(l, "quantity"),
                    Uom: Jstr(l, "uom"),
                    UnitPrice: Jdec(l, "unitPrice"),
                    Amount: Jdec(l, "amount"),
                    TaxRate: Jdec(l, "taxRate"),
                    TaxAmount: Jdec(l, "taxAmount")
                ));
            }
        }

        return new InvoiceData(
            InvoiceNo: invoiceNo,
            InvoiceDate: Jstr(header, "invoiceDate"),
            DueDate: Jstr(header, "dueDate"),
            CustomerCode: Jstr(header, "customerCode"),
            CustomerName: Jstr(header, "customerName"),
            AmountTotal: Jdec(header, "amountTotal"),
            TaxAmount: Jdec(header, "taxAmount"),
            Note: Jstr(header, "note"),
            Lines: lines
        );
    }

    public static DeliveryData ExtractDeliveryData(JsonElement payload, string deliveryNo)
    {
        var header = payload.TryGetProperty("header", out var h) ? h : payload;
        var lines = new List<DeliveryLine>();

        if (payload.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in linesEl.EnumerateArray())
            {
                lines.Add(new DeliveryLine(
                    LineNo: Jint(l, "lineNo"),
                    MaterialCode: Jstr(l, "materialCode"),
                    MaterialName: Jstr(l, "materialName"),
                    Qty: l.TryGetProperty("deliveryQty", out var dq) && dq.TryGetDecimal(out var dqv) ? dqv
                       : l.TryGetProperty("qty", out var q) && q.TryGetDecimal(out var qv) ? qv
                       : Jdec(l, "quantity"),
                    Uom: Jstr(l, "uom")
                ));
            }
        }

        return new DeliveryData(
            DeliveryNo: deliveryNo,
            DeliveryDate: Jstr(header, "deliveryDate"),
            SalesOrderNo: header.TryGetProperty("salesOrderNo", out var soNo) ? soNo.GetString() : null,
            CustomerCode: Jstr(header, "customerCode"),
            CustomerName: Jstr(header, "customerName"),
            Note: header.TryGetProperty("note", out var n) ? n.GetString() : null,
            Lines: lines
        );
    }

    public static async Task<CompanyInfo> GetCompanyInfoAsync(NpgsqlDataSource ds, string companyCode)
    {
        try
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, COALESCE(payload->>'postalCode',''), COALESCE(payload->>'address',''), COALESCE(payload->>'tel',''), payload->>'invoiceRegistrationNo' FROM companies WHERE company_code = $1 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            await using var rd = await cmd.ExecuteReaderAsync();
            if (await rd.ReadAsync())
            {
                return new CompanyInfo(
                    rd.IsDBNull(0) ? companyCode : rd.GetString(0),
                    rd.IsDBNull(1) ? "" : rd.GetString(1),
                    rd.IsDBNull(2) ? "" : rd.GetString(2),
                    rd.IsDBNull(3) ? "" : rd.GetString(3),
                    rd.IsDBNull(4) ? null : rd.GetString(4)
                );
            }
        }
        catch { }
        return GetCompanyFallback(companyCode);
    }

    static string Jstr(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static decimal Jdec(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.TryGetDecimal(out var d) ? d : 0m;
    static int Jint(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.TryGetInt32(out var i) ? i : 0;

    #endregion
}

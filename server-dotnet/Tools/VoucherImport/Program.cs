using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Npgsql;

/// <summary>
/// 会计凭证数据导入工具
/// 从老系统 MySQL dump 导入到新系统 PostgreSQL
/// 只导入公司 172 的数据到 JP01
/// </summary>
class Program
{
    const string TargetCompanyCode = "JP01";
    const string SourceCompanyId = "172";
    
    // Azure Storage配置
    static readonly string AzureConnectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING") ?? "YOUR_CONNECTION_STRING_HERE";
    const string SourceContainer = "172itb";
    const string TargetContainer = "aierp";
    
    // 映射表
    static readonly Dictionary<string, Guid> PartnerIdMap = new();      // 老BusinessPartnerId -> 新UUID
    static readonly Dictionary<string, Guid> EmployeeIdMap = new();     // 老EmployeeId -> 新UUID
    static readonly Dictionary<string, Guid> AssetIdMap = new();        // 老AssetId -> 新UUID
    static readonly Dictionary<string, Guid> DepartmentIdMap = new();   // 老TeamId -> 新UUID
    
    // 老系统数据缓存
    static readonly Dictionary<string, string> OldPartnerNames = new(); // 老Id -> 名称
    static readonly Dictionary<string, string> OldEmployeeNames = new();// 老Id -> 名称
    static readonly Dictionary<string, string> OldAssetNames = new();   // 老Id -> 名称
    
    // 部门固定映射 (TeamId -> DepartmentUUID)
    static readonly Dictionary<string, Guid> TeamMapping = new()
    {
        ["554"] = Guid.Parse("ded8b679-efa7-43b9-a620-d608364ab445"), // BPOサ-ビス -> D003 バックオフィス
        ["555"] = Guid.Parse("bb34336c-410b-4ab4-b294-b3bad66315cb"), // SAP事業部 -> D001 AI開発
        ["560"] = Guid.Parse("bb34336c-410b-4ab4-b294-b3bad66315cb"), // IT導入補助金 -> D001 AI開発
        ["1001"] = Guid.Parse("d2a6dfca-443b-496d-9a28-51af47e64f0f"), // 河口湖ホテル -> D002 ホテル運営
        ["1002"] = Guid.Parse("d2a6dfca-443b-496d-9a28-51af47e64f0f"), // 富士宮ホテル -> D002 ホテル運営
    };
    
    // 不平衡凭证列表
    static readonly List<UnbalancedVoucher> UnbalancedVouchers = new();
    
    // 统计
    static int totalHeaders = 0;
    static int importedHeaders = 0;
    static int skippedHeaders = 0;
    static int attachmentsCopied = 0;
    static int attachmentsFailed = 0;

    static async Task Main(string[] args)
    {
        var dumpDir = @"D:\yanxia\server-dotnet\Dump20251222";
        var connStr = "Host=localhost;Database=postgres;Username=postgres;Password=Hpxdcd2508";

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║          会计凭证数据导入工具 (Voucher Import)             ║");
        Console.WriteLine("╠════════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  源公司ID: {SourceCompanyId,-10} → 目标公司: {TargetCompanyCode,-20}  ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        await using var dataSource = NpgsqlDataSource.Create(connStr);

        // 1. 加载映射数据
        Console.WriteLine("【1】加载ID映射数据...");
        await LoadMappings(dataSource, dumpDir);
        
        // 2. 解析凭证数据
        Console.WriteLine("\n【2】解析凭证数据...");
        var headersFile = Path.Combine(dumpDir, "yourpartnerdb2_acdocheaders.sql");
        var itemsFile = Path.Combine(dumpDir, "yourpartnerdb2_acdocitems.sql");
        
        var headers = ParseVoucherHeaders(File.ReadAllText(headersFile, Encoding.UTF8));
        var items = ParseVoucherItems(File.ReadAllText(itemsFile, Encoding.UTF8));
        
        Console.WriteLine($"  解析到 {headers.Count} 张公司 {SourceCompanyId} 的凭证");
        Console.WriteLine($"  解析到 {items.Count} 条公司 {SourceCompanyId} 的凭证明细");
        
        // 3. 组装凭证并检查借贷平衡
        Console.WriteLine("\n【3】检查借贷平衡...");
        var vouchers = AssembleVouchers(headers, items);
        
        if (UnbalancedVouchers.Count > 0)
        {
            Console.WriteLine($"\n  ⚠️ 发现 {UnbalancedVouchers.Count} 张不平衡凭证:");
            foreach (var uv in UnbalancedVouchers.Take(20))
            {
                Console.WriteLine($"    凭证号: {uv.DocNo}, 借方: {uv.DebitTotal:N2}, 贷方: {uv.CreditTotal:N2}, 差额: {uv.Difference:N2}");
            }
            if (UnbalancedVouchers.Count > 20)
            {
                Console.WriteLine($"    ... 还有 {UnbalancedVouchers.Count - 20} 张");
            }
        }
        else
        {
            Console.WriteLine("  ✓ 所有凭证借贷平衡");
        }
        
        // 4. 导入凭证
        Console.WriteLine("\n【4】导入凭证到数据库...");
        await ImportVouchers(dataSource, vouchers);
        
        // 5. 输出统计
        Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                       导入完成                             ║");
        Console.WriteLine("╠════════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  总凭证数: {totalHeaders,-10} 成功: {importedHeaders,-10} 跳过: {skippedHeaders,-10}  ║");
        Console.WriteLine($"║  附件拷贝: {attachmentsCopied,-10} 失败: {attachmentsFailed,-10}                 ║");
        Console.WriteLine($"║  不平衡凭证: {UnbalancedVouchers.Count,-10}                                   ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        
        // 保存不平衡凭证报告
        if (UnbalancedVouchers.Count > 0)
        {
            var reportPath = Path.Combine(dumpDir, "unbalanced_vouchers_report.txt");
            await File.WriteAllLinesAsync(reportPath, UnbalancedVouchers.Select(u => 
                $"{u.DocNo}\t借方:{u.DebitTotal:N2}\t贷方:{u.CreditTotal:N2}\t差额:{u.Difference:N2}"));
            Console.WriteLine($"\n不平衡凭证报告已保存到: {reportPath}");
        }
    }

    #region 加载映射
    
    static async Task LoadMappings(NpgsqlDataSource ds, string dumpDir)
    {
        await using var conn = await ds.OpenConnectionAsync();
        
        // 1. 加载老系统取引先名称
        Console.WriteLine("  加载老系统取引先名称...");
        var bpFile = Path.Combine(dumpDir, "yourpartnerdb2_businesspartners.sql");
        ParseOldBusinessPartners(File.ReadAllText(bpFile, Encoding.UTF8));
        Console.WriteLine($"    → {OldPartnerNames.Count} 条");
        
        // 2. 加载新系统取引先，建立名称->UUID映射
        Console.WriteLine("  加载新系统取引先...");
        var newPartnersByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, payload->>'name' as name FROM businesspartners WHERE company_code = $1";
            cmd.Parameters.AddWithValue(TargetCompanyCode);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetGuid(0);
                var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (!string.IsNullOrWhiteSpace(name) && !newPartnersByName.ContainsKey(name))
                {
                    newPartnersByName[name] = id;
                }
            }
        }
        Console.WriteLine($"    → {newPartnersByName.Count} 条");
        
        // 建立老ID -> 新UUID映射
        foreach (var kvp in OldPartnerNames)
        {
            if (newPartnersByName.TryGetValue(kvp.Value, out var newId))
            {
                PartnerIdMap[kvp.Key] = newId;
            }
        }
        Console.WriteLine($"    → 匹配 {PartnerIdMap.Count} 条");
        
        // 3. 加载老系统员工名称
        Console.WriteLine("  加载老系统员工名称...");
        var empFile = Path.Combine(dumpDir, "yourpartnerdb2_employees.sql");
        ParseOldEmployees(File.ReadAllText(empFile, Encoding.UTF8));
        Console.WriteLine($"    → {OldEmployeeNames.Count} 条");
        
        // 4. 加载新系统员工
        Console.WriteLine("  加载新系统员工...");
        var newEmployeesByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, payload->>'nameKanji' as name FROM employees WHERE company_code = $1";
            cmd.Parameters.AddWithValue(TargetCompanyCode);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetGuid(0);
                var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    // 添加原始格式 (带空格)
                    if (!newEmployeesByName.ContainsKey(name))
                        newEmployeesByName[name] = id;
                    
                    // 也添加无空格版本
                    var noSpace = name.Replace(" ", "").Replace("　", "");
                    if (!newEmployeesByName.ContainsKey(noSpace))
                        newEmployeesByName[noSpace] = id;
                }
            }
        }
        Console.WriteLine($"    → {newEmployeesByName.Count} 条");
        
        // 建立老ID -> 新UUID映射 (尝试多种匹配方式)
        foreach (var kvp in OldEmployeeNames.Where(k => !k.Key.EndsWith("_condensed")))
        {
            var oldId = kvp.Key;
            var oldName = kvp.Value;
            
            // 尝试直接匹配
            if (newEmployeesByName.TryGetValue(oldName, out var newId))
            {
                EmployeeIdMap[oldId] = newId;
                continue;
            }
            
            // 尝试无空格匹配
            var noSpace = oldName.Replace(" ", "").Replace("　", "");
            if (newEmployeesByName.TryGetValue(noSpace, out newId))
            {
                EmployeeIdMap[oldId] = newId;
                continue;
            }
            
            // 尝试condensed名称匹配
            if (OldEmployeeNames.TryGetValue($"{oldId}_condensed", out var condensed))
            {
                if (newEmployeesByName.TryGetValue(condensed, out newId))
                {
                    EmployeeIdMap[oldId] = newId;
                }
            }
        }
        Console.WriteLine($"    → 匹配 {EmployeeIdMap.Count} 条");
        
        // 5. 加载老系统资产名称
        Console.WriteLine("  加载老系统资产名称...");
        var assetFile = Path.Combine(dumpDir, "yourpartnerdb2_assets.sql");
        ParseOldAssets(File.ReadAllText(assetFile, Encoding.UTF8));
        Console.WriteLine($"    → {OldAssetNames.Count} 条");
        
        // 6. 加载新系统资产
        Console.WriteLine("  加载新系统资产...");
        var newAssetsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, payload->>'assetName' as name FROM fixed_assets WHERE company_code = $1";
            cmd.Parameters.AddWithValue(TargetCompanyCode);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetGuid(0);
                var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (!string.IsNullOrWhiteSpace(name) && !newAssetsByName.ContainsKey(name))
                {
                    newAssetsByName[name] = id;
                }
            }
        }
        Console.WriteLine($"    → {newAssetsByName.Count} 条");
        
        foreach (var kvp in OldAssetNames)
        {
            if (newAssetsByName.TryGetValue(kvp.Value, out var newId))
            {
                AssetIdMap[kvp.Key] = newId;
            }
        }
        Console.WriteLine($"    → 匹配 {AssetIdMap.Count} 条");
        
        // 7. 部门映射（使用固定映射）
        Console.WriteLine("  加载部门映射...");
        foreach (var kvp in TeamMapping)
        {
            DepartmentIdMap[kvp.Key] = kvp.Value;
        }
        Console.WriteLine($"    → {DepartmentIdMap.Count} 条固定映射");
    }
    
    static void ParseOldBusinessPartners(string content)
    {
        var pattern = new Regex(@"\((\d+),172,'[^']*','[^']*','[^']*',\d+,\d+,'[^']*','([^']*)'");
        // 格式: (ID,CompanyID,...,'Title',...)
        // 需要更精确的解析
        var insertMatch = Regex.Match(content, @"INSERT INTO `businesspartners` VALUES (.+);", RegexOptions.Singleline);
        if (!insertMatch.Success) return;
        
        var valuesStr = insertMatch.Groups[1].Value;
        var records = ParseInsertValues(valuesStr);
        
        foreach (var fields in records)
        {
            if (fields.Count < 8) continue;
            var companyId = fields[1];
            if (companyId != SourceCompanyId) continue;
            
            var id = fields[0];
            var title = fields[6]; // Title字段
            if (!string.IsNullOrWhiteSpace(title))
            {
                OldPartnerNames[id] = title;
            }
        }
    }
    
    static void ParseOldEmployees(string content)
    {
        var insertMatch = Regex.Match(content, @"INSERT INTO `employees` VALUES (.+);", RegexOptions.Singleline);
        if (!insertMatch.Success) return;
        
        var valuesStr = insertMatch.Groups[1].Value;
        var records = ParseInsertValues(valuesStr);
        
        foreach (var fields in records)
        {
            if (fields.Count < 14) continue;
            var companyId = fields[1];
            if (companyId != SourceCompanyId) continue;
            
            var id = fields[0];
            // 老系统员工表结构:
            // 0:ID, 1:CompanyID, 2-5:timestamps/userids, 6:EmployeeNo
            // 7:Furigana_FirstName, 8:Furigana_LastName
            // 9:PinYin_FirstName, 10:PinYin_LastName
            // 11:FirstName(名), 12:LastName(姓), 13:CondensedName(全名)
            var firstName = fields[11]; // 名
            var lastName = fields[12];  // 姓
            var condensedName = fields.Count > 13 ? fields[13] : "";
            
            // 新系统格式是 "姓 名" (带空格)
            var fullName = $"{firstName} {lastName}".Trim();
            
            if (!string.IsNullOrWhiteSpace(fullName))
            {
                OldEmployeeNames[id] = fullName;
                // 也添加不带空格的版本以便匹配
                if (!string.IsNullOrWhiteSpace(condensedName))
                {
                    OldEmployeeNames[$"{id}_condensed"] = condensedName;
                }
            }
        }
    }
    
    static void ParseOldAssets(string content)
    {
        var insertMatch = Regex.Match(content, @"INSERT INTO `assets` VALUES (.+);", RegexOptions.Singleline);
        if (!insertMatch.Success) return;
        
        var valuesStr = insertMatch.Groups[1].Value;
        var records = ParseInsertValues(valuesStr);
        
        foreach (var fields in records)
        {
            if (fields.Count < 8) continue;
            var companyId = fields[1];
            if (companyId != SourceCompanyId) continue;
            
            var id = fields[0];
            var title = fields[6]; // Title字段
            if (!string.IsNullOrWhiteSpace(title))
            {
                OldAssetNames[id] = title;
            }
        }
    }
    
    #endregion

    #region 解析凭证
    
    static List<VoucherHeader> ParseVoucherHeaders(string content)
    {
        var result = new List<VoucherHeader>();
        
        // 匹配所有INSERT语句
        var insertMatches = Regex.Matches(content, @"INSERT INTO `acdocheaders` VALUES (.+?);", RegexOptions.Singleline);
        
        foreach (Match insertMatch in insertMatches)
        {
            var valuesStr = insertMatch.Groups[1].Value;
            var records = ParseInsertValues(valuesStr);
            
            foreach (var fields in records)
            {
                if (fields.Count < 15) continue;
                var companyId = fields[0];
                if (companyId != SourceCompanyId) continue;
                
                var header = new VoucherHeader
                {
                    DocNo = fields[1],
                    DocType = fields[2],
                    PostingDate = ParseDateTime(fields[3]),
                    Year = int.TryParse(fields[4], out var y) ? y : 0,
                    Month = int.TryParse(fields[5], out var m) ? m : 0,
                    InvoiceNo = fields[6],
                    Currency = fields[7] == "0" ? "JPY" : fields[7],
                    ExchangeRate = decimal.TryParse(fields[8], NumberStyles.Any, CultureInfo.InvariantCulture, out var er) ? er : 1m,
                    Comment = fields[9],
                    OriginType = fields[10],
                    OriginDoc = fields[11],
                    ReverseDoc = fields[12],
                    Posted = fields[13] == "1",
                    AttachedFiles = fields[14]
                };
                
                result.Add(header);
            }
        }
        
        return result;
    }
    
    static List<VoucherItem> ParseVoucherItems(string content)
    {
        var result = new List<VoucherItem>();
        
        var insertMatches = Regex.Matches(content, @"INSERT INTO `acdocitems` VALUES (.+?);", RegexOptions.Singleline);
        
        foreach (Match insertMatch in insertMatches)
        {
            var valuesStr = insertMatch.Groups[1].Value;
            var records = ParseInsertValues(valuesStr);
            
            foreach (var fields in records)
            {
                // acdocitems has 27 columns in dump:
                // CompanyID, DocNo, ItemNo, Year, Month, DRCR, AccountID, Amount, Currency, LocalAmount, TaxRate, Comment,
                // CustomerID, VendorID, TeamID, EmployeeID, AssetID, PaymentTermID, DueDate,
                // ClearStatus, ClearDate, ClearDocNo, ClearItemNo, UpdateTime, CreateTime, CreateUserID, UpdateUserID
                if (fields.Count < 27) continue;
                var companyId = fields[0];
                if (companyId != SourceCompanyId) continue;
                
                var item = new VoucherItem
                {
                    DocNo = fields[1],
                    ItemNo = int.TryParse(fields[2], out var itemNo) ? itemNo : 0,
                    Year = int.TryParse(fields[3], out var y) ? y : 0,
                    Month = int.TryParse(fields[4], out var m) ? m : 0,
                    DRCR = fields[5] == "1" ? "DR" : "CR",
                    AccountID = fields[6],
                    Amount = decimal.TryParse(fields[7], NumberStyles.Any, CultureInfo.InvariantCulture, out var amt) ? amt : 0,
                    Currency = fields[8],
                    LocalAmount = decimal.TryParse(fields[9], NumberStyles.Any, CultureInfo.InvariantCulture, out var lamt) ? lamt : 0,
                    TaxRate = int.TryParse(fields[10], out var tr) ? tr : 0,
                    Comment = fields[11],
                    CustomerID = fields[12],
                    VendorID = fields[13],
                    TeamID = fields[14],
                    EmployeeID = fields[15],
                    AssetID = fields[16],
                    PaymentTermID = fields[17],
                    DueDate = ParseDateTime(fields[18]),
                    ClearStatus = int.TryParse(fields[19], out var cs) ? cs : 0,
                    ClearDate = ParseDateTime(fields[20]),
                    ClearDocNo = fields[21],
                    ClearItemNo = int.TryParse(fields[22], out var ci) ? ci : 0
                };
                
                result.Add(item);
            }
        }
        
        return result;
    }
    
    #endregion

    #region 组装凭证
    
    static List<Voucher> AssembleVouchers(List<VoucherHeader> headers, List<VoucherItem> items)
    {
        var result = new List<Voucher>();
        var itemsByDocNo = items.GroupBy(i => i.DocNo).ToDictionary(g => g.Key, g => g.ToList());
        
        foreach (var header in headers)
        {
            totalHeaders++;
            
            if (!itemsByDocNo.TryGetValue(header.DocNo, out var docItems))
            {
                docItems = new List<VoucherItem>();
            }
            
            // 检查借贷平衡
            var debitTotal = docItems.Where(i => i.DRCR == "DR").Sum(i => i.Amount);
            var creditTotal = docItems.Where(i => i.DRCR == "CR").Sum(i => i.Amount);
            var diff = Math.Abs(debitTotal - creditTotal);
            
            if (diff > 0.01m)
            {
                UnbalancedVouchers.Add(new UnbalancedVoucher
                {
                    DocNo = header.DocNo,
                    DebitTotal = debitTotal,
                    CreditTotal = creditTotal,
                    Difference = diff
                });
                // 不平衡凭证也导入，但标记
            }
            
            var voucher = new Voucher
            {
                Header = header,
                Items = docItems,
                IsBalanced = diff <= 0.01m
            };
            
            result.Add(voucher);
        }
        
        return result;
    }
    
    #endregion

    #region 导入凭证
    
    static async Task ImportVouchers(NpgsqlDataSource ds, List<Voucher> vouchers)
    {
        await using var conn = await ds.OpenConnectionAsync();
        
        // 初始化Azure Blob客户端
        var blobServiceClient = new BlobServiceClient(AzureConnectionString);
        var sourceContainerClient = blobServiceClient.GetBlobContainerClient(SourceContainer);
        var targetContainerClient = blobServiceClient.GetBlobContainerClient(TargetContainer);
        
        int count = 0;
        foreach (var voucher in vouchers)
        {
            count++;
            if (count % 100 == 0)
            {
                Console.WriteLine($"  处理中... {count}/{vouchers.Count}");
            }
            
            try
            {
                // 检查凭证是否已存在
                await using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT id FROM vouchers WHERE company_code = $1 AND voucher_no = $2";
                checkCmd.Parameters.AddWithValue(TargetCompanyCode);
                checkCmd.Parameters.AddWithValue(voucher.Header.DocNo);
                var existingId = await checkCmd.ExecuteScalarAsync();
                
                if (existingId != null)
                {
                    skippedHeaders++;
                    continue;
                }
                
                // 构建payload
                var payload = await BuildPayload(voucher, sourceContainerClient, targetContainerClient);
                
                // 插入凭证
                await using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = "INSERT INTO vouchers(id, company_code, payload) VALUES ($1, $2, $3::jsonb)";
                insertCmd.Parameters.AddWithValue(Guid.NewGuid());
                insertCmd.Parameters.AddWithValue(TargetCompanyCode);
                insertCmd.Parameters.AddWithValue(JsonSerializer.Serialize(payload));
                await insertCmd.ExecuteNonQueryAsync();
                
                importedHeaders++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ 凭证 {voucher.Header.DocNo} 导入失败: {ex.Message}");
                skippedHeaders++;
            }
        }
    }
    
    static async Task<Dictionary<string, object?>> BuildPayload(Voucher voucher, BlobContainerClient sourceContainer, BlobContainerClient targetContainer)
    {
        var header = new Dictionary<string, object?>
        {
            ["companyCode"] = TargetCompanyCode,
            ["voucherNo"] = voucher.Header.DocNo,
            ["voucherType"] = voucher.Header.DocType,
            ["postingDate"] = voucher.Header.PostingDate?.ToString("yyyy-MM-dd"),
            ["currency"] = voucher.Header.Currency == "0" ? "JPY" : voucher.Header.Currency,
            ["summary"] = voucher.Header.Comment,
            ["invoiceRegistrationNo"] = string.IsNullOrWhiteSpace(voucher.Header.InvoiceNo) ? null : voucher.Header.InvoiceNo
        };
        
        var lines = new List<Dictionary<string, object?>>();
        int lineNo = 0;
        
        foreach (var item in voucher.Items.OrderBy(i => i.ItemNo))
        {
            lineNo++;
            var line = new Dictionary<string, object?>
            {
                ["lineNo"] = lineNo,
                ["accountCode"] = TransformAccountCode(item.AccountID, item.Comment),
                ["drcr"] = item.DRCR,
                ["amount"] = item.Amount,
                ["note"] = item.Comment
            };
            
            // 转换CustomerID (取引先)
            if (item.CustomerID != "0" && PartnerIdMap.TryGetValue(item.CustomerID, out var custId))
            {
                line["customerId"] = custId.ToString();
            }
            
            // 转换VendorID (取引先)
            if (item.VendorID != "0" && PartnerIdMap.TryGetValue(item.VendorID, out var vendId))
            {
                line["vendorId"] = vendId.ToString();
            }
            
            // 转换EmployeeID
            if (item.EmployeeID != "0" && EmployeeIdMap.TryGetValue(item.EmployeeID, out var empId))
            {
                line["employeeId"] = empId.ToString();
            }
            
            // 转换TeamID -> DepartmentID
            if (item.TeamID != "0" && DepartmentIdMap.TryGetValue(item.TeamID, out var deptId))
            {
                line["departmentId"] = deptId.ToString();
            }
            
            // 转换AssetID
            if (item.AssetID != "0" && AssetIdMap.TryGetValue(item.AssetID, out var assetId))
            {
                line["assetId"] = assetId.ToString();
            }
            
            // 支付日期
            if (item.DueDate.HasValue)
            {
                line["paymentDate"] = item.DueDate.Value.ToString("yyyy-MM-dd");
            }

            // 旧系统清账信息（来自 acdocitems 的 Clear* 字段）
            // 用于后续回填 open_items 的 cleared/partial 状态
            if (item.ClearStatus != 0)
            {
                line["clearStatus"] = item.ClearStatus;
                if (item.ClearDate.HasValue)
                {
                    line["clearDate"] = item.ClearDate.Value.ToString("yyyy-MM-dd");
                }
                if (!string.IsNullOrWhiteSpace(item.ClearDocNo) && item.ClearDocNo != "NULL")
                {
                    line["clearDocNo"] = item.ClearDocNo;
                }
                if (item.ClearItemNo > 0)
                {
                    line["clearItemNo"] = item.ClearItemNo;
                }
            }
            
            lines.Add(line);
        }
        
        var payload = new Dictionary<string, object?>
        {
            ["header"] = header,
            ["lines"] = lines
        };
        
        // 处理附件
        if (!string.IsNullOrWhiteSpace(voucher.Header.AttachedFiles) && voucher.Header.AttachedFiles != "[]" && voucher.Header.AttachedFiles != "''")
        {
            var attachments = await CopyAttachments(voucher.Header, sourceContainer, targetContainer);
            if (attachments.Count > 0)
            {
                payload["attachments"] = attachments;
            }
        }
        
        return payload;
    }
    
    static string TransformAccountCode(string oldCode, string comment)
    {
        // 科目318拆分逻辑
        if (oldCode == "318")
        {
            var commentLower = (comment ?? "").ToLower();
            if (commentLower.Contains("社会保険") || commentLower.Contains("健康保険") || commentLower.Contains("介護保険"))
            {
                return "3181";
            }
            if (commentLower.Contains("厚生年金"))
            {
                return "3182";
            }
            if (commentLower.Contains("雇用保険"))
            {
                return "3183";
            }
            if (commentLower.Contains("源泉所得税") || commentLower.Contains("所得税"))
            {
                return "3184";
            }
            // 如果无法判断，检查住民税
            if (commentLower.Contains("住民税"))
            {
                return "318"; // 住民税保持318
            }
            // 默认返回原值
            return "318";
        }
        
        return oldCode;
    }
    
    static async Task<List<Dictionary<string, object?>>> CopyAttachments(VoucherHeader header, BlobContainerClient source, BlobContainerClient target)
    {
        var result = new List<Dictionary<string, object?>>();
        
        try
        {
            // 解析附件JSON
            var attachedFiles = header.AttachedFiles?.Trim();
            if (string.IsNullOrWhiteSpace(attachedFiles) || attachedFiles == "''" || attachedFiles == "[]")
            {
                return result;
            }
            
            // 处理转义
            attachedFiles = attachedFiles.Replace("\\'", "'");
            if (attachedFiles.StartsWith("'") && attachedFiles.EndsWith("'"))
            {
                attachedFiles = attachedFiles[1..^1];
            }
            
            List<string>? paths;
            try
            {
                paths = JsonSerializer.Deserialize<List<string>>(attachedFiles);
            }
            catch
            {
                return result;
            }
            
            if (paths == null || paths.Count == 0) return result;
            
            var postingDate = header.PostingDate ?? DateTime.Now;
            
            foreach (var oldPath in paths)
            {
                try
                {
                    // 获取原文件名
                    var fileName = Path.GetFileName(oldPath);
                    var ext = Path.GetExtension(fileName).ToLowerInvariant();
                    
                    // 新路径格式: JP01/finance/vouchers/yyyy/MM/dd/GUID.ext
                    var newBlobName = $"{TargetCompanyCode}/finance/vouchers/{postingDate:yyyy/MM/dd}/{Guid.NewGuid():N}{ext}";
                    
                    // 从源容器拷贝到目标容器
                    var sourceBlob = source.GetBlobClient(oldPath);
                    var targetBlob = target.GetBlobClient(newBlobName);
                    
                    if (await sourceBlob.ExistsAsync())
                    {
                        await targetBlob.StartCopyFromUriAsync(sourceBlob.Uri);
                        
                        // 等待拷贝完成
                        var props = await targetBlob.GetPropertiesAsync();
                        while (props.Value.CopyStatus == Azure.Storage.Blobs.Models.CopyStatus.Pending)
                        {
                            await Task.Delay(100);
                            props = await targetBlob.GetPropertiesAsync();
                        }
                        
                        if (props.Value.CopyStatus == Azure.Storage.Blobs.Models.CopyStatus.Success)
                        {
                            result.Add(new Dictionary<string, object?>
                            {
                                ["id"] = Guid.NewGuid().ToString(),
                                ["name"] = fileName,
                                ["blobName"] = newBlobName,
                                ["contentType"] = GetContentType(ext),
                                ["size"] = props.Value.ContentLength,
                                ["uploadedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                                ["originalPath"] = oldPath
                            });
                            attachmentsCopied++;
                        }
                        else
                        {
                            attachmentsFailed++;
                        }
                    }
                    else
                    {
                        attachmentsFailed++;
                    }
                }
                catch
                {
                    attachmentsFailed++;
                }
            }
        }
        catch
        {
            // 附件处理失败不影响凭证导入
        }
        
        return result;
    }
    
    static string GetContentType(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".xls" => "application/vnd.ms-excel",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".csv" => "text/csv",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }
    
    #endregion

    #region 辅助方法
    
    static DateTime? ParseDateTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "NULL") return null;
        s = s.Trim('\'');
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        return null;
    }
    
    static List<List<string>> ParseInsertValues(string valuesStr)
    {
        var result = new List<List<string>>();
        var current = new List<string>();
        var sb = new StringBuilder();
        bool inString = false;
        bool escaped = false;
        int parenDepth = 0;

        for (int i = 0; i < valuesStr.Length; i++)
        {
            char c = valuesStr[i];

            if (escaped)
            {
                sb.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '\'' && !inString)
            {
                inString = true;
                continue;
            }

            if (c == '\'' && inString)
            {
                inString = false;
                continue;
            }

            if (inString)
            {
                sb.Append(c);
                continue;
            }

            if (c == '(')
            {
                parenDepth++;
                if (parenDepth == 1)
                {
                    current = new List<string>();
                    sb.Clear();
                }
                continue;
            }

            if (c == ')')
            {
                parenDepth--;
                if (parenDepth == 0)
                {
                    current.Add(sb.ToString());
                    sb.Clear();
                    result.Add(current);
                }
                continue;
            }

            if (c == ',' && parenDepth == 1)
            {
                current.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            if (parenDepth >= 1)
            {
                sb.Append(c);
            }
        }

        return result;
    }
    
    #endregion
}

#region 数据类

class VoucherHeader
{
    public string DocNo { get; set; } = "";
    public string DocType { get; set; } = "";
    public DateTime? PostingDate { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public string InvoiceNo { get; set; } = "";
    public string Currency { get; set; } = "JPY";
    public decimal ExchangeRate { get; set; } = 1m;
    public string Comment { get; set; } = "";
    public string OriginType { get; set; } = "";
    public string OriginDoc { get; set; } = "";
    public string ReverseDoc { get; set; } = "";
    public bool Posted { get; set; }
    public string AttachedFiles { get; set; } = "";
}

class VoucherItem
{
    public string DocNo { get; set; } = "";
    public int ItemNo { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public string DRCR { get; set; } = "";
    public string AccountID { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public decimal LocalAmount { get; set; }
    public int TaxRate { get; set; }
    public string Comment { get; set; } = "";
    public string CustomerID { get; set; } = "";
    public string VendorID { get; set; } = "";
    public string TeamID { get; set; } = "";
    public string EmployeeID { get; set; } = "";
    public string AssetID { get; set; } = "";
    public string PaymentTermID { get; set; } = "";
    public DateTime? DueDate { get; set; }
    public int ClearStatus { get; set; }
    public DateTime? ClearDate { get; set; }
    public string ClearDocNo { get; set; } = "";
    public int ClearItemNo { get; set; }
}

class Voucher
{
    public VoucherHeader Header { get; set; } = new();
    public List<VoucherItem> Items { get; set; } = new();
    public bool IsBalanced { get; set; }
}

class UnbalancedVoucher
{
    public string DocNo { get; set; } = "";
    public decimal DebitTotal { get; set; }
    public decimal CreditTotal { get; set; }
    public decimal Difference { get; set; }
}

#endregion


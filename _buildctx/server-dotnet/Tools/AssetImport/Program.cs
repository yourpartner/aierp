using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

/// <summary>
/// 固定资产数据导入工具
/// 从老系统 MySQL dump 导入到新系统 PostgreSQL
/// 只导入公司 172 的数据到 JP01
/// </summary>
class Program
{
    const string TargetCompanyCode = "JP01";
    const string SourceCompanyId = "172";
    
    // 老系统资产分类ID -> 新系统UUID的映射
    static readonly Dictionary<string, Guid> AssetClassIdMap = new();
    
    // 老系统资产ID -> 新系统UUID的映射
    static readonly Dictionary<string, Guid> AssetIdMap = new();

    static async Task Main(string[] args)
    {
        var dumpDir = @"D:\yanxia\server-dotnet\Dump20251222";
        var connStr = "Host=localhost;Database=postgres;Username=postgres;Password=Hpxdcd2508";

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 固定资产数据导入工具 ===");
        Console.WriteLine($"源公司ID: {SourceCompanyId} → 目标公司: {TargetCompanyCode}");
        Console.WriteLine();

        await using var dataSource = NpgsqlDataSource.Create(connStr);

        // 1. 导入资产分类
        Console.WriteLine("【1】导入资产分类 (asset_classes)...");
        var classesFile = Path.Combine(dumpDir, "yourpartnerdb2_assetclasses.sql");
        var classes = ParseAssetClasses(File.ReadAllText(classesFile, Encoding.UTF8));
        Console.WriteLine($"  解析到 {classes.Count} 条公司 {SourceCompanyId} 的资产分类");
        await ImportAssetClasses(dataSource, classes);

        // 2. 导入固定资产
        Console.WriteLine("\n【2】导入固定资产 (fixed_assets)...");
        var assetsFile = Path.Combine(dumpDir, "yourpartnerdb2_assets.sql");
        var assets = ParseAssets(File.ReadAllText(assetsFile, Encoding.UTF8));
        Console.WriteLine($"  解析到 {assets.Count} 条公司 {SourceCompanyId} 的固定资产");
        await ImportFixedAssets(dataSource, assets);

        // 3. 导入资产交易记录
        Console.WriteLine("\n【3】导入资产交易 (asset_transactions)...");
        var txFile = Path.Combine(dumpDir, "yourpartnerdb2_assettransactions.sql");
        var transactions = ParseAssetTransactions(File.ReadAllText(txFile, Encoding.UTF8));
        Console.WriteLine($"  解析到 {transactions.Count} 条公司 {SourceCompanyId} 的资产交易");
        await ImportAssetTransactions(dataSource, transactions);

        Console.WriteLine("\n=== 导入完成 ===");
    }

    #region 解析资产分类

    static List<AssetClassData> ParseAssetClasses(string sql)
    {
        var result = new List<AssetClassData>();
        
        // 匹配 INSERT INTO `assetclasses` VALUES (...
        var insertMatch = Regex.Match(sql, @"INSERT INTO `assetclasses` VALUES (.+);", RegexOptions.Singleline);
        if (!insertMatch.Success) return result;

        var valuesStr = insertMatch.Groups[1].Value;
        var rows = ParseInsertValues(valuesStr);

        foreach (var row in rows)
        {
            if (row.Count < 13) continue;
            
            // 字段顺序：ID, CompanyID, UpdateTime, CreateUserID, UpdateUserID, CreateTime, 
            //          Title, AcqAccount, DepDrAccount, DepCrAccount, DepwithTax, ScrapAccount, FixedAsset
            var companyId = row[1];
            if (companyId != SourceCompanyId) continue;

            result.Add(new AssetClassData
            {
                Id = row[0],
                Title = row[6],
                AcqAccount = row[7],
                DepDrAccount = row[8],
                DepCrAccount = row[9],
                DepWithTax = row[10] == "1",
                ScrapAccount = row[11],
                FixedAsset = row[12] == "1"
            });
        }

        return result;
    }

    #endregion

    #region 解析固定资产

    static List<AssetData> ParseAssets(string sql)
    {
        var result = new List<AssetData>();
        
        var insertMatch = Regex.Match(sql, @"INSERT INTO `assets` VALUES (.+);", RegexOptions.Singleline);
        if (!insertMatch.Success) return result;

        var valuesStr = insertMatch.Groups[1].Value;
        var rows = ParseInsertValues(valuesStr);

        foreach (var row in rows)
        {
            if (row.Count < 15) continue;
            
            // 字段顺序：ID, CompanyID, UpdateTime, CreateUserID, UpdateUserID, CreateTime,
            //          Title, Class, CapalizeDate, DepFromDate, DepYears, DepMethod, 
            //          CapalizeAmount, BookAmount, Comment
            var companyId = row[1];
            if (companyId != SourceCompanyId) continue;

            result.Add(new AssetData
            {
                Id = row[0],
                Title = row[6],
                ClassId = row[7],
                CapitalizeDate = ParseDateTime(row[8]),
                DepFromDate = ParseDateTime(row[9]),
                DepYears = int.TryParse(row[10], out var y) ? y : 5,
                DepMethod = row[11], // 0=定额法, 1=定率法
                CapitalizeAmount = decimal.TryParse(row[12], out var ca) ? ca : 0,
                BookAmount = decimal.TryParse(row[13], out var ba) ? ba : 0,
                Comment = row[14]
            });
        }

        return result;
    }

    #endregion

    #region 解析资产交易

    static List<AssetTransactionData> ParseAssetTransactions(string sql)
    {
        var result = new List<AssetTransactionData>();
        
        // 文件可能很大，逐行处理
        var lines = sql.Split('\n');
        foreach (var line in lines)
        {
            if (!line.StartsWith("INSERT INTO `assettransactions`")) continue;
            
            var insertMatch = Regex.Match(line, @"INSERT INTO `assettransactions` VALUES (.+);", RegexOptions.Singleline);
            if (!insertMatch.Success) continue;

            var valuesStr = insertMatch.Groups[1].Value;
            var rows = ParseInsertValues(valuesStr);

            foreach (var row in rows)
            {
                if (row.Count < 15) continue;
                
                // 字段顺序：ID, CompanyID, AssetID, TransType, TransDate, Amount, OldAmount,
                //          Comment, DocNo, DepreciationID, CreateUserID, CreateTime, UpdateUserID, UpdateTime, CapTransID
                var companyId = row[1];
                if (companyId != SourceCompanyId) continue;

                result.Add(new AssetTransactionData
                {
                    Id = row[0],
                    AssetId = row[2],
                    TransType = row[3], // 1=取得, 2=折旧, 3=除却 等
                    TransDate = ParseDateTime(row[4]),
                    Amount = decimal.TryParse(row[5], out var a) ? a : 0,
                    OldAmount = decimal.TryParse(row[6], out var oa) ? oa : 0,
                    Comment = row[7],
                    DocNo = row[8],
                    DepreciationId = row[9]
                });
            }
        }

        return result;
    }

    #endregion

    #region 导入资产分类

    static async Task ImportAssetClasses(NpgsqlDataSource ds, List<AssetClassData> classes)
    {
        await using var conn = await ds.OpenConnectionAsync();
        int inserted = 0, skipped = 0;

        foreach (var c in classes)
        {
            // 检查是否已存在（按名称）
            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT id FROM asset_classes WHERE company_code = $1 AND class_name = $2";
            checkCmd.Parameters.AddWithValue(TargetCompanyCode);
            checkCmd.Parameters.AddWithValue(c.Title);
            var existingId = await checkCmd.ExecuteScalarAsync();
            
            if (existingId != null)
            {
                AssetClassIdMap[c.Id] = (Guid)existingId;
                skipped++;
                continue;
            }

            // 插入新记录
            var newId = Guid.NewGuid();
            AssetClassIdMap[c.Id] = newId;

            var payload = new Dictionary<string, object?>
            {
                ["className"] = c.Title,
                ["acquisitionAccount"] = c.AcqAccount,
                ["depreciationExpenseAccount"] = c.DepDrAccount,
                ["accumulatedDepreciationAccount"] = c.DepCrAccount,
                ["disposalAccount"] = c.ScrapAccount,
                ["includeTaxInDepreciation"] = c.DepWithTax,
                ["isTangible"] = c.FixedAsset
            };

            await using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = "INSERT INTO asset_classes(id, company_code, payload) VALUES ($1, $2, $3::jsonb)";
            insertCmd.Parameters.AddWithValue(newId);
            insertCmd.Parameters.AddWithValue(TargetCompanyCode);
            insertCmd.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(payload));
            await insertCmd.ExecuteNonQueryAsync();
            inserted++;
        }

        Console.WriteLine($"  → 插入 {inserted} 条，跳过 {skipped} 条（已存在）");
    }

    #endregion

    #region 导入固定资产

    static async Task ImportFixedAssets(NpgsqlDataSource ds, List<AssetData> assets)
    {
        await using var conn = await ds.OpenConnectionAsync();
        int inserted = 0, skipped = 0;

        // 获取当前最大资产编号
        await using var seqCmd = conn.CreateCommand();
        seqCmd.CommandText = "SELECT COALESCE(MAX(CAST(asset_no AS INTEGER)), 0) FROM fixed_assets WHERE company_code = $1 AND asset_no ~ '^[0-9]+$'";
        seqCmd.Parameters.AddWithValue(TargetCompanyCode);
        var maxNo = (int)(await seqCmd.ExecuteScalarAsync() ?? 0);

        foreach (var a in assets)
        {
            // 检查是否已存在（按名称+取得日期）
            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = @"SELECT id FROM fixed_assets 
                                     WHERE company_code = $1 
                                       AND asset_name = $2 
                                       AND capitalization_date = $3";
            checkCmd.Parameters.AddWithValue(TargetCompanyCode);
            checkCmd.Parameters.AddWithValue(a.Title);
            checkCmd.Parameters.AddWithValue(a.CapitalizeDate.HasValue ? DateOnly.FromDateTime(a.CapitalizeDate.Value) : (DateOnly?)null);
            var existingId = await checkCmd.ExecuteScalarAsync();
            
            if (existingId != null)
            {
                AssetIdMap[a.Id] = (Guid)existingId;
                skipped++;
                continue;
            }

            // 映射资产分类ID
            Guid? assetClassId = null;
            if (AssetClassIdMap.TryGetValue(a.ClassId, out var classGuid))
            {
                assetClassId = classGuid;
            }

            // 生成新资产编号
            maxNo++;
            var assetNo = maxNo.ToString();

            var newId = Guid.NewGuid();
            AssetIdMap[a.Id] = newId;

            // 折旧方法映射：0=定额法, 1=定率法
            var depMethod = a.DepMethod == "1" ? "DECLINING_BALANCE" : "STRAIGHT_LINE";

            var payload = new Dictionary<string, object?>
            {
                ["assetNo"] = assetNo,
                ["assetName"] = a.Title,
                ["assetClassId"] = assetClassId?.ToString(),
                ["capitalizationDate"] = a.CapitalizeDate?.ToString("yyyy-MM-dd"),
                ["depreciationStartDate"] = a.DepFromDate?.ToString("yyyy-MM-dd"),
                ["usefulLife"] = a.DepYears,
                ["depreciationMethod"] = depMethod,
                ["acquisitionCost"] = a.CapitalizeAmount,
                ["bookValue"] = a.BookAmount,
                ["remarks"] = a.Comment
            };

            await using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = "INSERT INTO fixed_assets(id, company_code, payload) VALUES ($1, $2, $3::jsonb)";
            insertCmd.Parameters.AddWithValue(newId);
            insertCmd.Parameters.AddWithValue(TargetCompanyCode);
            insertCmd.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(payload));
            await insertCmd.ExecuteNonQueryAsync();
            inserted++;

            Console.WriteLine($"    资产 {assetNo}: {a.Title} (取得:{a.CapitalizeAmount:N0}, 帐簿:{a.BookAmount:N0})");
        }

        // 更新资产编号序列
        await using var updateSeqCmd = conn.CreateCommand();
        updateSeqCmd.CommandText = @"INSERT INTO asset_sequences(company_code, last_number) 
                                     VALUES ($1, $2) 
                                     ON CONFLICT (company_code) 
                                     DO UPDATE SET last_number = GREATEST(asset_sequences.last_number, $2)";
        updateSeqCmd.Parameters.AddWithValue(TargetCompanyCode);
        updateSeqCmd.Parameters.AddWithValue(maxNo);
        await updateSeqCmd.ExecuteNonQueryAsync();

        Console.WriteLine($"  → 插入 {inserted} 条，跳过 {skipped} 条（已存在）");
    }

    #endregion

    #region 导入资产交易

    static async Task ImportAssetTransactions(NpgsqlDataSource ds, List<AssetTransactionData> transactions)
    {
        await using var conn = await ds.OpenConnectionAsync();
        int inserted = 0, skipped = 0, noAsset = 0;

        foreach (var tx in transactions)
        {
            // 查找对应的资产
            if (!AssetIdMap.TryGetValue(tx.AssetId, out var assetId))
            {
                noAsset++;
                continue;
            }

            // 检查是否已存在（按资产ID+日期+类型+金额）
            var transType = MapTransactionType(tx.TransType);
            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = @"SELECT id FROM asset_transactions 
                                     WHERE company_code = $1 
                                       AND asset_id = $2 
                                       AND transaction_type = $3
                                       AND posting_date = $4
                                       AND amount = $5";
            checkCmd.Parameters.AddWithValue(TargetCompanyCode);
            checkCmd.Parameters.AddWithValue(assetId);
            checkCmd.Parameters.AddWithValue(transType);
            checkCmd.Parameters.AddWithValue(tx.TransDate.HasValue ? DateOnly.FromDateTime(tx.TransDate.Value) : (DateOnly?)null);
            checkCmd.Parameters.AddWithValue(tx.Amount);
            var existingId = await checkCmd.ExecuteScalarAsync();
            
            if (existingId != null)
            {
                skipped++;
                continue;
            }

            var payload = new Dictionary<string, object?>
            {
                ["transactionType"] = transType,
                ["postingDate"] = tx.TransDate?.ToString("yyyy-MM-dd"),
                ["amount"] = tx.Amount,
                ["voucherNo"] = tx.DocNo,
                ["note"] = tx.Comment
            };

            await using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = "INSERT INTO asset_transactions(company_code, asset_id, payload) VALUES ($1, $2, $3::jsonb)";
            insertCmd.Parameters.AddWithValue(TargetCompanyCode);
            insertCmd.Parameters.AddWithValue(assetId);
            insertCmd.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(payload));
            await insertCmd.ExecuteNonQueryAsync();
            inserted++;
        }

        Console.WriteLine($"  → 插入 {inserted} 条，跳过 {skipped} 条（已存在），{noAsset} 条无对应资产");
    }

    static string MapTransactionType(string oldType)
    {
        return oldType switch
        {
            "1" => "ACQUISITION",    // 取得
            "2" => "DEPRECIATION",   // 折旧
            "3" => "DISPOSAL",       // 除却/处置
            "4" => "ADJUSTMENT",     // 调整
            _ => "OTHER"
        };
    }

    #endregion

    #region 辅助方法

    static DateTime? ParseDateTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "NULL") return null;
        // 格式：'2024-01-01 00:00:00.000000'
        s = s.Trim('\'');
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        return null;
    }

    /// <summary>
    /// 解析 MySQL INSERT VALUES 中的多行数据
    /// </summary>
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
                // 检查是否是转义的单引号 ''
                if (i + 1 < valuesStr.Length && valuesStr[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i++;
                    continue;
                }
                inString = false;
                continue;
            }

            if (inString)
            {
                sb.Append(c);
                continue;
            }

            // 不在字符串中
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
                    current.Add(sb.ToString().Trim());
                    sb.Clear();
                    result.Add(current);
                }
                continue;
            }

            if (c == ',' && parenDepth == 1)
            {
                current.Add(sb.ToString().Trim());
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

class AssetClassData
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string AcqAccount { get; set; } = "";
    public string DepDrAccount { get; set; } = "";
    public string DepCrAccount { get; set; } = "";
    public bool DepWithTax { get; set; }
    public string ScrapAccount { get; set; } = "";
    public bool FixedAsset { get; set; } = true;
}

class AssetData
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string ClassId { get; set; } = "";
    public DateTime? CapitalizeDate { get; set; }
    public DateTime? DepFromDate { get; set; }
    public int DepYears { get; set; } = 5;
    public string DepMethod { get; set; } = "0";
    public decimal CapitalizeAmount { get; set; }
    public decimal BookAmount { get; set; }
    public string Comment { get; set; } = "";
}

class AssetTransactionData
{
    public string Id { get; set; } = "";
    public string AssetId { get; set; } = "";
    public string TransType { get; set; } = "";
    public DateTime? TransDate { get; set; }
    public decimal Amount { get; set; }
    public decimal OldAmount { get; set; }
    public string Comment { get; set; } = "";
    public string DocNo { get; set; } = "";
    public string DepreciationId { get; set; } = "";
}

#endregion


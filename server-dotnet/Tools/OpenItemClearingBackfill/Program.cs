using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

/// <summary>
/// 回填 open_items 的清账信息（cleared_flag / cleared_at / refs.clearingVoucherNo 等）
/// v4: 改进版 - 处理"自清账"问题
/// - 正常被清账记录（ClearDocNo != DocNo）：照常处理，设置已清账
/// - 自清账记录（ClearDocNo == DocNo）：这是清账方，记录"我清掉了哪些凭证"
/// </summary>
internal class Program
{
    private const string TargetCompanyCode = "JP01";
    private const string SourceCompanyId = "172";

    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var dumpDir = @"D:\yanxia\server-dotnet\Dump20251222";
        var connStr = BuildConnStringFromPgEnv();

        foreach (var a in args ?? Array.Empty<string>())
        {
            if (a.StartsWith("--dumpDir=", StringComparison.OrdinalIgnoreCase)) dumpDir = a.Substring("--dumpDir=".Length).Trim('"');
            if (a.StartsWith("--conn=", StringComparison.OrdinalIgnoreCase)) connStr = a.Substring("--conn=".Length).Trim('"');
        }

        var itemsFile = Path.Combine(dumpDir, "yourpartnerdb2_acdocitems.sql");
        if (!File.Exists(itemsFile))
        {
            Console.WriteLine($"找不到文件: {itemsFile}");
            return;
        }

        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   OpenItem 清账回填工具 v4 (改进自清账处理)                ║");
        Console.WriteLine("╠════════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  源公司ID: {SourceCompanyId,-10} → 目标公司: {TargetCompanyCode,-20}  ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // 步骤1：解析旧系统数据
        Console.WriteLine("【1】解析旧系统 acdocitems...");
        var content = File.ReadAllText(itemsFile, Encoding.UTF8);
        var allItems = ParseVoucherItems(content);

        // 分类：正常被清账 vs 自清账
        var normalClearItems = allItems
            .Where(i => i.ClearStatus != 0 && !string.IsNullOrWhiteSpace(i.ClearDocNo) && i.ClearDocNo != i.DocNo)
            .ToList();

        var selfClearItems = allItems
            .Where(i => i.ClearStatus != 0 && !string.IsNullOrWhiteSpace(i.ClearDocNo) && i.ClearDocNo == i.DocNo)
            .ToList();

        Console.WriteLine($"  明细总数: {allItems.Count}");
        Console.WriteLine($"  正常被清账明细: {normalClearItems.Count}");
        Console.WriteLine($"  自清账明细（清账方）: {selfClearItems.Count}");

        // 步骤2：建立反向索引（清账凭证 -> 被它清掉的凭证列表）
        Console.WriteLine("\n【2】建立反向索引（清账凭证→被清账凭证）...");
        var reverseIndex = new Dictionary<string, List<ClearedItemInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in normalClearItems)
        {
            if (string.IsNullOrWhiteSpace(item.ClearDocNo)) continue;
            if (!reverseIndex.TryGetValue(item.ClearDocNo, out var list))
            {
                list = new List<ClearedItemInfo>();
                reverseIndex[item.ClearDocNo] = list;
            }
            list.Add(new ClearedItemInfo
            {
                DocNo = item.DocNo,
                ItemNo = item.ItemNo,
                Amount = item.Amount,
                ClearDate = item.ClearDate
            });
        }
        Console.WriteLine($"  清账凭证数: {reverseIndex.Count}");

        // 步骤3：建立 ItemNo → lineNo 映射
        Console.WriteLine("\n【3】建立 ItemNo→lineNo 映射...");
        var itemNoToLineNo = BuildItemNoMapping(allItems);
        Console.WriteLine($"  凭证数: {itemNoToLineNo.Count}");

        // 步骤4：连接数据库
        Console.WriteLine("\n【4】连接数据库...");
        Console.WriteLine($"  连接信息（已隐藏密码）: {RedactPassword(connStr)}");
        PrintConnHints(connStr);
        await using var ds = await CreateDataSourceWithHostFallbackAsync(connStr);

        // 步骤5：加载新系统凭证索引
        Console.WriteLine("\n【5】加载新系统凭证索引...");
        var allDocNos = allItems
            .Where(i => i.ClearStatus != 0 && !string.IsNullOrWhiteSpace(i.ClearDocNo))
            .SelectMany(x => new[] { x.DocNo, x.ClearDocNo })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToArray();

        var voucherIdByNo = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var postingDateByNo = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        await using var conn = await ds.OpenConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT voucher_no, id, posting_date
FROM vouchers
WHERE company_code = $1 AND voucher_no = ANY($2::text[])";
            cmd.Parameters.AddWithValue(TargetCompanyCode);
            cmd.Parameters.AddWithValue(allDocNos);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var no = rd.GetString(0);
                var id = rd.GetGuid(1);
                var pd = rd.GetDateTime(2);
                voucherIdByNo[no] = id;
                postingDateByNo[no] = pd;
            }
        }

        Console.WriteLine($"  需要索引的凭证号数: {allDocNos.Length}");
        Console.WriteLine($"  实际找到凭证数: {voucherIdByNo.Count}");

        // 步骤6：回填正常被清账记录
        Console.WriteLine("\n【6】回填正常被清账记录（设置 cleared_flag = true）...");
        var updated = 0;
        var missingVoucher = 0;
        var missingOpenItem = 0;
        var missingMapping = 0;

        await using var tx = await conn.BeginTransactionAsync();

        // 准备更新已清账的 SQL
        await using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = @"
UPDATE open_items
SET residual_amount = 0,
    cleared_flag = TRUE,
    cleared_at = $4,
    refs = jsonb_set(
             jsonb_set(
               jsonb_set(
                 COALESCE(refs, '{}'::jsonb),
                 '{clearingVoucherNo}', to_jsonb($5::text), true
               ),
               '{clearingLineNo}', to_jsonb($6::int), true
             ),
             '{clearingHistory}',
             jsonb_build_array(
               jsonb_build_object(
                 'at', to_jsonb($4::timestamptz),
                 'amount', original_amount,
                 'clearingVoucherId', to_jsonb($7::text),
                 'clearingVoucherNo', to_jsonb($5::text),
                 'clearingVoucherLineNo', to_jsonb($6::int),
                 'type', 'legacy-import'
               )
             ),
             true
           ),
    updated_at = now()
WHERE company_code = $1 AND voucher_id = $2 AND voucher_line_no = $3";
        upd.Parameters.Add(new NpgsqlParameter { Value = TargetCompanyCode }); // $1
        upd.Parameters.Add(new NpgsqlParameter { Value = Guid.Empty });       // $2
        upd.Parameters.Add(new NpgsqlParameter { Value = 0 });               // $3
        upd.Parameters.Add(new NpgsqlParameter { Value = (object)DBNull.Value }); // $4
        upd.Parameters.Add(new NpgsqlParameter { Value = "" });              // $5
        upd.Parameters.Add(new NpgsqlParameter { Value = 0 });               // $6
        upd.Parameters.Add(new NpgsqlParameter { Value = "" });              // $7

        foreach (var it in normalClearItems)
        {
            if (!voucherIdByNo.TryGetValue(it.DocNo, out var vid))
            {
                missingVoucher++;
                continue;
            }

            if (!itemNoToLineNo.TryGetValue(it.DocNo, out var docMapping) ||
                !docMapping.TryGetValue(it.ItemNo, out var lineNo))
            {
                missingMapping++;
                continue;
            }

            int clearingLineNo = 0;
            if (it.ClearItemNo > 0 &&
                itemNoToLineNo.TryGetValue(it.ClearDocNo, out var clearDocMapping))
            {
                clearDocMapping.TryGetValue(it.ClearItemNo, out clearingLineNo);
            }

            DateTime? clearedAt = it.ClearDate;
            if (!clearedAt.HasValue && postingDateByNo.TryGetValue(it.ClearDocNo, out var pd))
            {
                clearedAt = pd;
            }

            string clearingVoucherId = "";
            if (voucherIdByNo.TryGetValue(it.ClearDocNo, out var clearVid))
            {
                clearingVoucherId = clearVid.ToString();
            }

            upd.Parameters[1].Value = vid;
            upd.Parameters[2].Value = lineNo;
            upd.Parameters[3].Value = clearedAt.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(clearedAt.Value, DateTimeKind.Utc), TimeSpan.Zero)
                : DBNull.Value;
            upd.Parameters[4].Value = it.ClearDocNo ?? "";
            upd.Parameters[5].Value = clearingLineNo;
            upd.Parameters[6].Value = clearingVoucherId;

            var affected = await upd.ExecuteNonQueryAsync();
            if (affected > 0)
                updated += affected;
            else
                missingOpenItem++;
        }

        Console.WriteLine($"  ✓ 更新已清账 open_items: {updated}");
        Console.WriteLine($"  ⚠ 找不到新系统凭证: {missingVoucher}");
        Console.WriteLine($"  ⚠ 找不到行号映射: {missingMapping}");
        Console.WriteLine($"  ⚠ 找不到对应 open_item: {missingOpenItem}");

        // 步骤7：处理自清账记录（清账方，记录它清掉了哪些凭证）
        Console.WriteLine("\n【7】处理自清账记录（记录关联的被清账凭证）...");
        var selfUpdated = 0;
        var selfMissingVoucher = 0;
        var selfMissingOpenItem = 0;
        var selfMissingMapping = 0;

        await using var updSelf = conn.CreateCommand();
        updSelf.Transaction = tx;
        updSelf.CommandText = @"
UPDATE open_items
SET refs = jsonb_set(
             COALESCE(refs, '{}'::jsonb),
             '{clearedItems}',
             $4::jsonb,
             true
           ),
    updated_at = now()
WHERE company_code = $1 AND voucher_id = $2 AND voucher_line_no = $3";
        updSelf.Parameters.Add(new NpgsqlParameter { Value = TargetCompanyCode }); // $1
        updSelf.Parameters.Add(new NpgsqlParameter { Value = Guid.Empty });       // $2
        updSelf.Parameters.Add(new NpgsqlParameter { Value = 0 });               // $3
        updSelf.Parameters.Add(new NpgsqlParameter { Value = "" });              // $4

        foreach (var it in selfClearItems)
        {
            if (!voucherIdByNo.TryGetValue(it.DocNo, out var vid))
            {
                selfMissingVoucher++;
                continue;
            }

            if (!itemNoToLineNo.TryGetValue(it.DocNo, out var docMapping) ||
                !docMapping.TryGetValue(it.ItemNo, out var lineNo))
            {
                selfMissingMapping++;
                continue;
            }

            // 查找这个凭证清掉了哪些凭证
            if (!reverseIndex.TryGetValue(it.DocNo, out var clearedList) || clearedList.Count == 0)
            {
                // 没有找到被清账的凭证，跳过
                continue;
            }

            // 构建 clearedItems 数组
            var clearedItemsJson = new List<object>();
            foreach (var cleared in clearedList)
            {
                int clearedLineNo = 0;
                if (itemNoToLineNo.TryGetValue(cleared.DocNo, out var clearedDocMapping))
                {
                    clearedDocMapping.TryGetValue(cleared.ItemNo, out clearedLineNo);
                }

                string clearedVoucherId = "";
                if (voucherIdByNo.TryGetValue(cleared.DocNo, out var clearedVid))
                {
                    clearedVoucherId = clearedVid.ToString();
                }

                clearedItemsJson.Add(new
                {
                    voucherNo = cleared.DocNo,
                    voucherId = clearedVoucherId,
                    lineNo = clearedLineNo,
                    amount = cleared.Amount,
                    clearedAt = cleared.ClearDate?.ToString("yyyy-MM-ddTHH:mm:ssZ") ?? ""
                });
            }

            updSelf.Parameters[1].Value = vid;
            updSelf.Parameters[2].Value = lineNo;
            updSelf.Parameters[3].Value = JsonSerializer.Serialize(clearedItemsJson);

            var affected = await updSelf.ExecuteNonQueryAsync();
            if (affected > 0)
                selfUpdated += affected;
            else
                selfMissingOpenItem++;
        }

        await tx.CommitAsync();

        Console.WriteLine($"  ✓ 更新清账方 open_items: {selfUpdated}");
        Console.WriteLine($"  ⚠ 找不到新系统凭证: {selfMissingVoucher}");
        Console.WriteLine($"  ⚠ 找不到行号映射: {selfMissingMapping}");
        Console.WriteLine($"  ⚠ 找不到对应 open_item: {selfMissingOpenItem}");

        Console.WriteLine("\n完成。");
    }

    private static Dictionary<string, Dictionary<int, int>> BuildItemNoMapping(List<VoucherItem> items)
    {
        var result = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        var grouped = items.GroupBy(i => i.DocNo, StringComparer.OrdinalIgnoreCase);

        foreach (var g in grouped)
        {
            var docNo = g.Key;
            var sorted = g.OrderBy(x => x.ItemNo).ToList();
            var mapping = new Dictionary<int, int>();
            for (int i = 0; i < sorted.Count; i++)
            {
                mapping[sorted[i].ItemNo] = i + 1;
            }
            result[docNo] = mapping;
        }

        return result;
    }

    private static List<VoucherItem> ParseVoucherItems(string content)
    {
        var result = new List<VoucherItem>();
        var insertMatches = Regex.Matches(content, @"INSERT INTO `acdocitems` VALUES (.+?);", RegexOptions.Singleline);
        foreach (Match insertMatch in insertMatches)
        {
            var valuesStr = insertMatch.Groups[1].Value;
            var records = ParseInsertValues(valuesStr);
            foreach (var fields in records)
            {
                if (fields.Count < 27) continue;
                if (fields[0] != SourceCompanyId) continue;

                var item = new VoucherItem
                {
                    DocNo = NormalizeSqlString(fields[1]),
                    ItemNo = int.TryParse(fields[2], out var itemNo) ? itemNo : 0,
                    Amount = decimal.TryParse(fields[7], NumberStyles.Any, CultureInfo.InvariantCulture, out var amt) ? amt : 0m,
                    ClearStatus = int.TryParse(fields[19], out var cs) ? cs : 0,
                    ClearDate = ParseDateTime(fields[20]),
                    ClearDocNo = NormalizeSqlString(fields[21]),
                    ClearItemNo = int.TryParse(fields[22], out var ci) ? ci : 0
                };
                result.Add(item);
            }
        }
        return result;
    }

    private static DateTime? ParseDateTime(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s == "NULL") return null;
        s = s.Trim('\'');
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return dt;
        if (DateTime.TryParse(s, out dt)) return dt;
        return null;
    }

    private static string NormalizeSqlString(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        s = s.Trim();
        if (s.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
        {
            s = s.Substring(1, s.Length - 2);
        }
        return s.Trim();
    }

    private static List<List<string>> ParseInsertValues(string valuesStr)
    {
        var result = new List<List<string>>();
        var current = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < valuesStr.Length; i++)
        {
            var c = valuesStr[i];

            if (c == '\'' && (i == 0 || valuesStr[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
                sb.Append(c);
                continue;
            }

            if (!inQuotes)
            {
                if (c == '(')
                {
                    current = new List<string>();
                    sb.Clear();
                    continue;
                }
                if (c == ',')
                {
                    current.Add(sb.ToString().Trim());
                    sb.Clear();
                    continue;
                }
                if (c == ')')
                {
                    current.Add(sb.ToString().Trim());
                    sb.Clear();
                    result.Add(current);
                    continue;
                }
            }

            sb.Append(c);
        }

        return result;
    }

    private static string BuildConnStringFromPgEnv()
    {
        var host = Environment.GetEnvironmentVariable("PGHOST");
        var port = Environment.GetEnvironmentVariable("PGPORT");
        var db = Environment.GetEnvironmentVariable("PGDATABASE");
        var user = Environment.GetEnvironmentVariable("PGUSER");
        var pwd = Environment.GetEnvironmentVariable("PGPASSWORD");

        if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";
        if (string.IsNullOrWhiteSpace(port)) port = "5432";
        if (string.IsNullOrWhiteSpace(db)) db = "postgres";
        if (string.IsNullOrWhiteSpace(user)) user = "postgres";
        if (pwd == null) pwd = "";

        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = int.TryParse(port, out var p) ? p : 5432,
            Database = db,
            Username = user,
            Password = pwd,
            Timeout = 15,
            CommandTimeout = 120
        };
        return csb.ConnectionString;
    }

    private static async Task<NpgsqlDataSource> CreateDataSourceWithHostFallbackAsync(string connStr)
    {
        var baseBuilder = new NpgsqlConnectionStringBuilder(connStr);
        var originalHost = baseBuilder.Host?.Trim();

        var candidates = new List<string>();
        void AddHost(string? h)
        {
            if (string.IsNullOrWhiteSpace(h)) return;
            h = h.Trim();
            if (!candidates.Contains(h, StringComparer.OrdinalIgnoreCase)) candidates.Add(h);
        }

        AddHost(originalHost);
        AddHost("127.0.0.1");
        AddHost("localhost");
        AddHost("::1");

        var lastEx = (Exception?)null;
        foreach (var host in candidates)
        {
            try
            {
                var csb = new NpgsqlConnectionStringBuilder(connStr) { Host = host };
                var ds = NpgsqlDataSource.Create(csb.ConnectionString);
                await using var conn = await ds.OpenConnectionAsync();

                if (!string.Equals(host, originalHost, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  ⚠ 已自动改用 Host={host} 连接成功");
                }
                return ds;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Console.WriteLine($"  ✗ 连接失败: Host={host}, Port={baseBuilder.Port}, Db={baseBuilder.Database}, User={baseBuilder.Username}");
                PrintExceptionDigest(ex);
            }
        }

        Console.WriteLine("连接数据库失败：已尝试多个 Host 仍无法连接。");
        if (lastEx != null) Console.WriteLine(lastEx.ToString());
        throw lastEx ?? new Exception("Unknown connection failure");
    }

    private static string RedactPassword(string connStr)
    {
        if (string.IsNullOrWhiteSpace(connStr)) return connStr;
        return Regex.Replace(connStr, @"(?i)\b(Password|Pwd)\s*=\s*([^;]*)", "$1=***");
    }

    private static void PrintConnHints(string connStr)
    {
        try
        {
            var csb = new NpgsqlConnectionStringBuilder(connStr);
            var hasPwd = !string.IsNullOrEmpty(csb.Password);
            Console.WriteLine($"  解析到: Host={csb.Host}, Port={csb.Port}, Database={csb.Database}, Username={csb.Username}, Password={(hasPwd ? "已提供" : "未提供")}");
            if (!hasPwd)
            {
                Console.WriteLine("  ⚠ 未提供密码：请在 PowerShell 里先设置 $env:PGPASSWORD，或通过 --conn= 传入 Password=...");
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void PrintExceptionDigest(Exception ex)
    {
        if (ex is PostgresException pg)
        {
            Console.WriteLine($"    PostgresException: SqlState={pg.SqlState}, Message={pg.MessageText}");
            return;
        }

        if (ex is NpgsqlException npg && npg.InnerException != null)
        {
            Console.WriteLine($"    NpgsqlException: {npg.Message}");
            Console.WriteLine($"    Inner: {npg.InnerException.GetType().Name}: {npg.InnerException.Message}");
            return;
        }

        Console.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException != null)
        {
            Console.WriteLine($"    Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
    }

    private sealed class VoucherItem
    {
        public string DocNo { get; set; } = "";
        public int ItemNo { get; set; }
        public decimal Amount { get; set; }
        public int ClearStatus { get; set; }
        public DateTime? ClearDate { get; set; }
        public string ClearDocNo { get; set; } = "";
        public int ClearItemNo { get; set; }
    }

    private sealed class ClearedItemInfo
    {
        public string DocNo { get; set; } = "";
        public int ItemNo { get; set; }
        public decimal Amount { get; set; }
        public DateTime? ClearDate { get; set; }
    }
}

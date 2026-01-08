using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var dumpPath = @"D:\yanxia\server-dotnet\Dump20251222";
var connectionString = "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=Hpxdcd2508";

// 读取员工数据
var employeesFile = Path.Combine(dumpPath, "yourpartnerdb2_employees.sql");
var contractsFile = Path.Combine(dumpPath, "yourpartnerdb2_employeecontracts.sql");
var bankAccountsFile = Path.Combine(dumpPath, "yourpartnerdb2_employeebankaccounts.sql");
var hireInsuranceFile = Path.Combine(dumpPath, "yourpartnerdb2_employeehireinsurances.sql");
var endowFile = Path.Combine(dumpPath, "yourpartnerdb2_employeeendows.sql");

Console.WriteLine("=== 开始导入员工数据 ===\n");

// 解析员工基本信息
var employees = ParseEmployees(employeesFile);
Console.WriteLine($"解析到 {employees.Count} 条员工记录");

// 解析雇佣契约
var contracts = ParseContracts(contractsFile);
Console.WriteLine($"解析到 {contracts.Count} 条雇佣契约记录");

// 解析银行账户
var bankAccounts = ParseBankAccounts(bankAccountsFile);
Console.WriteLine($"解析到 {bankAccounts.Count} 条银行账户记录");

// 解析雇佣保险
var hireInsurances = ParseHireInsurances(hireInsuranceFile);
Console.WriteLine($"解析到 {hireInsurances.Count} 条雇佣保险记录");

// 解析厚生年金
var endows = ParseEndows(endowFile);
Console.WriteLine($"解析到 {endows.Count} 条厚生年金记录");

// 组装完整的员工数据
var fullEmployees = new List<JsonObject>();
foreach (var emp in employees.Where(e => e["CompanyID"]?.ToString() == "172"))
{
    var empId = emp["ID"]?.ToString() ?? "";
    
    // 基本信息
    var payload = new JsonObject
    {
        ["code"] = $"E{empId.PadLeft(4, '0')}",
        ["nameKanji"] = $"{emp["LastName"]} {emp["FirstName"]}",
        ["nameKana"] = $"{emp["Furigana_LastName"]} {emp["Furigana_FirstName"]}",
        ["gender"] = emp["Sex"]?.ToString() == "0" ? "M" : "F",
        ["birthDate"] = ParseDate(emp["Birthday"]?.ToString()),
        ["nationality"] = ParseNationality(emp["Nationality"]?.ToString()),
        ["arriveJPDate"] = ParseDate(emp["ArriveJPDate"]?.ToString()),
        ["myNumber"] = emp["MyNumber"]?.ToString(),
        ["taxNo"] = emp["TaxNo"]?.ToString()
    };
    
    // 联系方式
    payload["contact"] = new JsonObject
    {
        ["phone"] = emp["Phone"]?.ToString(),
        ["email"] = emp["Mail"]?.ToString(),
        ["postalCode"] = emp["Postal"]?.ToString(),
        ["address"] = emp["Address"]?.ToString()
    };
    
    // 雇佣契约
    var empContracts = contracts.Where(c => c["EmployeeID"]?.ToString() == empId && c["CompanyID"]?.ToString() == "172").ToList();
    var contractsArray = new JsonArray();
    foreach (var c in empContracts)
    {
        contractsArray.Add(new JsonObject
        {
            ["employmentTypeCode"] = ParseEmploymentType(c["EmployeeTypeID"]?.ToString()),
            ["periodFrom"] = ParseDate(c["FromDate"]?.ToString()),
            ["periodTo"] = ParseDate(c["ToDate"]?.ToString()),
            ["note"] = c["Comment"]?.ToString()
        });
    }
    payload["contracts"] = contractsArray;
    
    // 银行账户
    var empBankAccounts = bankAccounts.Where(b => b["EmployeeID"]?.ToString() == empId && b["CompanyID"]?.ToString() == "172").ToList();
    var bankArray = new JsonArray();
    foreach (var b in empBankAccounts)
    {
        bankArray.Add(new JsonObject
        {
            ["bankCode"] = b["BankCode"]?.ToString(),
            ["branchCode"] = b["BranchCode"]?.ToString(),
            ["accountType"] = b["AccountType"]?.ToString() == "1" ? "ordinary" : "checking",
            ["accountNo"] = b["AccountNo"]?.ToString(),
            ["holder"] = b["AccountHolder"]?.ToString()
        });
    }
    payload["bankAccounts"] = bankArray;
    
    // 社会保险信息
    var empHireIns = hireInsurances.FirstOrDefault(h => h["EmployeeID"]?.ToString() == empId && h["CompanyID"]?.ToString() == "172");
    var empEndow = endows.FirstOrDefault(e => e["EmployeeID"]?.ToString() == empId && e["CompanyID"]?.ToString() == "172");
    
    payload["insurance"] = new JsonObject
    {
        ["hireInsuranceNo"] = empHireIns?["InsuranceNo"]?.ToString(),
        ["endowNo"] = empEndow?["EndowNo"]?.ToString(),
        ["healthNo"] = empEndow?["HealthNo"]?.ToString(),
        ["endowBaseNo"] = empEndow?["BaseNo"]?.ToString(),
        ["joinDate"] = ParseDate(empHireIns?["JoinDate"]?.ToString() ?? empEndow?["JoinDate"]?.ToString()),
        ["quitDate"] = ParseDate(empHireIns?["QuitDate"]?.ToString() ?? empEndow?["QuitDate"]?.ToString())
    };
    
    // 空数组
    payload["departments"] = new JsonArray();
    payload["emergencies"] = new JsonArray();
    payload["attachments"] = new JsonArray();
    
    fullEmployees.Add(new JsonObject
    {
        ["id"] = empId,
        ["code"] = payload["code"]?.ToString(),
        ["payload"] = payload
    });
}

Console.WriteLine($"\n组装完成 {fullEmployees.Count} 条员工数据\n");

// 导入到 PostgreSQL
Console.WriteLine("开始导入到 PostgreSQL...\n");

await using var conn = new NpgsqlConnection(connectionString);
await conn.OpenAsync();

// 先清空
await using (var cmd = new NpgsqlCommand("DELETE FROM employees WHERE company_code = 'JP01'", conn))
{
    var deleted = await cmd.ExecuteNonQueryAsync();
    Console.WriteLine($"清空现有数据: {deleted} 条");
}

// 插入新数据 (employee_code 是生成列，从 payload->>'code' 自动提取)
var inserted = 0;
foreach (var emp in fullEmployees)
{
    var code = emp["code"]?.ToString() ?? "";
    var payloadJson = emp["payload"]?.ToJsonString() ?? "{}";
    
    try
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO employees (company_code, payload) VALUES (@cc, @payload::jsonb)",
            conn);
        cmd.Parameters.AddWithValue("cc", "JP01");
        cmd.Parameters.AddWithValue("payload", payloadJson);
        
        await cmd.ExecuteNonQueryAsync();
        inserted++;
        
        var name = emp["payload"]?["nameKanji"]?.ToString() ?? "";
        Console.WriteLine($"  ✓ 导入: {code} - {name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗ 导入失败: {code} - {ex.Message}");
    }
}

Console.WriteLine($"\n✅ 完成! 共导入 {inserted} 条员工数据.");

// ========== 辅助函数 ==========

List<Dictionary<string, string?>> ParseEmployees(string file)
{
    var content = File.ReadAllText(file);
    var match = Regex.Match(content, @"INSERT INTO `employees` VALUES (.+?);", RegexOptions.Singleline);
    if (!match.Success) return new();
    
    var valuesStr = match.Groups[1].Value;
    return ParseInsertValues(valuesStr, new[]
    {
        "ID", "CompanyID", "UpdateTime", "CreateUserID", "UpdateUserID", "CreateTime",
        "EmployeeNo", "Furigana_FirstName", "Furigana_LastName", "PinYin_FirstName", "PinYin_LastName",
        "FirstName", "LastName", "CondensedName", "Sex", "Birthday", "Nationality", "ArriveJPDate",
        "StartWorkDate", "MyNumber", "TaxNo", "Phone", "Mail", "Postal", "Address", "Station",
        "LiveCity", "BaseSalaryForOverTime", "EmployeeBankAccountID", "EmployeeHireInsuranceID",
        "EmployeeEndowID", "MainSkill", "VendorID", "Comment", "SealImage", "LatestContractToDate",
        "LatestType", "Attachments"
    });
}

List<Dictionary<string, string?>> ParseContracts(string file)
{
    var content = File.ReadAllText(file);
    var match = Regex.Match(content, @"INSERT INTO `employeecontracts` VALUES (.+?);", RegexOptions.Singleline);
    if (!match.Success) return new();
    
    var valuesStr = match.Groups[1].Value;
    // 正确的字段顺序: ID, CompanyID, UpdateTime, CreateUserID, UpdateUserID, CreateTime, EmployeeID, EmployeeTypeID, FromDate, ToDate, ContractAutoRenew, Comment
    return ParseInsertValues(valuesStr, new[]
    {
        "ID", "CompanyID", "UpdateTime", "CreateUserID", "UpdateUserID", "CreateTime",
        "EmployeeID", "EmployeeTypeID", "FromDate", "ToDate", "ContractAutoRenew", "Comment"
    });
}

List<Dictionary<string, string?>> ParseBankAccounts(string file)
{
    var content = File.ReadAllText(file);
    var match = Regex.Match(content, @"INSERT INTO `employeebankaccounts` VALUES (.+?);", RegexOptions.Singleline);
    if (!match.Success) return new();
    
    var valuesStr = match.Groups[1].Value;
    // 正确的字段顺序: ID, CompanyID, UpdateTime, CreateUserID, UpdateUserID, CreateTime, EmployeeID, BankCode, BranchCode, AccountType, AccountNo, AccountHolder
    return ParseInsertValues(valuesStr, new[]
    {
        "ID", "CompanyID", "UpdateTime", "CreateUserID", "UpdateUserID", "CreateTime",
        "EmployeeID", "BankCode", "BranchCode", "AccountType", "AccountNo", "AccountHolder"
    });
}

List<Dictionary<string, string?>> ParseHireInsurances(string file)
{
    var content = File.ReadAllText(file);
    var match = Regex.Match(content, @"INSERT INTO `employeehireinsurances` VALUES (.+?);", RegexOptions.Singleline);
    if (!match.Success) return new();
    
    var valuesStr = match.Groups[1].Value;
    // 正确的字段顺序: ID, CompanyID, UpdateTime, CreateUserID, UpdateUserID, CreateTime, EmployeeID, InsuranceNo, JoinDate, QuitDate
    return ParseInsertValues(valuesStr, new[]
    {
        "ID", "CompanyID", "UpdateTime", "CreateUserID", "UpdateUserID", "CreateTime",
        "EmployeeID", "InsuranceNo", "JoinDate", "QuitDate"
    });
}

List<Dictionary<string, string?>> ParseEndows(string file)
{
    var content = File.ReadAllText(file);
    var match = Regex.Match(content, @"INSERT INTO `employeeendows` VALUES (.+?);", RegexOptions.Singleline);
    if (!match.Success) return new();
    
    var valuesStr = match.Groups[1].Value;
    return ParseInsertValues(valuesStr, new[]
    {
        "ID", "CompanyID", "EmployeeID", "EndowNo", "HealthNo", "BaseNo", "JoinDate", "QuitDate",
        "UpdateTime", "CreateUserID", "UpdateUserID", "CreateTime"
    });
}

List<Dictionary<string, string?>> ParseInsertValues(string valuesStr, string[] columns)
{
    var results = new List<Dictionary<string, string?>>();
    
    // 匹配 (...) 括号内的内容
    var rowMatches = Regex.Matches(valuesStr, @"\(([^()]*(?:\([^()]*\)[^()]*)*)\)");
    
    foreach (Match rowMatch in rowMatches)
    {
        var row = rowMatch.Groups[1].Value;
        var values = ParseRowValues(row);
        
        if (values.Count >= columns.Length)
        {
            var dict = new Dictionary<string, string?>();
            for (int i = 0; i < columns.Length; i++)
            {
                dict[columns[i]] = values[i];
            }
            results.Add(dict);
        }
    }
    
    return results;
}

List<string?> ParseRowValues(string row)
{
    var values = new List<string?>();
    var current = new System.Text.StringBuilder();
    var inString = false;
    var escape = false;
    
    for (int i = 0; i < row.Length; i++)
    {
        var c = row[i];
        
        if (escape)
        {
            current.Append(c);
            escape = false;
            continue;
        }
        
        if (c == '\\')
        {
            escape = true;
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
        
        if (c == ',' && !inString)
        {
            var val = current.ToString().Trim();
            values.Add(val == "NULL" ? null : val);
            current.Clear();
            continue;
        }
        
        current.Append(c);
    }
    
    // 最后一个值
    var lastVal = current.ToString().Trim();
    values.Add(lastVal == "NULL" ? null : lastVal);
    
    return values;
}

string? ParseDate(string? dateStr)
{
    if (string.IsNullOrWhiteSpace(dateStr) || dateStr == "NULL") return null;
    
    // MySQL datetime format: 2024-01-15 00:00:00.000000
    var match = Regex.Match(dateStr, @"(\d{4}-\d{2}-\d{2})");
    if (match.Success) return match.Groups[1].Value;
    
    return null;
}

string? ParseNationality(string? code)
{
    return code switch
    {
        "1" => "JP",
        "2" => "CN",
        "3" => "KR",
        "4" => "TW",
        "5" => "VN",
        _ => "OTHER"
    };
}

string? ParseEmploymentType(string? typeId)
{
    return typeId switch
    {
        "1" => "正社員",
        "2" => "契約社員",
        "3" => "個人事業主",
        "4" => "アルバイト",
        "5" => "役員",
        _ => "契約社員"
    };
}


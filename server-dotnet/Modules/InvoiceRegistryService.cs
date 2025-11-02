using System.Text.RegularExpressions;
using Npgsql;

namespace Server.Modules;

public class InvoiceRegistryService
{
    private static readonly Regex Pattern = new("^T\\d{13}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly NpgsqlDataSource _ds;

    public InvoiceRegistryService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public static string Normalize(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim().ToUpperInvariant();

    public static bool IsFormatValid(string? regNo)
        => !string.IsNullOrWhiteSpace(regNo) && Pattern.IsMatch(regNo.Trim().ToUpperInvariant());

    public static string StatusKey(InvoiceVerificationStatus status) => status switch
    {
        InvoiceVerificationStatus.Matched => "matched",
        InvoiceVerificationStatus.NotFound => "not_found",
        InvoiceVerificationStatus.Inactive => "inactive",
        InvoiceVerificationStatus.Expired => "expired",
        _ => status.ToString().ToLowerInvariant()
    };

    public async Task<InvoiceVerificationResult> VerifyAsync(string regNo)
    {
        var normalized = Normalize(regNo);
        if (!IsFormatValid(normalized))
            throw new ArgumentException("invalid invoice registration number", nameof(regNo));

        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT registration_no, name, name_kana, effective_from, effective_to
                              FROM invoice_issuers
                              WHERE registration_no=$1
                              LIMIT 1";
        cmd.Parameters.AddWithValue(normalized);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            var nameKana = reader.IsDBNull(2) ? null : reader.GetString(2);
            var effectiveFrom = reader.IsDBNull(3) ? (DateOnly?)null : DateOnly.FromDateTime(reader.GetDateTime(3));
            var effectiveTo = reader.IsDBNull(4) ? (DateOnly?)null : DateOnly.FromDateTime(reader.GetDateTime(4));

            var today = DateTime.UtcNow.Date;
            var status = InvoiceVerificationStatus.Matched;
            if (effectiveTo.HasValue && effectiveTo.Value.ToDateTime(TimeOnly.MinValue) < today)
                status = InvoiceVerificationStatus.Expired;
            else if (effectiveFrom.HasValue && effectiveFrom.Value.ToDateTime(TimeOnly.MinValue) > today)
                status = InvoiceVerificationStatus.Inactive;

            return new InvoiceVerificationResult(normalized, status, name, nameKana, effectiveFrom, effectiveTo, DateTimeOffset.UtcNow);
        }

        return new InvoiceVerificationResult(normalized, InvoiceVerificationStatus.NotFound, null, null, null, null, DateTimeOffset.UtcNow);
    }
}

public enum InvoiceVerificationStatus
{
    Matched,
    NotFound,
    Inactive,
    Expired
}

public record InvoiceVerificationResult(
    string RegistrationNo,
    InvoiceVerificationStatus Status,
    string? Name,
    string? NameKana,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    DateTimeOffset CheckedAt
)
{
    public object ToResponse() => new
    {
        registrationNo = RegistrationNo,
        status = InvoiceRegistryService.StatusKey(Status),
        name = Name,
        nameKana = NameKana,
        effectiveFrom = EffectiveFrom?.ToString("yyyy-MM-dd"),
        effectiveTo = EffectiveTo?.ToString("yyyy-MM-dd"),
        checkedAt = CheckedAt.ToString("O")
    };
}


using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace BankReporting.Api;

// 權限與流程狀態列舉：避免散落 magic string，並維持審核流程語意一致。
public enum UserRole { Admin, Supervisor, Clerk, ReadOnly }
public enum AccountStatus { PendingEmailVerification, PendingApproval, Active, Rejected, Disabled, Locked }
public enum SubmissionStatus { Draft, Pending, Rejected, Approved, Submitting, Submitted, Failed }
public enum KeyType { JwePublic, ApiToken }
public enum MfaPolicyScope { Disabled, AdminOnly, AdminAndSupervisor, AllUsers }

public record User(
    Guid Id,
    string Name,
    string Email,
    string InstitutionCode,
    string Department,
    string Title,
    UserRole Role,
    AccountStatus Status,
    string PasswordHash,
    List<string> PasswordHistory,
    DateTimeOffset PasswordChangedAt,
    int FailedLoginCount,
    DateTimeOffset? LockoutEnd,
    bool MfaEnabled,
    string? MfaSecret,
    bool IsAdUser = false);

public record Institution(string Code, string Name, bool Enabled, string? Domain = null, string[]? SupervisorEmails = null);

public record ReportField(string FieldCode, string FieldName, string DataType, bool Required, decimal? MinValue = null, decimal? MaxValue = null, int? MaxLength = null, string? DateFormat = null, string[]? EnumValues = null, string? ValidationRule = null, string? ExcelColumn = null, int SortOrder = 0);

public record ReportVersion(Guid VersionId, string ReportCode, string Version, DateOnly EffectiveDate, string ChangeNote, bool IsPublished, IReadOnlyList<ReportField> Fields, string JsonSchema, string ExcelMapping);
public record ReportDefinition(string ReportCode, string Name, string ReportType, int DeadlineDay, string ApiEndpoint, bool IsEnabled, List<ReportVersion> Versions);

public record ReportSubmission(
    Guid Id,
    string ReportCode,
    string Period,
    string InstitutionCode,
    Guid SubmitterId,
    Guid? ReviewerId,
    string ReportVersion,
    SubmissionStatus Status,
    JsonDocument Payload,
    int RejectCount,
    List<string> RejectReasons,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ApiResponseCode,
    string? ApiResponseMessage);

public record CryptoKey(Guid KeyId, string InstitutionCode, KeyType KeyType, string Version, bool Active, DateTimeOffset? ExpiresAt, string UploadedBy, DateTimeOffset UploadedAt, string EncryptedValue, string? Subject = null, string? Issuer = null, string? Thumbprint = null);
public record Notification(Guid Id, Guid UserId, string Type, string Message, bool Read, DateTimeOffset CreatedAt);
public record AuditLog(Guid Id, DateTimeOffset At, Guid? UserId, string UserName, string Action, string EntityType, string EntityId, string Summary, string Ip);

public record MfaPolicy(
    MfaPolicyScope Scope,
    bool EnforceOnPrivilegedEndpoints,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedBy,
    string? Note)
{
    // 預設不強制 MFA，避免新環境升級時直接造成全面登入失敗。
    // 正式上線請由管理端點明確開啟並搭配 session 撤銷策略。
    public static MfaPolicy Default => new(MfaPolicyScope.Disabled, false, DateTimeOffset.UtcNow, null, null);

    public bool RequiresMfa(UserRole role)
    {
        if (!EnforceOnPrivilegedEndpoints) return false;

        return Scope switch
        {
            MfaPolicyScope.Disabled => false,
            MfaPolicyScope.AdminOnly => role == UserRole.Admin,
            MfaPolicyScope.AdminAndSupervisor => role is UserRole.Admin or UserRole.Supervisor,
            MfaPolicyScope.AllUsers => true,
            _ => false
        };
    }
}

public sealed class AppState
{
    // 目前為單程序 in-memory 狀態容器，透過 repository 做快照持久化。
    // 併發使用 Concurrent*；但跨進程一致性需由外部儲存層保證。
    public ConcurrentDictionary<Guid, User> Users { get; } = new();
    public ConcurrentDictionary<string, Institution> Institutions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, ReportDefinition> ReportDefinitions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<Guid, ReportSubmission> Submissions { get; } = new();
    public ConcurrentDictionary<Guid, CryptoKey> Keys { get; } = new();
    public ConcurrentBag<Notification> Notifications { get; private set; } = new();
    public ConcurrentBag<AuditLog> AuditLogs { get; private set; } = new();
    public MfaPolicy MfaPolicy { get; set; } = MfaPolicy.Default;

    public void ResetBags()
    {
        Notifications = new ConcurrentBag<Notification>();
        AuditLogs = new ConcurrentBag<AuditLog>();
    }
}

public static class SecurityHelpers
{
    // 安全工具集中區：密碼、TOTP、靜態加密與匯出格式。
    // 修改此區邏輯時，請同步更新 smoke tests 與相容性說明。
    public static bool IsStrongPassword(string password) =>
        password.Length >= 12 &&
        password.Any(char.IsUpper) &&
        password.Any(char.IsLower) &&
        password.Any(char.IsDigit) &&
        password.Any(ch => !char.IsLetterOrDigit(ch));

    public static string HashPassword(string password)
    {
        // PBKDF2 + per-password salt。
        // 注意：迭代次數調整會影響效能與安全強度，需連同壓測與升級策略一起評估。
        var salt = RandomNumberGenerator.GetBytes(16);
        Span<byte> hash = stackalloc byte[32];
        Rfc2898DeriveBytes.Pbkdf2(password, salt, hash, 100_000, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }

    public static bool VerifyPassword(string password, string hash)
    {
        var parts = hash.Split('.');
        if (parts.Length != 2) return false;
        var salt = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        Span<byte> actual = stackalloc byte[32];
        Rfc2898DeriveBytes.Pbkdf2(password, salt, actual, 100_000, HashAlgorithmName.SHA256);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static string EncryptAtRest(string plain)
    {
        var key = GetMasterKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(plain);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);
        return Convert.ToBase64String(payload);
    }

    public static string DecryptAtRest(string encrypted)
    {
        var key = GetMasterKey();
        var payload = Convert.FromBase64String(encrypted);
        if (payload.Length < 28) throw new CryptographicException("Invalid ciphertext payload.");

        var nonce = payload.AsSpan(0, 12);
        var tag = payload.AsSpan(12, 16);
        var ciphertext = payload.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] GetMasterKey()
    {
        // 金鑰來源只接受環境變數，避免硬編碼或落地在 repo。
        // 部署時請用祕密管理服務注入，並建立輪替/回滾機制。
        var raw = Environment.GetEnvironmentVariable("REPORTING_MASTER_KEY");
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("REPORTING_MASTER_KEY is required for at-rest encryption.");

        try
        {
            var key = Convert.FromBase64String(raw);
            if (key.Length != 32) throw new InvalidOperationException("REPORTING_MASTER_KEY must decode to 32 bytes.");
            return key;
        }
        catch (FormatException)
        {
            var key = Encoding.UTF8.GetBytes(raw);
            if (key.Length != 32) throw new InvalidOperationException("REPORTING_MASTER_KEY must be a 32-byte UTF-8 string or base64(32 bytes).");
            return key;
        }
    }

    public static string BuildCsv(JsonDocument payload)
    {
        if (payload.RootElement.ValueKind != JsonValueKind.Object) return "";
        var props = payload.RootElement.EnumerateObject().ToArray();
        var header = string.Join(',', props.Select(p => p.Name));
        var body = string.Join(',', props.Select(p => p.Value.ToString()?.Replace(',', '，') ?? ""));
        return header + "\n" + body + "\n";
    }

    public static string NewTotpSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(20));

    public static bool VerifyTotp(string base64Secret, string code, int window = 1)
    {
        // window=1 代表容許前後一個 time-step（共約 ±30 秒）時鐘漂移。
        // 若要更嚴格可調小，但會增加使用者驗證失敗機率。
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6) return false;
        var secret = Convert.FromBase64String(base64Secret);
        var timestep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        for (var i = -window; i <= window; i++)
        {
            if (GenerateTotp(secret, timestep + i) == code) return true;
        }
        return false;
    }

    private static string GenerateTotp(byte[] key, long timestep)
    {
        Span<byte> data = stackalloc byte[8];
        var ts = BitConverter.GetBytes(timestep);
        if (BitConverter.IsLittleEndian) Array.Reverse(ts);
        ts.CopyTo(data);

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(data.ToArray());
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                     | ((hash[offset + 1] & 0xff) << 16)
                     | ((hash[offset + 2] & 0xff) << 8)
                     | (hash[offset + 3] & 0xff);
        var otp = binary % 1_000_000;
        return otp.ToString("D6");
    }

    public static byte[] BuildXlsx(JsonDocument payload)
    {
        if (payload.RootElement.ValueKind != JsonValueKind.Object) return Array.Empty<byte>();
        var props = payload.RootElement.EnumerateObject().ToArray();

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            WriteEntry(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            WriteEntry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            WriteEntry(zip, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Submission\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");

            var row1 = string.Join("", props.Select((p, i) => $"<c r=\"{Col(i + 1)}1\" t=\"inlineStr\"><is><t>{XmlEscape(p.Name)}</t></is></c>"));
            var row2 = string.Join("", props.Select((p, i) => $"<c r=\"{Col(i + 1)}2\" t=\"inlineStr\"><is><t>{XmlEscape(p.Value.ToString())}</t></is></c>"));
            var sheet = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\">{row1}</row><row r=\"2\">{row2}</row></sheetData></worksheet>";
            WriteEntry(zip, "xl/worksheets/sheet1.xml", sheet);
        }

        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static string XmlEscape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");

    private static string Col(int index)
    {
        var result = string.Empty;
        while (index > 0)
        {
            index--;
            result = (char)('A' + (index % 26)) + result;
            index /= 26;
        }
        return result;
    }
}

using System.Collections.Concurrent;
using System.Data;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace BankReporting.Api;

// 狀態儲存抽象：目前承載整體快照（含使用者、申報、稽核、session）。
// 若未來改事件溯源/分表儲存，請先確保 SnapshotMapper 可雙向相容。
public interface IStateRepository
{
    void EnsureReady();
    bool TryLoad(AppState state, SessionStore sessions);
    void Save(AppState state, SessionStore sessions);
}

public static class ConfigurationExtensions
{
    public static string? GetString(this IConfiguration cfg, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = cfg[key];
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    public static int? GetInt(this IConfiguration cfg, params string[] keys)
    {
        var raw = cfg.GetString(keys);
        return int.TryParse(raw, out var value) ? value : null;
    }

    public static bool? GetBool(this IConfiguration cfg, params string[] keys)
    {
        var raw = cfg.GetString(keys);
        return bool.TryParse(raw, out var value) ? value : null;
    }
}

public sealed class JwtTokenService
{
    // JWT 僅負責身分聲明與簽章驗證；真正的「可撤銷性」由 SessionStore 補上。
    private readonly byte[] _key;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeSpan _ttl;
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly TokenValidationParameters _validation;

    public JwtTokenService(IConfiguration cfg)
    {
        var secret = cfg.GetString("Security:Jwt:Secret", "JWT_SECRET");
        // 生產環境要求足夠長度的對稱金鑰，避免弱密鑰造成簽章可被暴力破解。
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
            throw new InvalidOperationException("JWT_SECRET must be configured and at least 32 chars for production use.");

        _issuer = cfg.GetString("Security:Jwt:Issuer", "JWT_ISSUER") ?? "bank-reporting";
        _audience = cfg.GetString("Security:Jwt:Audience", "JWT_AUDIENCE") ?? "bank-reporting-web";
        _ttl = TimeSpan.FromMinutes(cfg.GetInt("Security:Jwt:TtlMinutes", "JWT_TTL_MINUTES") ?? 30);
        _key = Encoding.UTF8.GetBytes(secret);

        _validation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidIssuer = _issuer,
            ValidAudience = _audience,
            IssuerSigningKey = new SymmetricSecurityKey(_key)
        };
    }

    public (string Token, DateTimeOffset ExpiresAt, string Jti) Create(User user)
    {
        var now = DateTimeOffset.UtcNow;
        var jti = Guid.NewGuid().ToString("N");
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new("role", user.Role.ToString()),
            new("inst", user.InstitutionCode),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, jti)
        };

        var creds = new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(_issuer, _audience, claims, now.UtcDateTime, now.Add(_ttl).UtcDateTime, creds);
        return (_handler.WriteToken(token), now.Add(_ttl), jti);
    }

    public ClaimsPrincipal? Validate(string token)
    {
        try
        {
            return _handler.ValidateToken(token, _validation, out _);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class SessionStore
{
    // 以 token hash 作為索引，避免明文 token 留在記憶體結構中。
    // 另維護 revoked JTI 清單，處理「token 尚未過期但需即時失效」情境。
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RevokedTokenInfo> _revokedJtis = new(StringComparer.OrdinalIgnoreCase);

    public string TokenHash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    public void Add(string token, Guid userId, DateTimeOffset createdAt, DateTimeOffset expiresAt, string jti)
    {
        _sessions[TokenHash(token)] = new SessionInfo(TokenHash(token), userId, createdAt, expiresAt, jti);
    }

    public bool TryGetUserId(string token, out Guid userId)
    {
        userId = default;
        if (!_sessions.TryGetValue(TokenHash(token), out var session)) return false;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(session.Token, out _);
            return false;
        }

        if (IsJtiRevoked(session.Jti))
        {
            _sessions.TryRemove(session.Token, out _);
            return false;
        }

        userId = session.UserId;
        return true;
    }

    public void RevokeToken(string token, DateTimeOffset revokedAt)
    {
        if (!_sessions.TryRemove(TokenHash(token), out var session)) return;
        _revokedJtis[session.Jti] = new RevokedTokenInfo(session.Jti, session.UserId, revokedAt, session.ExpiresAt);
        CleanupExpiredRevocations();
    }

    public int RevokeUserSessions(Guid userId, DateTimeOffset revokedAt)
    {
        var removed = 0;
        foreach (var session in _sessions.Values.Where(s => s.UserId == userId).ToArray())
        {
            if (_sessions.TryRemove(session.Token, out _))
            {
                _revokedJtis[session.Jti] = new RevokedTokenInfo(session.Jti, session.UserId, revokedAt, session.ExpiresAt);
                removed++;
            }
        }

        CleanupExpiredRevocations();
        return removed;
    }

    public bool IsJtiRevoked(string? jti)
    {
        if (string.IsNullOrWhiteSpace(jti)) return false;
        if (!_revokedJtis.TryGetValue(jti, out var revoked)) return false;
        if (revoked.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _revokedJtis.TryRemove(jti, out _);
            return false;
        }

        return true;
    }

    public IReadOnlyCollection<SessionInfo> Snapshot() => _sessions.Values.ToArray();
    public IReadOnlyCollection<RevokedTokenInfo> RevokedSnapshot() => _revokedJtis.Values.ToArray();

    public void Restore(IEnumerable<SessionInfo> sessions, IEnumerable<RevokedTokenInfo>? revokedJtis = null)
    {
        _sessions.Clear();
        foreach (var session in sessions.Where(s => s.ExpiresAt > DateTimeOffset.UtcNow))
            _sessions[session.Token] = session;

        _revokedJtis.Clear();
        foreach (var revoked in (revokedJtis ?? Array.Empty<RevokedTokenInfo>()).Where(r => r.ExpiresAt > DateTimeOffset.UtcNow))
            _revokedJtis[revoked.Jti] = revoked;
    }

    private void CleanupExpiredRevocations()
    {
        foreach (var revoked in _revokedJtis.Values.Where(r => r.ExpiresAt <= DateTimeOffset.UtcNow).ToArray())
            _revokedJtis.TryRemove(revoked.Jti, out _);
    }
}

public sealed class JsonStateRepository : IStateRepository
{
    // JSON 持久化適合單機開發/示範；高併發與多實例部署請改用 SQL repository。
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonStateRepository(IConfiguration cfg)
    {
        _path = cfg.GetString("Persistence:Json:FilePath", "PERSISTENCE_FILE") ?? Path.Combine(AppContext.BaseDirectory, "data", "app-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void EnsureReady()
    {
        // no-op for file persistence
    }

    public bool TryLoad(AppState state, SessionStore sessions)
    {
        if (!File.Exists(_path)) return false;
        var text = File.ReadAllText(_path);
        var snapshot = JsonSerializer.Deserialize<AppStateSnapshot>(text, _options);
        if (snapshot is null) return false;

        SnapshotMapper.Apply(snapshot, state, sessions);
        return true;
    }

    public void Save(AppState state, SessionStore sessions)
    {
        var snapshot = SnapshotMapper.Build(state, sessions);
        var json = JsonSerializer.Serialize(snapshot, _options);
        File.WriteAllText(_path, json);
    }
}

public sealed class SqlStateRepository : IStateRepository
{
    // SQL 持久化會在啟動時自動檢查/套用 migration，並對已套用腳本做 checksum 防竄改。
    private static readonly Regex MigrationFilePattern = new("^(?<id>\\d{4})_.*\\.sql$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _connectionString;
    private readonly string _migrationsPath;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public SqlStateRepository(IConfiguration cfg)
    {
        _connectionString = cfg.GetString("ConnectionStrings:Default", "Persistence:SqlServer:ConnectionString", "SQLSERVER_CONNECTION_STRING")
            ?? throw new InvalidOperationException("SQL Server persistence enabled but no connection string provided.");

        _migrationsPath = cfg.GetString("Persistence:SqlServer:MigrationsPath", "SQLSERVER_MIGRATIONS_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "database", "sqlserver");
    }

    public void EnsureReady()
    {
        // 啟動不變量：schema metadata 存在、migration manifest 可讀、DB 與程式版本一致。
        // 任一條件不符直接 fail fast，避免服務在未知 schema 上執行。
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        EnsureMigrationMetadataTables(conn);

        var manifest = ReadMigrationManifest(_migrationsPath);
        if (manifest.Count == 0)
            throw new InvalidOperationException($"No SQL migration scripts found at '{_migrationsPath}'.");

        BootstrapLegacyTracking(conn, manifest);

        var applied = ReadAppliedMigrations(conn);
        GuardForMismatches(applied, manifest);
        ApplyPendingMigrations(conn, applied, manifest);
    }

    public bool TryLoad(AppState state, SessionStore sessions)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();

        using var cmd = new SqlCommand("SELECT Payload FROM dbo.AppStateSnapshots WHERE Id = 1", conn);
        var payload = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(payload)) return false;

        var snapshot = JsonSerializer.Deserialize<AppStateSnapshot>(payload, _options);
        if (snapshot is null) return false;

        SnapshotMapper.Apply(snapshot, state, sessions);
        return true;
    }

    public void Save(AppState state, SessionStore sessions)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();

        var snapshot = SnapshotMapper.Build(state, sessions);
        var payload = JsonSerializer.Serialize(snapshot, _options);

        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        using var cmd = new SqlCommand(@"
MERGE dbo.AppStateSnapshots AS target
USING (SELECT 1 AS Id, @payload AS Payload) AS source
ON target.Id = source.Id
WHEN MATCHED THEN
    UPDATE SET Payload = source.Payload, UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (Id, Payload, UpdatedAt) VALUES (1, source.Payload, SYSUTCDATETIME());", conn, tx);

        cmd.Parameters.AddWithValue("@payload", payload);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static void EnsureMigrationMetadataTables(SqlConnection conn)
    {
        using var cmd = new SqlCommand(@"
IF OBJECT_ID('dbo.SchemaMigrations', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SchemaMigrations(
        MigrationId NVARCHAR(64) NOT NULL CONSTRAINT PK_SchemaMigrations PRIMARY KEY,
        ScriptName NVARCHAR(260) NOT NULL,
        ScriptChecksum NVARCHAR(64) NOT NULL,
        AppliedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_SchemaMigrations_AppliedAt DEFAULT (SYSUTCDATETIME())
    );
END", conn);
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<MigrationScript> ReadMigrationManifest(string migrationsPath)
    {
        if (!Directory.Exists(migrationsPath)) return Array.Empty<MigrationScript>();

        return Directory
            .EnumerateFiles(migrationsPath, "*.sql", System.IO.SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var match = MigrationFilePattern.Match(fileName);
                if (!match.Success)
                    throw new InvalidOperationException($"Invalid migration filename '{fileName}'. Expected format: 0001_description.sql");

                var id = match.Groups["id"].Value;
                var sql = File.ReadAllText(path);
                var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
                return new MigrationScript(id, fileName, sql, checksum);
            })
            .OrderBy(x => x.MigrationId, StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, AppliedMigration> ReadAppliedMigrations(SqlConnection conn)
    {
        var result = new Dictionary<string, AppliedMigration>(StringComparer.Ordinal);
        using var cmd = new SqlCommand("SELECT MigrationId, ScriptName, ScriptChecksum, AppliedAt FROM dbo.SchemaMigrations", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            result[id] = new AppliedMigration(id, reader.GetString(1), reader.GetString(2), reader.GetDateTimeOffset(3));
        }

        return result;
    }

    private static void BootstrapLegacyTracking(SqlConnection conn, IReadOnlyList<MigrationScript> manifest)
    {
        using var countCmd = new SqlCommand("SELECT COUNT(1) FROM dbo.SchemaMigrations", conn);
        var count = (int)(countCmd.ExecuteScalar() ?? 0);
        if (count > 0) return;

        foreach (var script in manifest)
        {
            if (!IsLegacyMigrationAlreadyApplied(conn, script.MigrationId)) continue;
            InsertAppliedMigration(conn, script);
        }
    }

    private static bool IsLegacyMigrationAlreadyApplied(SqlConnection conn, string migrationId)
    {
        return migrationId switch
        {
            "0001" => TableExists(conn, "dbo", "AppStateSnapshots"),
            "0002" => SnapshotRowExists(conn),
            // 0003 must be recorded only after the script actually runs.
            // EnsureMigrationMetadataTables creates dbo.SchemaMigrations up-front,
            // so table existence alone is not evidence that migration 0003 was applied.
            "0003" => false,
            _ => false
        };
    }

    private static bool TableExists(SqlConnection conn, string schema, string table)
    {
        using var cmd = new SqlCommand(@"
SELECT 1
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.name = @table AND s.name = @schema", conn);
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@schema", schema);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool SnapshotRowExists(SqlConnection conn)
    {
        if (!TableExists(conn, "dbo", "AppStateSnapshots")) return false;
        using var cmd = new SqlCommand("SELECT 1 FROM dbo.AppStateSnapshots WHERE Id = 1", conn);
        return cmd.ExecuteScalar() is not null;
    }

    private static void GuardForMismatches(Dictionary<string, AppliedMigration> applied, IReadOnlyList<MigrationScript> manifest)
    {
        // 安全防線：
        // - DB 多了程式碼不存在的 migration -> 可能版本漂移
        // - 已套用 migration checksum 改變 -> 可能腳本遭覆寫
        // 兩者都必須中止啟動並人工介入。
        var manifestById = manifest.ToDictionary(x => x.MigrationId, x => x, StringComparer.Ordinal);

        var unknownApplied = applied.Keys.Where(id => !manifestById.ContainsKey(id)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (unknownApplied.Length > 0)
            throw new InvalidOperationException($"Schema mismatch: DB has applied migrations not present in code: {string.Join(", ", unknownApplied)}.");

        var changedScripts = applied
            .Where(kv => manifestById.TryGetValue(kv.Key, out var script)
                && !string.Equals(kv.Value.ScriptChecksum, script.Checksum, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        if (changedScripts.Length > 0)
            throw new InvalidOperationException($"Schema mismatch: migration script checksum changed after apply: {string.Join(", ", changedScripts)}.");
    }

    private static void ApplyPendingMigrations(SqlConnection conn, Dictionary<string, AppliedMigration> applied, IReadOnlyList<MigrationScript> manifest)
    {
        foreach (var script in manifest)
        {
            if (applied.ContainsKey(script.MigrationId)) continue;

            using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
            ExecuteSqlBatches(conn, tx, script.Sql);

            using (var trackCmd = new SqlCommand(@"
INSERT INTO dbo.SchemaMigrations(MigrationId, ScriptName, ScriptChecksum, AppliedAt)
VALUES (@id, @name, @checksum, SYSUTCDATETIME());", conn, tx))
            {
                trackCmd.Parameters.AddWithValue("@id", script.MigrationId);
                trackCmd.Parameters.AddWithValue("@name", script.FileName);
                trackCmd.Parameters.AddWithValue("@checksum", script.Checksum);
                trackCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    private static void ExecuteSqlBatches(SqlConnection conn, SqlTransaction tx, string sql)
    {
        var batches = Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        foreach (var batch in batches)
        {
            var statement = batch.Trim();
            if (statement.Length == 0) continue;

            using var cmd = new SqlCommand(statement, conn, tx);
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertAppliedMigration(SqlConnection conn, MigrationScript script)
    {
        using var cmd = new SqlCommand(@"
INSERT INTO dbo.SchemaMigrations(MigrationId, ScriptName, ScriptChecksum, AppliedAt)
VALUES (@id, @name, @checksum, SYSUTCDATETIME());", conn);
        cmd.Parameters.AddWithValue("@id", script.MigrationId);
        cmd.Parameters.AddWithValue("@name", script.FileName);
        cmd.Parameters.AddWithValue("@checksum", script.Checksum);
        cmd.ExecuteNonQuery();
    }

    private sealed record MigrationScript(string MigrationId, string FileName, string Sql, string Checksum);
    private sealed record AppliedMigration(string MigrationId, string ScriptName, string ScriptChecksum, DateTimeOffset AppliedAt);
}

internal static class SnapshotMapper
{
    // 將執行期 Domain 與持久化 Snapshot 隔離，避免序列化細節污染核心模型。
    public static AppStateSnapshot Build(AppState state, SessionStore sessions)
        => new(
            state.Users.Values.ToArray(),
            state.Institutions.Values.ToArray(),
            state.ReportDefinitions.Values.ToArray(),
            state.Submissions.Values.Select(SubmissionSnapshot.FromDomain).ToArray(),
            state.Keys.Values.ToArray(),
            state.Notifications.ToArray(),
            state.AuditLogs.ToArray(),
            sessions.Snapshot().ToArray(),
            sessions.RevokedSnapshot().ToArray(),
            state.MfaPolicy);

    public static void Apply(AppStateSnapshot snapshot, AppState state, SessionStore sessions)
    {
        state.Users.Clear();
        foreach (var i in snapshot.Users) state.Users[i.Id] = i;

        state.Institutions.Clear();
        foreach (var i in snapshot.Institutions) state.Institutions[i.Code] = i;

        state.ReportDefinitions.Clear();
        foreach (var i in snapshot.ReportDefinitions) state.ReportDefinitions[i.ReportCode] = i;

        state.Submissions.Clear();
        foreach (var i in snapshot.Submissions)
            state.Submissions[i.Id] = i.ToDomain();

        state.Keys.Clear();
        foreach (var i in snapshot.Keys) state.Keys[i.KeyId] = i;

        state.ResetBags();
        foreach (var i in snapshot.Notifications) state.Notifications.Add(i);
        foreach (var i in snapshot.AuditLogs) state.AuditLogs.Add(i);

        sessions.Restore(snapshot.Sessions, snapshot.RevokedTokens);
        state.MfaPolicy = snapshot.MfaPolicy ?? MfaPolicy.Default;
    }
}

public sealed class CompositeNotificationSink
{
    // 通知採 best-effort：主流程先寫入本地通知，再非同步嘗試 webhook。
    // webhook 失敗只記錄 warning，不回滾主交易。
    private readonly ILogger<CompositeNotificationSink> _logger;
    private readonly HttpClient _httpClient;
    private readonly string? _webhookUrl;

    public CompositeNotificationSink(ILogger<CompositeNotificationSink> logger, IHttpClientFactory factory, IConfiguration cfg)
    {
        _logger = logger;
        _httpClient = factory.CreateClient();
        _webhookUrl = cfg.GetString("Notifications:WebhookUrl", "NOTIFICATION_WEBHOOK_URL");
    }

    public async Task PublishAsync(Notification notification, CancellationToken ct = default)
    {
        _logger.LogInformation("Notification {Type} => user {UserId}: {Message}", notification.Type, notification.UserId, notification.Message);
        if (string.IsNullOrWhiteSpace(_webhookUrl)) return;

        try
        {
            using var resp = await _httpClient.PostAsJsonAsync(_webhookUrl, notification, ct);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Notification webhook failed with status {Status}", resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification webhook publish failed");
        }
    }
}

public enum AdAuthStatus
{
    Disabled,
    Success,
    InvalidCredentials,
    Misconfigured,
    DirectoryUnavailable
}

public sealed record AdAuthResult(AdAuthStatus Status, string? Detail = null)
{
    public bool IsSuccess => Status == AdAuthStatus.Success;
    public bool IsCredentialFailure => Status == AdAuthStatus.InvalidCredentials;
}

public sealed class AdAuthenticator
{
    private readonly ILogger<AdAuthenticator> _logger;
    private readonly Dictionary<string, string> _users;
    private readonly string _mode;
    private readonly string? _ldapHost;
    private readonly int _ldapPort;
    private readonly bool _ldapUseSsl;
    private readonly bool _ldapStartTls;
    private readonly bool _ignoreCertificateErrors;
    private readonly string? _bindDnTemplate;
    private readonly string? _upnDomain;
    private readonly int _connectTimeoutSeconds;
    private readonly int _operationTimeoutSeconds;

    public bool Enabled { get; }

    public AdAuthenticator(IConfiguration cfg, ILogger<AdAuthenticator> logger)
    {
        _logger = logger;
        Enabled = cfg.GetBool("Auth:Ad:Enabled", "AD_ENABLED") ?? false;
        _mode = (cfg.GetString("Auth:Ad:Mode", "AD_MODE") ?? "mock").Trim().ToLowerInvariant();

        var raw = cfg.GetString("Auth:Ad:MockUsersJson", "AD_MOCK_USERS_JSON");
        _users = string.IsNullOrWhiteSpace(raw)
            ? new(StringComparer.OrdinalIgnoreCase)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? new(StringComparer.OrdinalIgnoreCase);

        _ldapHost = cfg.GetString("Auth:Ad:Ldap:Host", "AD_LDAP_HOST");
        _ldapPort = cfg.GetInt("Auth:Ad:Ldap:Port", "AD_LDAP_PORT") ?? 636;
        _ldapUseSsl = cfg.GetBool("Auth:Ad:Ldap:UseSsl", "AD_LDAP_USE_SSL") ?? true;
        _ldapStartTls = cfg.GetBool("Auth:Ad:Ldap:StartTls", "AD_LDAP_STARTTLS") ?? false;
        _ignoreCertificateErrors = cfg.GetBool("Auth:Ad:Ldap:IgnoreCertificateErrors", "AD_LDAP_IGNORE_CERT_ERRORS") ?? false;
        _bindDnTemplate = cfg.GetString("Auth:Ad:Ldap:BindDnTemplate", "AD_LDAP_BIND_DN_TEMPLATE");
        _upnDomain = cfg.GetString("Auth:Ad:Ldap:UpnDomain", "AD_LDAP_UPN_DOMAIN");
        _connectTimeoutSeconds = cfg.GetInt("Auth:Ad:Ldap:ConnectTimeoutSeconds", "AD_LDAP_CONNECT_TIMEOUT_SECONDS") ?? 5;
        _operationTimeoutSeconds = cfg.GetInt("Auth:Ad:Ldap:OperationTimeoutSeconds", "AD_LDAP_OPERATION_TIMEOUT_SECONDS") ?? 10;
    }

    public AdAuthResult Validate(string email, string password)
    {
        if (!Enabled) return new(AdAuthStatus.Disabled);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return new(AdAuthStatus.InvalidCredentials);

        return _mode switch
        {
            "mock" => ValidateMock(email, password),
            "ldap" => ValidateLdap(email, password),
            _ => new(AdAuthStatus.Misconfigured, $"Unknown AD mode: {_mode}")
        };
    }

    private AdAuthResult ValidateMock(string email, string password)
    {
        return _users.TryGetValue(email, out var pw) && pw == password
            ? new(AdAuthStatus.Success)
            : new(AdAuthStatus.InvalidCredentials);
    }

    private AdAuthResult ValidateLdap(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(_ldapHost))
            return new(AdAuthStatus.Misconfigured, "AD LDAP host is not configured");

        if (_ldapUseSsl && _ldapStartTls)
            return new(AdAuthStatus.Misconfigured, "UseSsl and StartTls cannot both be true");

        var bindUser = ResolveBindUser(email);

        try
        {
            var connectTimeout = TimeSpan.FromSeconds(Math.Clamp(_connectTimeoutSeconds, 1, 60));
            var operationTimeout = TimeSpan.FromSeconds(Math.Clamp(_operationTimeoutSeconds, 1, 120));

            using (var tcp = new TcpClient())
            {
                var connectTask = tcp.ConnectAsync(_ldapHost, _ldapPort);
                if (!connectTask.Wait(connectTimeout))
                    return new(AdAuthStatus.DirectoryUnavailable, "LDAP connect timeout");
            }

            var identifier = new LdapDirectoryIdentifier(_ldapHost, _ldapPort, false, false);
            using var connection = new LdapConnection(identifier)
            {
                AuthType = AuthType.Basic,
                Credential = new NetworkCredential(bindUser, password),
                Timeout = operationTimeout
            };

            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.SecureSocketLayer = _ldapUseSsl;
            if (_ignoreCertificateErrors)
                connection.SessionOptions.VerifyServerCertificate = (_, _) => true;

            if (_ldapStartTls)
                connection.SessionOptions.StartTransportLayerSecurity(null);

            connection.Bind();
            return new(AdAuthStatus.Success);
        }
        catch (LdapException ex) when (ex.ErrorCode == 49)
        {
            return new(AdAuthStatus.InvalidCredentials);
        }
        catch (LdapException ex)
        {
            _logger.LogWarning(ex, "LDAP validation failed for user {Email}.", email);
            return new(AdAuthStatus.DirectoryUnavailable, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LDAP unexpected validation failure for user {Email}.", email);
            return new(AdAuthStatus.DirectoryUnavailable, ex.Message);
        }
    }

    private string ResolveBindUser(string email)
    {
        if (!string.IsNullOrWhiteSpace(_bindDnTemplate))
        {
            var username = email.Split('@')[0];
            return _bindDnTemplate
                .Replace("{email}", email, StringComparison.OrdinalIgnoreCase)
                .Replace("{username}", username, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(_upnDomain) && !email.Contains('@'))
            return $"{email}@{_upnDomain}";

        return email;
    }
}

public record SessionInfo(string Token, Guid UserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Jti);
public record RevokedTokenInfo(string Jti, Guid UserId, DateTimeOffset RevokedAt, DateTimeOffset ExpiresAt);

public record AppStateSnapshot(
    User[] Users,
    Institution[] Institutions,
    ReportDefinition[] ReportDefinitions,
    SubmissionSnapshot[] Submissions,
    CryptoKey[] Keys,
    Notification[] Notifications,
    AuditLog[] AuditLogs,
    SessionInfo[] Sessions,
    RevokedTokenInfo[]? RevokedTokens = null,
    MfaPolicy? MfaPolicy = null);

public record SubmissionSnapshot(
    Guid Id,
    string ReportCode,
    string Period,
    string InstitutionCode,
    Guid SubmitterId,
    Guid? ReviewerId,
    string ReportVersion,
    SubmissionStatus Status,
    string PayloadJson,
    int RejectCount,
    List<string> RejectReasons,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ApiResponseCode,
    string? ApiResponseMessage)
{
    public static SubmissionSnapshot FromDomain(ReportSubmission item) => new(
        item.Id, item.ReportCode, item.Period, item.InstitutionCode, item.SubmitterId, item.ReviewerId, item.ReportVersion,
        item.Status, item.Payload.RootElement.GetRawText(), item.RejectCount, item.RejectReasons, item.CreatedAt, item.UpdatedAt, item.ApiResponseCode, item.ApiResponseMessage);

    public ReportSubmission ToDomain() => new(
        Id, ReportCode, Period, InstitutionCode, SubmitterId, ReviewerId, ReportVersion,
        Status, JsonDocument.Parse(PayloadJson), RejectCount, RejectReasons, CreatedAt, UpdatedAt, ApiResponseCode, ApiResponseMessage);
}

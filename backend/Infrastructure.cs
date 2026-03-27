using System.Collections.Concurrent;
using System.Data;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace BankReporting.Api;

public interface IStateRepository
{
    bool TryLoad(AppState state, SessionStore sessions);
    void Save(AppState state, SessionStore sessions);
}

public sealed class JwtTokenService
{
    private readonly byte[] _key;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeSpan _ttl;
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly TokenValidationParameters _validation;

    public JwtTokenService(IConfiguration cfg)
    {
        var secret = cfg["JWT_SECRET"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
            throw new InvalidOperationException("JWT_SECRET must be configured and at least 32 chars for production use.");

        _issuer = cfg["JWT_ISSUER"] ?? "bank-reporting";
        _audience = cfg["JWT_AUDIENCE"] ?? "bank-reporting-web";
        _ttl = TimeSpan.FromMinutes(int.TryParse(cfg["JWT_TTL_MINUTES"], out var mins) ? mins : 30);
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
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonStateRepository(IConfiguration cfg)
    {
        _path = cfg["PERSISTENCE_FILE"] ?? Path.Combine(AppContext.BaseDirectory, "data", "app-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
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
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public SqlStateRepository(IConfiguration cfg)
    {
        _connectionString = cfg.GetConnectionString("Default")
            ?? cfg["SQLSERVER_CONNECTION_STRING"]
            ?? throw new InvalidOperationException("SQL Server persistence enabled but no connection string provided.");
    }

    public bool TryLoad(AppState state, SessionStore sessions)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        EnsureSchema(conn);

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
        EnsureSchema(conn);

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

    private static void EnsureSchema(SqlConnection conn)
    {
        using var cmd = new SqlCommand(@"
IF OBJECT_ID('dbo.AppStateSnapshots', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AppStateSnapshots(
        Id INT NOT NULL CONSTRAINT PK_AppStateSnapshots PRIMARY KEY,
        Payload NVARCHAR(MAX) NOT NULL,
        UpdatedAt DATETIMEOFFSET(7) NOT NULL
    );
END", conn);
        cmd.ExecuteNonQuery();
    }
}

internal static class SnapshotMapper
{
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
            sessions.RevokedSnapshot().ToArray());

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
    }
}

public sealed class CompositeNotificationSink
{
    private readonly ILogger<CompositeNotificationSink> _logger;
    private readonly HttpClient _httpClient;
    private readonly string? _webhookUrl;

    public CompositeNotificationSink(ILogger<CompositeNotificationSink> logger, IHttpClientFactory factory, IConfiguration cfg)
    {
        _logger = logger;
        _httpClient = factory.CreateClient();
        _webhookUrl = cfg["NOTIFICATION_WEBHOOK_URL"];
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

public sealed class AdAuthenticator
{
    private readonly Dictionary<string, string> _users;
    public bool Enabled { get; }

    public AdAuthenticator(IConfiguration cfg)
    {
        Enabled = bool.TryParse(cfg["AD_ENABLED"], out var enabled) && enabled;
        var raw = cfg["AD_MOCK_USERS_JSON"];
        _users = string.IsNullOrWhiteSpace(raw)
            ? new(StringComparer.OrdinalIgnoreCase)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? new(StringComparer.OrdinalIgnoreCase);
    }

    public bool Validate(string email, string password)
    {
        if (!Enabled) return false;
        return _users.TryGetValue(email, out var pw) && pw == password;
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
    RevokedTokenInfo[]? RevokedTokens = null);

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

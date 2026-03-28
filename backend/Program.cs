using BankReporting.Api;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
// 核心狀態採 Singleton：此專案目前以「單程序記憶體 + 持久化快照」為基礎，
// 若改為多節點部署，需改成共享儲存與分散式 session 驗證。
builder.Services.AddSingleton<AppState>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<IStateRepository>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    // 支援 json/sqlserver 雙持久化模式；預設 json 便於本機開發，
    // 正式環境建議使用 SQL 以避免檔案競態與單機故障風險。
    var provider = (cfg.GetString("Persistence:Provider", "PERSISTENCE_PROVIDER") ?? "json").Trim().ToLowerInvariant();
    return provider is "sql" or "sqlserver" ? new SqlStateRepository(cfg) : new JsonStateRepository(cfg);
});
builder.Services.AddSingleton<AdAuthenticator>();
builder.Services.AddSingleton<CompositeNotificationSink>();
builder.Services.AddHttpClient();
builder.Services.AddRateLimiter(options =>
{
    // API 節流策略：登入端點較嚴格，其它 API 較寬鬆。
    // 目標是降低暴力嘗試與誤用風險，而非取代 WAF/邊界防護。
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var permitLimit = path.StartsWith("/auth/login", StringComparison.OrdinalIgnoreCase) ? 10 : 60;
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"{ip}:{(path.StartsWith("/auth/login", StringComparison.OrdinalIgnoreCase) ? "login" : "api")}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
    });
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();

var forwardedHeadersOptions = BuildForwardedHeadersOptions(app.Configuration);
if (forwardedHeadersOptions is not null)
{
    // 若服務部署在反向代理後方，需先套用 forwarded headers，
    // 才能正確取得來源 IP / HTTPS 狀態（影響稽核、HSTS、登入防護）。
    app.UseForwardedHeaders(forwardedHeadersOptions);
}

var enableHttpsRedirect = app.Configuration.GetBool("Security:HttpsRedirection:Enabled", "ENABLE_HTTPS_REDIRECT") ?? !app.Environment.IsDevelopment();
if (enableHttpsRedirect)
{
    app.UseHttpsRedirection();
}

app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    // 基線安全標頭：避免 MIME sniffing、點擊劫持與過度外部載入。
    // 若前端改為外部 CDN，需同步檢視 CSP，避免部署後被瀏覽器阻擋。
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (ctx.Request.IsHttps)
        ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});

var db = app.Services.GetRequiredService<AppState>();
var sessions = app.Services.GetRequiredService<SessionStore>();
var repo = app.Services.GetRequiredService<IStateRepository>();
var jwt = app.Services.GetRequiredService<JwtTokenService>();
repo.EnsureReady();
// 啟動時先確認儲存層可用並嘗試載入快照；僅在全新環境才進行 Seed。
// 注意：Seed 含示範帳號密碼，只適合開發/測試，不可直接沿用到正式環境。
if (!repo.TryLoad(db, sessions)) Seed(db);

app.MapGet("/health", () => Results.Ok(new { status = "ok", ts = DateTimeOffset.UtcNow }));

app.MapPost("/auth/register", (AppState state, IStateRepository storage, RegisterRequest req, HttpContext ctx) =>
{
    if (!SecurityHelpers.IsStrongPassword(req.Password)) return Results.BadRequest("Weak password");
    if (state.Users.Values.Any(u => u.Email.Equals(req.Email, StringComparison.OrdinalIgnoreCase))) return Results.Conflict("Email exists");

    var hash = SecurityHelpers.HashPassword(req.Password);
    var user = new User(Guid.NewGuid(), req.Name, req.Email, req.InstitutionCode, req.Department, req.Title, UserRole.Clerk, AccountStatus.PendingApproval,
        hash, new() { hash }, DateTimeOffset.UtcNow, 0, null, false, null, req.IsAdUser);
    state.Users[user.Id] = user;
    Audit(state, user.Id, user.Name, "REG-001", "User", user.Id.ToString(), "Account registration submitted", ctx);
    Persist(state, sessions, storage);
    return Results.Ok(new { userId = user.Id, status = user.Status.ToString() });
});

app.MapPost("/auth/login", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, AdAuthenticator ad, IStateRepository storage, LoginRequest req, HttpContext ctx) =>
{
    // 登入流程重點：
    // 1) 帳號鎖定檢查 -> 2) 密碼/AD 驗證 -> 3) MFA 驗證 -> 4) 建立 token + session。
    // 其中 session 與 JWT 需同時有效，才能視為已登入。
    var user = state.Users.Values.FirstOrDefault(x => x.Email.Equals(req.Email, StringComparison.OrdinalIgnoreCase));
    if (user is null) return Results.Unauthorized();

    if (user.LockoutEnd is not null && user.LockoutEnd > DateTimeOffset.UtcNow) return Results.BadRequest("Locked");

    var passOk = user.IsAdUser ? ad.Validate(req.Email, req.Password) : SecurityHelpers.VerifyPassword(req.Password, user.PasswordHash);
    if (!passOk)
    {
        // 連續失敗超過門檻後鎖定 30 分鐘。
        // 鎖定同時會將狀態標記為 Locked，利於後台快速識別異常帳號。
        var failed = user.FailedLoginCount + 1;
        var updated = user with { FailedLoginCount = failed, LockoutEnd = failed >= 5 ? DateTimeOffset.UtcNow.AddMinutes(30) : null, Status = failed >= 5 ? AccountStatus.Locked : user.Status };
        state.Users[user.Id] = updated;
        Audit(state, user.Id, user.Name, "AUTH-002", "User", user.Id.ToString(), "Login failed", ctx);
        Persist(state, sessions, storage);
        return Results.Unauthorized();
    }

    if (user.Status is not AccountStatus.Active and not AccountStatus.PendingApproval) return Results.BadRequest($"Account status: {user.Status}");

    var mfaRequiredByPolicy = RequiresMfaPolicy(state, user);
    if (user.MfaEnabled)
    {
        // 使用者已啟用 MFA 時，登入必須附上有效 TOTP。
        // 注意：政策層(requiredByPolicy)僅回報合規狀態；實際阻擋由 RequireAny/RequireRole 在授權端點執行。
        if (string.IsNullOrWhiteSpace(req.MfaCode) || string.IsNullOrWhiteSpace(user.MfaSecret) || !SecurityHelpers.VerifyTotp(user.MfaSecret, req.MfaCode))
            return Results.BadRequest("MFA required or invalid");
    }

    var issued = tokenService.Create(user);
    sessionStore.Add(issued.Token, user.Id, DateTimeOffset.UtcNow, issued.ExpiresAt, issued.Jti);
    state.Users[user.Id] = user with { FailedLoginCount = 0, LockoutEnd = null };

    ctx.Response.Cookies.Append("access_token", issued.Token, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Expires = issued.ExpiresAt,
        IsEssential = true
    });

    Audit(state, user.Id, user.Name, "AUTH-001", "Session", issued.Jti, "Login success", ctx);
    Persist(state, sessions, storage);
    return Results.Ok(new
    {
        token = issued.Token,
        expiresInSeconds = (int)(issued.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds,
        user = new { user.Id, user.Name, user.Email, role = user.Role.ToString(), user.InstitutionCode, status = user.Status.ToString(), user.MfaEnabled, user.IsAdUser },
        mfa = new { requiredByPolicy = mfaRequiredByPolicy, compliant = !mfaRequiredByPolicy || user.MfaEnabled, scope = state.MfaPolicy.Scope.ToString(), enforcementEnabled = state.MfaPolicy.EnforceOnPrivilegedEndpoints }
    });
});

app.MapPost("/auth/logout", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, IStateRepository storage, HttpContext ctx) =>
{
    var token = ExtractToken(ctx);
    var principal = string.IsNullOrWhiteSpace(token) ? null : tokenService.Validate(token);
    var jti = principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;
    var sub = principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;

    if (!string.IsNullOrWhiteSpace(token))
    {
        sessionStore.RevokeToken(token, DateTimeOffset.UtcNow);
        if (Guid.TryParse(sub, out var userId) && state.Users.TryGetValue(userId, out var user) && !string.IsNullOrWhiteSpace(jti))
            Audit(state, userId, user.Name, "AUTH-003", "Session", jti, "Logout/Revoke current token", ctx);
        Persist(state, sessions, storage);
    }

    ctx.Response.Cookies.Delete("access_token");
    return Results.Ok();
});

app.MapPost("/auth/mfa/setup", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, IStateRepository storage, HttpContext ctx) =>
{
    var user = CurrentUser(state, sessionStore, tokenService, ctx);
    if (user is null) return Results.Unauthorized();
    var secret = SecurityHelpers.NewTotpSecret();
    state.Users[user.Id] = user with { MfaSecret = secret, MfaEnabled = false };
    Persist(state, sessions, storage);
    return Results.Ok(new { secret });
});

app.MapPost("/auth/mfa/enable", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, IStateRepository storage, EnableMfaRequest req, HttpContext ctx) =>
{
    var user = CurrentUser(state, sessionStore, tokenService, ctx);
    if (user is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(user.MfaSecret) || !SecurityHelpers.VerifyTotp(user.MfaSecret, req.Code)) return Results.BadRequest("invalid code");
    state.Users[user.Id] = user with { MfaEnabled = true };
    Persist(state, sessions, storage);
    return Results.Ok(new { enabled = true });
});

app.MapPost("/auth/change-password", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, IStateRepository storage, ChangePasswordRequest req, HttpContext ctx) =>
{
    var user = CurrentUser(state, sessionStore, tokenService, ctx);
    if (user is null) return Results.Unauthorized();
    if (user.IsAdUser) return Results.BadRequest("AD user password is managed by directory service");
    if (!SecurityHelpers.VerifyPassword(req.CurrentPassword, user.PasswordHash)) return Results.BadRequest("Current password invalid");
    if (!SecurityHelpers.IsStrongPassword(req.NewPassword)) return Results.BadRequest("Weak password");

    var history = user.PasswordHistory ?? new List<string>();
    if (history.Any(h => SecurityHelpers.VerifyPassword(req.NewPassword, h)))
        return Results.BadRequest("New password must not match password history");

    var newHash = SecurityHelpers.HashPassword(req.NewPassword);
    var updatedHistory = history.Append(newHash).TakeLast(5).ToList();
    state.Users[user.Id] = user with
    {
        PasswordHash = newHash,
        PasswordHistory = updatedHistory,
        PasswordChangedAt = DateTimeOffset.UtcNow,
        FailedLoginCount = 0,
        LockoutEnd = null
    };

    Audit(state, user.Id, user.Name, "AUTH-005", "User", user.Id.ToString(), "Password changed", ctx);
    Persist(state, sessions, storage);
    return Results.Ok(new { changed = true });
});

app.MapPost("/admin/users/{userId:guid}/approve", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, IStateRepository storage, CompositeNotificationSink sink, Guid userId, ApproveUserRequest req, HttpContext ctx) =>
{
    var admin = RequireRole(state, sessionStore, tokenService, ctx, UserRole.Admin);
    if (admin is null) return Results.Unauthorized();
    if (!state.Users.TryGetValue(userId, out var user)) return Results.NotFound();
    var updated = user with { Role = req.Role, InstitutionCode = req.InstitutionCode, Status = req.Approve ? AccountStatus.Active : AccountStatus.Rejected };
    state.Users[userId] = updated;
    Notify(state, sink, userId, "Account", req.Approve ? "帳號申請已核准" : "帳號申請遭拒絕");
    Audit(state, admin.Id, admin.Name, "REG-004", "User", userId.ToString(), req.Approve ? "Approved" : "Rejected", ctx);
    Persist(state, sessions, storage);
    return Results.Ok(updated);
});

app.MapPost("/admin/users/{userId:guid}/revoke-sessions", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, IStateRepository storage, Guid userId, HttpContext ctx) =>
{
    var admin = RequireRole(state, sessionStore, tokenService, ctx, UserRole.Admin);
    if (admin is null) return Results.Unauthorized();
    if (!state.Users.TryGetValue(userId, out var target)) return Results.NotFound();

    var revokedCount = sessionStore.RevokeUserSessions(userId, DateTimeOffset.UtcNow);
    Audit(state, admin.Id, admin.Name, "AUTH-004", "User", userId.ToString(), $"Revoked {revokedCount} active sessions", ctx);
    Persist(state, sessions, storage);
    return Results.Ok(new { userId, revokedCount, target.Email });
});

app.MapGet("/admin/users", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, HttpContext ctx) =>
{
    var admin = RequireRole(state, sessionStore, tokenService, ctx, UserRole.Admin);
    if (admin is null) return Results.Unauthorized();
    return Results.Ok(state.Users.Values.OrderBy(x => x.Email));
});

app.MapGet("/admin/security/mfa-policy", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, HttpContext ctx) =>
{
    var admin = RequireRole(state, sessionStore, tokenService, ctx, UserRole.Admin);
    if (admin is null) return Results.Unauthorized();
    return Results.Ok(state.MfaPolicy);
});

app.MapPut("/admin/security/mfa-policy", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, IStateRepository storage, UpdateMfaPolicyRequest req, HttpContext ctx) =>
{
    // MFA 政策變更屬高風險操作：可選擇立即撤銷不合規帳號的現有 session。
    // 這是「政策生效」與「既有登入清場」的關鍵開關，請在公告後執行。
    var admin = RequireRole(state, sessionStore, tokenService, ctx, UserRole.Admin);
    if (admin is null) return Results.Unauthorized();

    var updated = new MfaPolicy(req.Scope, req.EnforceOnPrivilegedEndpoints, DateTimeOffset.UtcNow, admin.Id, string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim());
    state.MfaPolicy = updated;

    var revokedCount = 0;
    if (req.RevokeNonCompliantSessions)
    {
        var impactedUsers = state.Users.Values.Where(u => updated.RequiresMfa(u.Role) && !u.MfaEnabled).Select(u => u.Id).ToArray();
        foreach (var userId in impactedUsers)
            revokedCount += sessionStore.RevokeUserSessions(userId, DateTimeOffset.UtcNow);
    }

    Audit(state, admin.Id, admin.Name, "AUTH-006", "SecurityPolicy", "MFA", $"Updated MFA policy: scope={updated.Scope}, enforce={updated.EnforceOnPrivilegedEndpoints}, revokedSessions={revokedCount}", ctx);
    Persist(state, sessions, storage);
    return Results.Ok(new { policy = updated, revokedCount });
});

app.MapGet("/report-definitions", (AppState state) => Results.Ok(state.ReportDefinitions.Values.OrderBy(x => x.ReportCode)));

app.MapPost("/submissions", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, IStateRepository storage, CreateSubmissionRequest req, HttpContext ctx) =>
{
    var user = RequireAny(state, sessionStore, tokenService, ctx, UserRole.Clerk, UserRole.Supervisor, UserRole.Admin);
    if (user is null) return Results.Unauthorized();
    if (!state.ReportDefinitions.TryGetValue(req.ReportCode, out var def) || !def.IsEnabled) return Results.BadRequest("Invalid report code");

    if (state.Submissions.Values.Any(x => x.ReportCode == req.ReportCode && x.Period == req.Period && x.InstitutionCode == user.InstitutionCode && x.Status is SubmissionStatus.Pending or SubmissionStatus.Approved or SubmissionStatus.Submitted))
        return Results.BadRequest("Duplicate in same period");

    var version = def.Versions.Where(v => v.EffectiveDate <= DateOnly.Parse(req.Period + "-01")).OrderByDescending(v => v.EffectiveDate).FirstOrDefault() ?? def.Versions.OrderByDescending(v => v.EffectiveDate).First();

    using var payload = JsonDocument.Parse(req.Payload.GetRawText());
    foreach (var field in version.Fields.Where(f => f.Required))
        if (!payload.RootElement.TryGetProperty(field.FieldCode, out _))
            return Results.BadRequest($"Missing field {field.FieldCode}");

    var now = DateTimeOffset.UtcNow;
    var sub = new ReportSubmission(Guid.NewGuid(), req.ReportCode, req.Period, user.InstitutionCode, user.Id, null, version.Version, SubmissionStatus.Draft,
        JsonDocument.Parse(req.Payload.GetRawText()), 0, new(), now, now, null, null);
    state.Submissions[sub.Id] = sub;
    Audit(state, user.Id, user.Name, "WF-001", "Submission", sub.Id.ToString(), "Created draft", ctx);
    Persist(state, sessions, storage);
    return Results.Ok(sub);
});

app.MapPost("/submissions/{id:guid}/submit", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, IStateRepository storage, CompositeNotificationSink sink, Guid id, HttpContext ctx) =>
{
    var user = RequireAny(state, sessionStore, tokenService, ctx, UserRole.Clerk, UserRole.Supervisor, UserRole.Admin);
    if (user is null) return Results.Unauthorized();
    if (!state.Submissions.TryGetValue(id, out var sub)) return Results.NotFound();
    if (sub.SubmitterId != user.Id && user.Role != UserRole.Admin) return Results.Forbid();
    if (sub.Status is not SubmissionStatus.Draft and not SubmissionStatus.Rejected) return Results.BadRequest("Invalid state");

    var updated = sub with { Status = SubmissionStatus.Pending, UpdatedAt = DateTimeOffset.UtcNow };
    state.Submissions[id] = updated;
    NotifyRole(state, sink, user.InstitutionCode, UserRole.Supervisor, "Workflow", $"報表 {sub.ReportCode}/{sub.Period} 待審核");
    Audit(state, user.Id, user.Name, "WF-003", "Submission", sub.Id.ToString(), "Submitted for review", ctx);
    Persist(state, sessions, storage);
    return Results.Ok(updated);
});

app.MapPost("/submissions/{id:guid}/review", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, IStateRepository storage, CompositeNotificationSink sink, Guid id, ReviewRequest req, HttpContext ctx) =>
{
    // 審核流程不變量：僅 Pending 可審、同機構主管可審（Admin 例外）。
    // 核准後的「對外送審」目前為模擬呼叫（10% 失敗），正式串接需改為可重試的外部整合。
    var reviewer = RequireAny(state, sessionStore, tokenService, ctx, UserRole.Supervisor, UserRole.Admin);
    if (reviewer is null) return Results.Unauthorized();
    if (!state.Submissions.TryGetValue(id, out var sub)) return Results.NotFound();
    if (sub.Status != SubmissionStatus.Pending) return Results.BadRequest("Not pending");
    if (reviewer.Role != UserRole.Admin && reviewer.InstitutionCode != sub.InstitutionCode) return Results.Forbid();

    var updated = req.Approve
        ? sub with { Status = SubmissionStatus.Approved, ReviewerId = reviewer.Id, UpdatedAt = DateTimeOffset.UtcNow }
        : sub with { Status = SubmissionStatus.Rejected, ReviewerId = reviewer.Id, UpdatedAt = DateTimeOffset.UtcNow, RejectCount = sub.RejectCount + 1, RejectReasons = sub.RejectReasons.Append(req.Reason ?? "").ToList() };
    state.Submissions[id] = updated;
    Notify(state, sink, sub.SubmitterId, "Workflow", req.Approve ? "主管已放行" : $"主管退回：{req.Reason}");

    if (req.Approve)
    {
        var submitting = updated with { Status = SubmissionStatus.Submitting, UpdatedAt = DateTimeOffset.UtcNow };
        state.Submissions[id] = submitting;
        var success = Random.Shared.Next(0, 100) >= 10;
        state.Submissions[id] = submitting with
        {
            Status = success ? SubmissionStatus.Submitted : SubmissionStatus.Failed,
            UpdatedAt = DateTimeOffset.UtcNow,
            ApiResponseCode = success ? "200" : "500",
            ApiResponseMessage = success ? "Submitted to JCIC" : "Simulated timeout/retry exceeded"
        };
    }

    Audit(state, reviewer.Id, reviewer.Name, req.Approve ? "WF-009" : "WF-010", "Submission", id.ToString(), req.Approve ? "Approved" : "Rejected", ctx);
    Persist(state, sessions, storage);
    return Results.Ok(state.Submissions[id]);
});

app.MapGet("/history/{id:guid}/download", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, Guid id, HttpContext ctx, string format = "json") =>
{
    var user = RequireAny(state, sessionStore, tokenService, ctx, UserRole.Admin, UserRole.Supervisor, UserRole.Clerk, UserRole.ReadOnly);
    if (user is null) return Results.Unauthorized();
    if (!state.Submissions.TryGetValue(id, out var sub)) return Results.NotFound();
    if (!CanDownload(user, sub)) return Results.Forbid();

    Audit(state, user.Id, user.Name, "HIS-018", "Submission", id.ToString(), $"download {format}", ctx);
    return format.ToLowerInvariant() switch
    {
        "csv" => Results.File(System.Text.Encoding.UTF8.GetBytes(SecurityHelpers.BuildCsv(sub.Payload)), "text/csv", $"{sub.ReportCode}_{sub.Period}.csv"),
        "xlsx" => Results.File(SecurityHelpers.BuildXlsx(sub.Payload), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{sub.ReportCode}_{sub.Period}.xlsx"),
        _ => Results.Json(sub.Payload.RootElement)
    };
});

app.MapGet("/notifications", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, HttpContext ctx) =>
{
    var user = RequireAny(state, sessionStore, tokenService, ctx, UserRole.Admin, UserRole.Supervisor, UserRole.Clerk, UserRole.ReadOnly);
    if (user is null) return Results.Unauthorized();
    return Results.Ok(state.Notifications.Where(x => x.UserId == user.Id).OrderByDescending(x => x.CreatedAt).Take(200));
});

app.MapGet("/admin/audit-logs", (AppState state, SessionStore sessionStore, JwtTokenService tokenService, HttpContext ctx, int top = 200) =>
{
    var admin = RequireRole(state, sessionStore, tokenService, ctx, UserRole.Admin);
    if (admin is null) return Results.Unauthorized();
    return Results.Ok(state.AuditLogs.OrderByDescending(x => x.At).Take(top));
});

app.Run();

static void Persist(AppState db, SessionStore sessions, IStateRepository repo) => repo.Save(db, sessions);

static User? RequireRole(AppState db, SessionStore sessions, JwtTokenService jwt, HttpContext ctx, UserRole role)
{
    var u = CurrentUser(db, sessions, jwt, ctx);
    return u is not null && u.Role == role && IsMfaCompliant(db, u) ? u : null;
}

static User? RequireAny(AppState db, SessionStore sessions, JwtTokenService jwt, HttpContext ctx, params UserRole[] roles)
{
    var u = CurrentUser(db, sessions, jwt, ctx);
    return u is not null && roles.Contains(u.Role) && IsMfaCompliant(db, u) ? u : null;
}

static User? CurrentUser(AppState db, SessionStore sessions, JwtTokenService jwt, HttpContext ctx)
{
    // 驗證採雙層：JWT 簽章/時效 + 伺服器端 session 狀態（含 JTI 撤銷）。
    // 任何一層失效都視為未授權，可避免單純 JWT 無狀態下的撤銷延遲問題。
    var token = ExtractToken(ctx);
    if (string.IsNullOrWhiteSpace(token)) return null;
    var principal = jwt.Validate(token);
    if (principal is null) return null;

    var jti = principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;
    if (sessions.IsJtiRevoked(jti)) return null;

    if (!sessions.TryGetUserId(token, out var userId)) return null;
    return db.Users.TryGetValue(userId, out var user) ? user : null;
}

static string? ExtractToken(HttpContext ctx)
{
    // Token 來源優先序：X-Auth-Token > Authorization: Bearer > cookie。
    // 如需收斂來源以降低混淆，建議僅保留 Bearer/cookie 其中一種。
    if (ctx.Request.Headers.TryGetValue("X-Auth-Token", out var h) && !string.IsNullOrWhiteSpace(h.ToString())) return h.ToString();

    if (ctx.Request.Headers.TryGetValue("Authorization", out var auth))
    {
        var v = auth.ToString();
        if (v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return v[7..].Trim();
    }

    if (ctx.Request.Cookies.TryGetValue("access_token", out var cookie) && !string.IsNullOrWhiteSpace(cookie)) return cookie;
    return null;
}

static bool IsMfaCompliant(AppState db, User user)
{
    return !RequiresMfaPolicy(db, user) || user.MfaEnabled;
}

static bool RequiresMfaPolicy(AppState db, User user)
{
    var policy = db.MfaPolicy ?? MfaPolicy.Default;
    return policy.RequiresMfa(user.Role);
}

static bool CanDownload(User user, ReportSubmission sub) => user.Role switch
{
    UserRole.Admin => true,
    UserRole.Supervisor => sub.InstitutionCode == user.InstitutionCode && sub.Status is SubmissionStatus.Submitted or SubmissionStatus.Rejected,
    UserRole.Clerk => sub.SubmitterId == user.Id && sub.Status == SubmissionStatus.Submitted,
    UserRole.ReadOnly => sub.Status == SubmissionStatus.Submitted,
    _ => false
};

static void Notify(AppState db, CompositeNotificationSink sink, Guid userId, string type, string message)
{
    var item = new Notification(Guid.NewGuid(), userId, type, message, false, DateTimeOffset.UtcNow);
    db.Notifications.Add(item);
    _ = sink.PublishAsync(item);
}

static void NotifyRole(AppState db, CompositeNotificationSink sink, string institutionCode, UserRole role, string type, string message)
{
    foreach (var user in db.Users.Values.Where(x => x.InstitutionCode == institutionCode && x.Role == role && x.Status == AccountStatus.Active))
        Notify(db, sink, user.Id, type, message);
}

static void Audit(AppState db, Guid? userId, string userName, string action, string entityType, string entityId, string summary, HttpContext ctx)
    => db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), DateTimeOffset.UtcNow, userId, userName, action, entityType, entityId, summary, ctx.Connection.RemoteIpAddress?.ToString() ?? "local"));

static void Seed(AppState db)
{
    db.Institutions["B001"] = new Institution("B001", "第一銀行", true);
    db.Institutions["B002"] = new Institution("B002", "第二銀行", true);

    var adminHash = SecurityHelpers.HashPassword("Admin#12345678");
    var admin = new User(Guid.NewGuid(), "System Admin", "admin@bank.local", "B001", "IT", "Admin", UserRole.Admin, AccountStatus.Active, adminHash, new() { adminHash }, DateTimeOffset.UtcNow, 0, null, false, null);
    var supHash = SecurityHelpers.HashPassword("Supervisor#1234");
    var sup = new User(Guid.NewGuid(), "B001 Supervisor", "supervisor@bank.local", "B001", "Risk", "Supervisor", UserRole.Supervisor, AccountStatus.Active, supHash, new() { supHash }, DateTimeOffset.UtcNow, 0, null, false, null);
    var clerkHash = SecurityHelpers.HashPassword("Clerk#12345678");
    var clerk = new User(Guid.NewGuid(), "B001 Clerk", "clerk@bank.local", "B001", "Risk", "Clerk", UserRole.Clerk, AccountStatus.Active, clerkHash, new() { clerkHash }, DateTimeOffset.UtcNow, 0, null, false, null);

    db.Users[admin.Id] = admin;
    db.Users[sup.Id] = sup;
    db.Users[clerk.Id] = clerk;

    var reports = new[] { ("AI330", "授信擔保品別分析表"), ("AI863", "海外分行授信顆粒化資料") };

    foreach (var (code, name) in reports)
    {
        var fields = new List<ReportField>
        {
            new("reportMonth", "申報月份", "string", true, null, null, 7),
            new("amount", "金額", "number", true, 0),
            new("note", "備註", "string", false, null, null, 200)
        };

        var schema = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new { reportMonth = new { type = "string" }, amount = new { type = "number" }, note = new { type = "string" } },
            required = new[] { "reportMonth", "amount" }
        });

        var v = new ReportVersion(Guid.NewGuid(), code, "1.0.0", new DateOnly(2026, 1, 1), "seed", true, fields, schema, "{}");
        db.ReportDefinitions[code] = new ReportDefinition(code, name, code == "AI863" ? "Granular" : "Monthly", 15, $"/api/report/{code.ToLowerInvariant()}", true, new() { v });
    }
}



static ForwardedHeadersOptions? BuildForwardedHeadersOptions(IConfiguration config)
{
    // 反向代理信任設定：正式環境應明確填入 TrustedProxies/TrustedNetworks。
    // 若使用預設私網段，請確認網路邊界可信，避免偽造 X-Forwarded-* 影響來源判定。
    var enabled = config.GetBool("Security:ForwardedHeaders:Enabled", "ENABLE_FORWARDED_HEADERS") ?? true;
    if (!enabled) return null;

    var options = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = config.GetInt("Security:ForwardedHeaders:ForwardLimit", "FORWARDED_HEADERS_FORWARD_LIMIT") ?? 1,
        RequireHeaderSymmetry = false
    };

    var proxyIps = (config.GetString("Security:ForwardedHeaders:TrustedProxies", "FORWARDED_HEADERS_TRUSTED_PROXIES") ?? string.Empty)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    foreach (var ip in proxyIps)
    {
        if (IPAddress.TryParse(ip, out var parsed))
            options.KnownProxies.Add(parsed);
    }

    var proxyNetworks = (config.GetString("Security:ForwardedHeaders:TrustedNetworks", "FORWARDED_HEADERS_TRUSTED_NETWORKS") ?? string.Empty)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    foreach (var network in proxyNetworks)
    {
        var parts = network.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var prefix) && int.TryParse(parts[1], out var prefixLength))
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
    }

    // Sensible defaults for common reverse-proxy/container networks.
    if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 1)
    {
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
    }

    return options;
}

public record RegisterRequest(string Name, string Email, string Password, string InstitutionCode, string Department, string Title, bool IsAdUser = false);
public record LoginRequest(string Email, string Password, string? MfaCode = null);
public record EnableMfaRequest(string Code);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record ApproveUserRequest(bool Approve, UserRole Role, string InstitutionCode);
public record UpdateMfaPolicyRequest(MfaPolicyScope Scope, bool EnforceOnPrivilegedEndpoints, bool RevokeNonCompliantSessions = false, string? Note = null);
public record CreateSubmissionRequest(string ReportCode, string Period, JsonElement Payload);
public record ReviewRequest(bool Approve, string? Reason);

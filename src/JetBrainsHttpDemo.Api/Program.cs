using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
    ?? throw new InvalidOperationException("JWT settings are required.");

builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton(sp => new SqliteDatabase(
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Database")
        ?? throw new InvalidOperationException("A Database connection string is required.")));
builder.Services.AddSingleton<EventStore>();
builder.Services.AddSingleton<TaskCommandHandler>();
builder.Services.AddSingleton<TaskQueryHandler>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = TokenService.ValidationParameters(jwt);
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var sessionId = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);
                var database = context.HttpContext.RequestServices.GetRequiredService<SqliteDatabase>();
                if (sessionId is null || !await SessionExists(database, sessionId))
                    context.Fail("The login session is no longer valid.");
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
await InitializeDatabase(app.Services.GetRequiredService<SqliteDatabase>());

app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/auth/login", async (HttpRequest request, TokenService tokens, IConfiguration config,
    SqliteDatabase database) =>
{
    var isOAuthRequest = request.HasFormContentType;
    LoginRequest credentials;
    if (isOAuthRequest)
    {
        var form = await request.ReadFormAsync();
        credentials = new LoginRequest(form["username"].ToString(), form["password"].ToString());
    }
    else
    {
        credentials = await request.ReadFromJsonAsync<LoginRequest>()
            ?? throw new BadHttpRequestException("Login credentials are required.");
    }

    var expectedUser = config["DemoUser:Username"];
    var expectedPassword = config["DemoUser:Password"];
    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(credentials.Username), Encoding.UTF8.GetBytes(expectedUser ?? "")) ||
        !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(credentials.Password), Encoding.UTF8.GetBytes(expectedPassword ?? "")))
        return Results.Unauthorized();

    var result = tokens.Create(credentials.Username);
    await using var connection = await database.OpenConnection();
    await using var command = connection.CreateCommand();
    command.CommandText = "INSERT INTO sessions (id, username, expires_at) VALUES ($id, $username, $expiresAt)";
    command.Parameters.AddWithValue("$id", result.SessionId);
    command.Parameters.AddWithValue("$username", credentials.Username);
    command.Parameters.AddWithValue("$expiresAt", result.ExpiresAt.ToString("O"));
    await command.ExecuteNonQueryAsync();
    return isOAuthRequest
        ? Results.Ok(new Dictionary<string, object>
        {
            ["access_token"] = result.Token,
            ["token_type"] = "Bearer",
            ["expires_in"] = 3600
        })
        : Results.Ok(new LoginResponse(result.Token, result.ExpiresAt));
});

app.MapPost("/api/tasks", async (CreateTask command, TaskCommandHandler handler, ClaimsPrincipal user) =>
{
    if (string.IsNullOrWhiteSpace(command.Title))
        return Results.BadRequest(new { error = "Title is required." });
    var result = await handler.Handle(command, user.Identity!.Name!);
    return Results.Created($"/api/tasks/{result.Id}", result);
}).RequireAuthorization();

app.MapGet("/api/tasks", async (string? search, string? status, int? page, int? pageSize,
    TaskQueryHandler handler) =>
{
    var normalizedPage = page is null or <= 0 ? 1 : page.Value;
    var normalizedPageSize = pageSize is null or <= 0 ? 10 : Math.Clamp(pageSize.Value, 1, 100);
    var query = new GetTasks(search, status, normalizedPage, normalizedPageSize);
    return Results.Ok(await handler.Handle(query));
}).RequireAuthorization();

app.Run();

static async Task InitializeDatabase(SqliteDatabase database)
{
    await using var connection = await database.OpenConnection();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS events (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            stream_id TEXT NOT NULL,
            event_type TEXT NOT NULL,
            data TEXT NOT NULL,
            occurred_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_events_stream_id ON events(stream_id, sequence);
        CREATE TABLE IF NOT EXISTS sessions (
            id TEXT PRIMARY KEY,
            username TEXT NOT NULL,
            expires_at TEXT NOT NULL
        );
        """;
    await command.ExecuteNonQueryAsync();
}

static async Task<bool> SessionExists(SqliteDatabase database, string id)
{
    await using var connection = await database.OpenConnection();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS(SELECT 1 FROM sessions WHERE id = $id AND expires_at > $now)";
    command.Parameters.AddWithValue("$id", id);
    command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
    return Convert.ToBoolean(await command.ExecuteScalarAsync());
}

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
public sealed record CreateTask(string Title, string? Description);
public sealed record TaskCreated(Guid Id, string Title, string? Description, string Status, string CreatedBy, DateTimeOffset CreatedAt);
public sealed record GetTasks(string? Search, string? Status, int Page, int PageSize);
public sealed record PagedTasks(IReadOnlyList<TaskView> Items, int Page, int PageSize, int Total);
public sealed record TaskView(Guid Id, string Title, string? Description, string Status, string CreatedBy, DateTimeOffset CreatedAt);
public sealed record StoredEvent(long Sequence, Guid StreamId, string EventType, string Data, DateTimeOffset OccurredAt);
public sealed record JwtOptions(string Issuer, string Audience, string Key);

public sealed class TaskCommandHandler(EventStore store)
{
    public async Task<TaskCreated> Handle(CreateTask command, string username)
    {
        var created = new TaskCreated(Guid.NewGuid(), command.Title.Trim(), command.Description?.Trim(),
            "open", username, DateTimeOffset.UtcNow);
        await store.Append(created.Id, nameof(TaskCreated), created, created.CreatedAt);
        return created;
    }
}

public sealed class TaskQueryHandler(EventStore store)
{
    public async Task<PagedTasks> Handle(GetTasks query)
    {
        var tasks = (await store.ReadAll())
            .Where(e => e.EventType == nameof(TaskCreated))
            .Select(e => JsonSerializer.Deserialize<TaskCreated>(e.Data)!)
            .Select(e => new TaskView(e.Id, e.Title, e.Description, e.Status, e.CreatedBy, e.CreatedAt));

        if (!string.IsNullOrWhiteSpace(query.Search))
            tasks = tasks.Where(t => t.Title.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
                (t.Description?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false));
        if (!string.IsNullOrWhiteSpace(query.Status))
            tasks = tasks.Where(t => t.Status.Equals(query.Status, StringComparison.OrdinalIgnoreCase));

        var matching = tasks.OrderByDescending(t => t.CreatedAt).ToList();
        return new PagedTasks(matching.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList(),
            query.Page, query.PageSize, matching.Count);
    }
}

public sealed class EventStore(SqliteDatabase database)
{
    public async Task Append<T>(Guid streamId, string eventType, T data, DateTimeOffset occurredAt)
    {
        await using var connection = await database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO events (stream_id, event_type, data, occurred_at) VALUES ($streamId, $type, $data, $at)";
        command.Parameters.AddWithValue("$streamId", streamId.ToString());
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(data));
        command.Parameters.AddWithValue("$at", occurredAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<StoredEvent>> ReadAll()
    {
        var result = new List<StoredEvent>();
        await using var connection = await database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence, stream_id, event_type, data, occurred_at FROM events ORDER BY sequence";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new StoredEvent(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetString(2),
                reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4))));
        return result;
    }
}

public sealed class TokenService(JwtOptions options)
{
    public TokenResult Create(string username)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var sessionId = Guid.NewGuid().ToString("N");
        var token = new JwtSecurityToken(options.Issuer, options.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, username), new Claim(ClaimTypes.Name, username),
             new Claim(JwtRegisteredClaimNames.Jti, sessionId)],
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)),
                SecurityAlgorithms.HmacSha256));
        return new TokenResult(new JwtSecurityTokenHandler().WriteToken(token), sessionId, expiresAt);
    }

    public static TokenValidationParameters ValidationParameters(JwtOptions options) => new()
    {
        ValidateIssuer = true, ValidIssuer = options.Issuer,
        ValidateAudience = true, ValidAudience = options.Audience,
        ValidateLifetime = true, ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)),
        ClockSkew = TimeSpan.FromSeconds(15)
    };
}

public sealed record TokenResult(string Token, string SessionId, DateTimeOffset ExpiresAt);

public sealed class SqliteDatabase(string connectionString)
{
    public async Task<SqliteConnection> OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }
}

public partial class Program;

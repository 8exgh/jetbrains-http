using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JetBrainsHttpDemo.Api.Tests;

public sealed class ApiIntegrationTests
{
    [Fact]
    public async Task Json_login_returns_a_signed_jwt_and_creates_a_session()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest("demo", "demo-password"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        login.Should().NotBeNull();
        login!.Token.Should().NotBeNullOrWhiteSpace();
        login.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(55));
        new JwtSecurityTokenHandler().ReadJwtToken(login.Token).Claims.Should()
            .Contain(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == "demo");

        await using var connection = await factory.Services.GetRequiredService<SqliteDatabase>().OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions WHERE username = 'demo'";
        Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("demo", "wrong")]
    [InlineData("wrong", "demo-password")]
    public async Task Invalid_credentials_are_rejected(string username, string password)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(username, password));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OAuth_password_login_returns_standard_token_fields()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["username"] = "demo", ["password"] = "demo-password",
            ["client_id"] = "jetbrains-http-client"
        });

        var response = await client.PostAsync("/auth/login", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        json.RootElement.GetProperty("token_type").GetString().Should().Be("Bearer");
        json.RootElement.GetProperty("expires_in").GetInt32().Should().Be(3600);
    }

    [Theory]
    [InlineData(HttpMethodKind.Get)]
    [InlineData(HttpMethodKind.Post)]
    public async Task Protected_endpoints_require_authentication(HttpMethodKind method)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var request = method == HttpMethodKind.Get
            ? new HttpRequestMessage(HttpMethod.Get, "/api/tasks")
            : new HttpRequestMessage(HttpMethod.Post, "/api/tasks") { Content = JsonContent.Create(new CreateTask("task", null)) };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_command_appends_an_event_and_returns_created_resource()
    {
        using var factory = new ApiFactory();
        using var client = await AuthenticatedClient(factory);

        var response = await client.PostAsJsonAsync("/api/tasks", new CreateTask("  First task  ", "  details  "));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<TaskCreated>();
        created.Should().BeEquivalentTo(new { Title = "First task", Description = "details", Status = "open", CreatedBy = "demo" });
        response.Headers.Location.Should().Be($"/api/tasks/{created!.Id}");

        await using var connection = await factory.Services.GetRequiredService<SqliteDatabase>().OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_type, stream_id FROM events";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be(nameof(TaskCreated));
        reader.GetString(1).Should().Be(created.Id.ToString());
    }

    [Fact]
    public async Task Blank_title_is_rejected_without_appending_an_event()
    {
        using var factory = new ApiFactory();
        using var client = await AuthenticatedClient(factory);

        var response = await client.PostAsJsonAsync("/api/tasks", new CreateTask("  ", null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Title is required");
        await using var connection = await factory.Services.GetRequiredService<SqliteDatabase>().OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM events";
        Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Query_replays_events_and_filters_title_and_description_case_insensitively()
    {
        using var factory = new ApiFactory();
        using var client = await AuthenticatedClient(factory);
        await Create(client, "JetBrains client", "manual calls");
        await Create(client, "Other title", "Uses JETBRAINS tooling");
        await Create(client, "Unrelated", "nothing to match");

        var result = await client.GetFromJsonAsync<PagedTasks>("/api/tasks?search=jetbrains");

        result.Should().NotBeNull();
        result!.Total.Should().Be(2);
        result.Items.Select(x => x.Title).Should().BeEquivalentTo(["JetBrains client", "Other title"]);
    }

    [Fact]
    public async Task Query_filters_status_and_returns_no_matches_for_unknown_status()
    {
        using var factory = new ApiFactory();
        using var client = await AuthenticatedClient(factory);
        await Create(client, "Open task", null);

        var open = await client.GetFromJsonAsync<PagedTasks>("/api/tasks?status=OPEN");
        var closed = await client.GetFromJsonAsync<PagedTasks>("/api/tasks?status=closed");

        open!.Total.Should().Be(1);
        closed!.Total.Should().Be(0);
        closed.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Query_paginates_in_reverse_creation_order()
    {
        using var factory = new ApiFactory();
        using var client = await AuthenticatedClient(factory);
        for (var i = 1; i <= 5; i++)
            await Create(client, $"Task {i}", null);

        var result = await client.GetFromJsonAsync<PagedTasks>("/api/tasks?page=2&pageSize=2");

        result.Should().NotBeNull();
        result!.Should().BeEquivalentTo(new { Page = 2, PageSize = 2, Total = 5 });
        result.Items.Select(x => x.Title).Should().ContainInOrder("Task 3", "Task 2");
    }

    [Fact]
    public async Task Query_normalizes_invalid_page_values_and_caps_page_size()
    {
        using var factory = new ApiFactory();
        using var client = await AuthenticatedClient(factory);

        var defaults = await client.GetFromJsonAsync<PagedTasks>("/api/tasks?page=0&pageSize=0");
        var capped = await client.GetFromJsonAsync<PagedTasks>("/api/tasks?page=1&pageSize=1000");

        defaults.Should().BeEquivalentTo(new { Page = 1, PageSize = 10, Total = 0 });
        capped.Should().BeEquivalentTo(new { Page = 1, PageSize = 100, Total = 0 });
    }

    [Fact]
    public async Task Jwt_is_rejected_after_its_server_side_session_is_removed()
    {
        using var factory = new ApiFactory();
        using var client = await AuthenticatedClient(factory);
        await using (var connection = await factory.Services.GetRequiredService<SqliteDatabase>().OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM sessions";
            await command.ExecuteNonQueryAsync();
        }

        var response = await client.GetAsync("/api/tasks");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpClient> AuthenticatedClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest("demo", "demo-password"));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return client;
    }

    private static async Task Create(HttpClient client, string title, string? description)
        => (await client.PostAsJsonAsync("/api/tasks", new CreateTask(title, description))).EnsureSuccessStatusCode();

    public enum HttpMethodKind { Get, Post }
}

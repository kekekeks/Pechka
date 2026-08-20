using System.Net.Http.Json;
using MyWebApp;
using Pechka.AspNet.TestHelpers;

namespace MyWebApp.Tests;

public class HttpApiTests : IClassFixture<TestEnv>
{
    private readonly TestEnv _env;

    public HttpApiTests(TestEnv env) => _env = env;

    private async Task<string[]> Names(HttpClient http) =>
        (await http.GetFromJsonAsync<TodoItem[]>("/api/todo"))!.Select(x => x.Name).ToArray();

    [Fact]
    public async Task Controller_Round_Trip()
    {
        using var http = _env.CreateClient();
        var first = TestData.Unique("http-first");
        var second = TestData.Unique("http-second");

        var response = await http.PostAsync($"/api/todo/pair?first={first}&second={second}", null);

        response.EnsureSuccessStatusCode();
        var names = await Names(http);
        Assert.Contains(first, names);
        Assert.Contains(second, names);
    }

    [Fact]
    public async Task Pipeline_Level_Retry_Replays_The_Buffered_Body()
    {
        using var http = _env.CreateClient();
        var name = TestData.Unique("http-flaky");

        var response = await http.PostAsJsonAsync("/api/todo/flaky", new { name });

        response.EnsureSuccessStatusCode();
        Assert.Contains(name, await Names(http));
    }

    [Fact]
    public async Task A_Failing_Action_Rolls_Its_Writes_Back()
    {
        using var http = _env.CreateClient();
        var name = TestData.Unique("http-rolled-back");

        var response = await http.PostAsync($"/api/todo/pair-failing?first={name}", null);

        Assert.False(response.IsSuccessStatusCode);
        Assert.DoesNotContain(name, await Names(http));
    }
}

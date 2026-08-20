using Microsoft.Playwright;
using MyWebApp;
using Pechka.AspNet.TestHelpers;

namespace MyWebApp.Tests;

[Collection("E2E")]
[Trait("Category", "E2E")]
public class SmokeE2ETests : PechkaE2ETestBase<MyWebAppTestApp>, IClassFixture<TestEnv>, IAsyncLifetime
{
    public SmokeE2ETests(TestEnv env, PlaywrightFixture playwright) : base(env, playwright)
    {
    }

    [Fact]
    public async Task The_Spa_Builds_And_Renders_Against_A_Lane()
    {
        await Page.GotoAsync("/");
        await ExpectVisible("greeting");
        await Assertions.Expect(TestId("greeting")).ToHaveTextAsync("Hello World");
    }
}

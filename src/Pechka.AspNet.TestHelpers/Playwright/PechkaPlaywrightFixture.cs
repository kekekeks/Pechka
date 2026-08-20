using Microsoft.Playwright;

namespace Pechka.AspNet.TestHelpers;

/// <summary>
/// One Chromium per E2E collection, self-installing and headless by default
/// (<c>PLAYWRIGHT_HEADED=1</c> to watch). Ensures the generated TS API and the frontend build
/// exist first, so a fresh clone can run E2E without manual steps. Use it as a collection
/// fixture, not an assembly one, so backend-only runs never build the SPA or launch a browser;
/// the <c>[CollectionDefinition]</c> carrying it is xunit-specific and lives in the consuming
/// test project:
/// <code>
/// [CollectionDefinition("E2E", DisableParallelization = true)]
/// public class E2ECollection : ICollectionFixture&lt;PlaywrightFixture&gt;;
/// </code>
/// where <c>PlaywrightFixture</c> derives from this class and implements the test framework's
/// async-lifetime interface by forwarding to <see cref="InitializeAsync"/>/<see cref="DisposeAsync"/>
/// (implicit with xunit v2).
/// </summary>
public class PechkaPlaywrightFixture<TApp> : IAsyncDisposable where TApp : PechkaTestApp, new()
{
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;

    public virtual async Task InitializeAsync()
    {
        var app = new TApp();
        await app.EnsureTsApiGeneratedAsync();
        await app.EnsureFrontendBuiltAsync();
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0)
            throw new InvalidOperationException($"'playwright install chromium' failed ({exitCode})");
        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADED") != "1",
        });
    }

    public virtual async Task DisposeAsync()
    {
        if (Browser is { } browser)
            await browser.DisposeAsync();
        _playwright?.Dispose();
    }

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();
}

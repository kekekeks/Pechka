using Microsoft.Playwright;

namespace Pechka.AspNet.TestHelpers;

/// <summary>
/// Base for browser tests: per test (via the test framework's async lifetime calling
/// <see cref="InitializeAsync"/>) a fresh browser context — own cookies and storage — pointed at
/// the same in-process host the integration tests use, so a scenario seeds state over typed RPC
/// and then drives the real page. Selection helpers go through <c>data-testid</c>.
/// </summary>
public abstract class PechkaE2ETestBase<TApp> where TApp : PechkaTestApp, new()
{
    private readonly PechkaPlaywrightFixture<TApp> _playwright;
    private IBrowserContext? _context;
    private IPage? _page;

    protected PechkaE2ETestBase(PechkaTestEnv<TApp> env, PechkaPlaywrightFixture<TApp> playwright)
    {
        Env = env;
        _playwright = playwright;
    }

    protected PechkaTestEnv<TApp> Env { get; }

    protected IPage Page => _page ?? throw new InvalidOperationException(
        "No page — the test framework must run InitializeAsync before the test body.");

    public virtual async Task InitializeAsync()
    {
        _context = await _playwright.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = Env.BaseUrl,
        });
        _page = await _context.NewPageAsync();
    }

    public virtual async Task DisposeAsync()
    {
        if (_context is { } context)
            await context.DisposeAsync();
        _context = null;
        _page = null;
    }

    protected ILocator TestId(string id) => Page.GetByTestId(id);

    protected Task ExpectVisible(string testId) => Assertions.Expect(TestId(testId)).ToBeVisibleAsync();

    protected Task FillAsync(string testId, string value) => TestId(testId).FillAsync(value);
}

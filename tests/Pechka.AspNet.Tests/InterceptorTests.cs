using CoreRPC.Transferable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Tests;

public class InterceptorTests : SqliteTestBase
{
    public InterceptorTests(SqliteTestDatabase db) : base(db)
    {
    }

    public class RpcTarget
    {
        public Task<object> Work() => Task.FromResult<object>(null!);

        [NoTransaction]
        public Task<object> WithoutTransaction() => Task.FromResult<object>(null!);

        [NoRetry]
        public Task<object> WithoutRetry() => Task.FromResult<object>(null!);
    }

    [NoTransaction]
    public class NoTransactionTarget
    {
        public Task<object> Work() => Task.FromResult<object>(null!);
    }

    private static PechkaDbTransactionOptions Options(bool enableRetries = false) => new()
    {
        EnableRetries = enableRetries,
        RetryMaxAttempts = 3,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        RetryMaxDelay = TimeSpan.FromMilliseconds(2),
        RetryBudgetMaxRetries = 100,
        RetryBudgetWindow = TimeSpan.FromMinutes(1),
        IsTransientFailure = e => e is FakeTransientException
    };

    private static TransactionMethodCallInterceptor Interceptor(PechkaDbTransactionOptions options) =>
        new(options, NullLogger<TransactionMethodCallInterceptor>.Instance);

    private static MethodCall Call(object target, string method) => new()
    {
        Target = target,
        Method = target.GetType().GetMethod(method)!,
        Arguments = Array.Empty<object>()
    };

    private ServiceProvider BuildServices(bool withManager = true)
    {
        var services = new ServiceCollection();
        if (withManager)
        {
            services.AddScoped(_ => Db.CreateManager());
            services.AddScoped<ITransactionalDbContextManager>(sp => sp.GetRequiredService<TestDbManager>());
        }
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext HttpFor(AsyncServiceScope scope) =>
        new() { RequestServices = scope.ServiceProvider };

    [Fact]
    public async Task Commits_On_Success()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<TestDbManager>();

        await Interceptor(Options()).Intercept(Call(new RpcTarget(), nameof(RpcTarget.Work)), HttpFor(scope),
            async () =>
            {
                Assert.NotNull(manager.CurrentTransaction);
                await Insert(manager, "a");
                return null!;
            });

        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task Rolls_Back_When_The_Handler_Throws()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<TestDbManager>();

        await Assert.ThrowsAsync<FakePermanentException>(() => Interceptor(Options())
            .Intercept(Call(new RpcTarget(), nameof(RpcTarget.Work)), HttpFor(scope), async () =>
            {
                await Insert(manager, "a");
                throw new FakePermanentException();
            }));

        Assert.Empty(await ReadItemNames());
    }

    [Fact]
    public async Task NoTransaction_On_The_Method_Skips_The_Scope()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<TestDbManager>();

        await Assert.ThrowsAsync<FakePermanentException>(() => Interceptor(Options())
            .Intercept(Call(new RpcTarget(), nameof(RpcTarget.WithoutTransaction)), HttpFor(scope), async () =>
            {
                Assert.Null(manager.CurrentTransaction);
                await Insert(manager, "a");
                throw new FakePermanentException();
            }));

        // No scope means the write autocommitted despite the failure
        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task NoTransaction_On_The_Target_Type_Skips_The_Scope()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<TestDbManager>();

        await Interceptor(Options()).Intercept(
            Call(new NoTransactionTarget(), nameof(NoTransactionTarget.Work)), HttpFor(scope), () =>
            {
                Assert.Null(manager.CurrentTransaction);
                return Task.FromResult<object>(null!);
            });
    }

    [Fact]
    public async Task A_Transient_Failure_Re_Runs_The_Call_When_Retries_Are_Enabled()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<TestDbManager>();
        var invocations = 0;

        var result = await Interceptor(Options(enableRetries: true))
            .Intercept(Call(new RpcTarget(), nameof(RpcTarget.Work)), HttpFor(scope), async () =>
            {
                invocations++;
                await Insert(manager, "a");
                if (invocations == 1)
                    throw new FakeTransientException();
                return "done";
            });

        Assert.Equal("done", result);
        Assert.Equal(2, invocations);
        // The first attempt's write was rolled back
        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task NoRetry_Keeps_A_Single_Invocation()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<TestDbManager>();
        var invocations = 0;

        await Assert.ThrowsAsync<FakeTransientException>(() => Interceptor(Options(enableRetries: true))
            .Intercept(Call(new RpcTarget(), nameof(RpcTarget.WithoutRetry)), HttpFor(scope), async () =>
            {
                invocations++;
                await Insert(manager, "a");
                throw new FakeTransientException();
            }));

        Assert.Equal(1, invocations);
        Assert.Empty(await ReadItemNames());
    }

    [Fact]
    public async Task Disabled_Rpc_Interception_Skips_The_Scope()
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<TestDbManager>();
        var options = Options();
        options.InterceptRpcCalls = false;

        await Interceptor(options).Intercept(Call(new RpcTarget(), nameof(RpcTarget.Work)), HttpFor(scope),
            () =>
            {
                Assert.Null(manager.CurrentTransaction);
                return Task.FromResult<object>(null!);
            });
    }

    [Fact]
    public async Task A_Non_Http_Context_Is_Passed_Through()
    {
        var invoked = false;
        await Interceptor(Options()).Intercept(Call(new RpcTarget(), nameof(RpcTarget.Work)), new object(),
            () =>
            {
                invoked = true;
                return Task.FromResult<object>(null!);
            });
        Assert.True(invoked);
    }

    [Fact]
    public async Task Without_Registered_Managers_The_Call_Is_Passed_Through()
    {
        await using var provider = BuildServices(withManager: false);
        await using var scope = provider.CreateAsyncScope();
        var invoked = false;

        await Interceptor(Options()).Intercept(Call(new RpcTarget(), nameof(RpcTarget.Work)), HttpFor(scope),
            () =>
            {
                invoked = true;
                return Task.FromResult<object>(null!);
            });

        Assert.True(invoked);
    }
}

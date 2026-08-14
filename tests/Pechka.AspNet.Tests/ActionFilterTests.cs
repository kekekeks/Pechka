using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Tests;

public class ActionFilterTests : SqliteTestBase
{
    public ActionFilterTests(SqliteTestDatabase db) : base(db)
    {
    }

    public class TestController : Controller
    {
        public void Action()
        {
        }

        [NoTransaction]
        public void WithoutTransaction()
        {
        }
    }

    [NoTransaction]
    public class NoTransactionController : Controller
    {
        public void Action()
        {
        }
    }

    private static ActionDescriptor Descriptor(Type controller, string method) => new ControllerActionDescriptor
    {
        MethodInfo = controller.GetMethod(method)!,
        ControllerTypeInfo = controller.GetTypeInfo(),
        DisplayName = $"{controller.Name}.{method}"
    };

    private static TransactionActionFilter Filter(ITransactionalDbContextManager manager,
        PechkaDbTransactionOptions? options = null, bool withManager = true) =>
        new(withManager ? new[] { manager } : Array.Empty<ITransactionalDbContextManager>(),
            options ?? new PechkaDbTransactionOptions(), NullLogger<TransactionActionFilter>.Instance);

    private static async Task Run(TransactionActionFilter filter, ActionDescriptor descriptor,
        Func<Task> body, Exception? exception = null, bool exceptionHandled = false)
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor);
        var filters = new List<IFilterMetadata>();
        var controller = new object();
        var executing = new ActionExecutingContext(actionContext, filters,
            new Dictionary<string, object?>(), controller);

        await filter.OnActionExecutionAsync(executing, async () =>
        {
            await body();
            return new ActionExecutedContext(actionContext, filters, controller)
            {
                Exception = exception,
                ExceptionHandled = exceptionHandled
            };
        });
    }

    [Fact]
    public async Task Commits_When_The_Action_Succeeds()
    {
        await using var manager = Db.CreateManager();
        await Run(Filter(manager), Descriptor(typeof(TestController), nameof(TestController.Action)), async () =>
        {
            Assert.NotNull(manager.CurrentTransaction);
            await Insert(manager, "a");
        });
        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task Rolls_Back_When_The_Action_Reports_An_Exception()
    {
        await using var manager = Db.CreateManager();
        await Run(Filter(manager), Descriptor(typeof(TestController), nameof(TestController.Action)),
            () => Insert(manager, "a"), exception: new FakePermanentException());
        Assert.Empty(await ReadItemNames());
    }

    [Fact]
    public async Task Commits_When_The_Exception_Was_Handled()
    {
        await using var manager = Db.CreateManager();
        await Run(Filter(manager), Descriptor(typeof(TestController), nameof(TestController.Action)),
            () => Insert(manager, "a"), exception: new FakePermanentException(), exceptionHandled: true);
        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task NoTransaction_On_The_Action_Skips_The_Scope()
    {
        await using var manager = Db.CreateManager();
        await Run(Filter(manager),
            Descriptor(typeof(TestController), nameof(TestController.WithoutTransaction)), async () =>
            {
                Assert.Null(manager.CurrentTransaction);
                await Insert(manager, "a");
            }, exception: new FakePermanentException());
        // Without a scope the write autocommits regardless of the action's outcome
        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task NoTransaction_On_The_Controller_Skips_The_Scope()
    {
        await using var manager = Db.CreateManager();
        await Run(Filter(manager),
            Descriptor(typeof(NoTransactionController), nameof(NoTransactionController.Action)),
            () =>
            {
                Assert.Null(manager.CurrentTransaction);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Disabled_Mvc_Interception_Skips_The_Scope()
    {
        await using var manager = Db.CreateManager();
        var options = new PechkaDbTransactionOptions { InterceptMvcActions = false };
        await Run(Filter(manager, options), Descriptor(typeof(TestController), nameof(TestController.Action)),
            () =>
            {
                Assert.Null(manager.CurrentTransaction);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Without_Managers_The_Action_Runs_Unwrapped()
    {
        await using var manager = Db.CreateManager();
        var invoked = false;
        await Run(Filter(manager, withManager: false),
            Descriptor(typeof(TestController), nameof(TestController.Action)), () =>
            {
                invoked = true;
                Assert.Null(manager.CurrentTransaction);
                return Task.CompletedTask;
            });
        Assert.True(invoked);
    }
}

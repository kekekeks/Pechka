using Pechka.AspNet;
using Pechka.AspNet.Database;
using Pechka.AspNet.Jobs;

namespace MyWebApp;

/// <summary>The app's composition, exposed as a builder so a test harness can start the real app
/// in-process (Pechka.AspNet.TestHelpers' PechkaTestApp.CreateProgram seam).</summary>
public static class MyWebAppProgram
{
    public static IPechkaProgramBuilderExecutable Create(string[] args) =>
        PechkaProgramBuilder<TodoRpc>
            .Create(args)
            .ConfigureServices(ConfigureServices)
            .ConfigureApp(Configure);

    static PechkaConfiguration ConfigureServices(IConfiguration configuration, IServiceCollection services)
    {
        services.AddControllers();
        services.AddTransactionalDbContextManager((dp, c) => new MyDbContextManager(dp, c),
            configure: o => o.EnableRetries = true);
        services.AddBackgroundJobs<MyDbContextManager>(o => o.CompletedJobRetention = TimeSpan.FromHours(1));
        services.AddBackgroundJob<GreetingJob, GreetingJobHandler>();
        services.AddBackgroundJob<FlakyJob, FlakyJobHandler>();
        services.AddBackgroundJob<TransientJob, TransientJobHandler>(retryTransientFailures: true);
        return new PechkaConfiguration
        {
            WebAppApiPath = Path.Combine("webapp", "src", "api.ts"),
        };
    }

    static void Configure(WebHostBuilderContext ctx, IApplicationBuilder app)
    {
        app.UseRouting();
        app.UsePechkaTransactionRetries();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
    }
}

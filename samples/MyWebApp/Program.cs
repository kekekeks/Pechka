using MyWebApp;
using Pechka.AspNet;
using Pechka.AspNet.Database;
using Pechka.AspNet.Jobs;


PechkaConfiguration ConfigureServices(IConfiguration configuration, IServiceCollection services)
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

void Configure(WebHostBuilderContext ctx, IApplicationBuilder app)
{
    app.UseRouting();
    app.UsePechkaTransactionRetries();
    app.UseEndpoints(endpoints =>
    {
        endpoints.MapControllers();
    });
}

void CustomizeHost(IHostBuilder hostBuilder, IConfiguration args)
{
    
}

PechkaProgramBuilder<Program>
    .Create(args)
    .ConfigureServices(ConfigureServices)
    .ConfigureApp(Configure)
    .CustomizeHost(CustomizeHost)
    .Run();
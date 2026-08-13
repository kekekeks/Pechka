using MyWebApp;
using Pechka.AspNet;
using Pechka.AspNet.Jobs;


PechkaConfiguration ConfigureServices(IConfiguration configuration, IServiceCollection services)
{
    services.AddControllers();
    services.AddTransactionalDbContextManager((dp, c) => new MyDbContextManager(dp, c));
    services.AddBackgroundJobs<MyDbContextManager>(o => o.CompletedJobRetention = TimeSpan.FromHours(1));
    services.AddBackgroundJob<GreetingJob, GreetingJobHandler>();
    services.AddBackgroundJob<FlakyJob, FlakyJobHandler>();
    return new PechkaConfiguration
    {
        WebAppApiPath = Path.Combine("webapp", "src", "api.ts"),
    };
}

void Configure(WebHostBuilderContext ctx, IApplicationBuilder app)
{
    app.UseRouting();
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
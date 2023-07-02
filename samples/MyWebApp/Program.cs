using MyWebApp;
using Pechka.AspNet;


void ConfigureServices(IConfiguration configuration, IServiceCollection services)
{
    services.AddSingleton(new PechkaConfiguration()
    {
        WebAppRoot = "webapp",
        WebAppApiPath = "webapp/src/api.ts",
        WebAppBuildPath = "webapp/build"
    });
    services.AddControllers();
    services.AddDbContextManager((dp, c) => new MyDbContextManager(dp, c));
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
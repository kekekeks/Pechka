using MyWebApp;
using Pechka.AspNet;


void ConfigureServices(IConfiguration configuration, IServiceCollection services)
{
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
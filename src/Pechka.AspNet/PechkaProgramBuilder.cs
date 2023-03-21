using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pechka.AspNet.BackgroundServices;
using Pechka.AspNet.Cmdlets;

namespace Pechka.AspNet;

public interface IPechkaProgramBuilderExecutable
{
    IPechkaProgramBuilderExecutable CustomizeHost(Action<IHostBuilder, IConfiguration> f);
    int Run();
}

public interface IPechkaProgramBuilderWithServices
{
    IPechkaProgramBuilderExecutable ConfigureApp(Action<WebHostBuilderContext, IApplicationBuilder> f);
}

public interface IPechkaProgramBuilderMain
{
    IPechkaProgramBuilderWithServices ConfigureServices(Action<IConfiguration, IServiceCollection> f);
}

public class PechkaProgramBuilder<TAssembly> : IPechkaProgramBuilderMain, IPechkaProgramBuilderWithServices, IPechkaProgramBuilderExecutable
{
    private readonly IHostBuilder _host;
    private readonly IConfiguration _configuration;
    private readonly string[] _args;
    private Action<IConfiguration, IServiceCollection> _customServicesConfigure;
    private Action<WebHostBuilderContext, IApplicationBuilder> _customAppConfigure;
    private Action<IHostBuilder, IConfiguration>? _customization;

    public static IPechkaProgramBuilderMain Create(string[] args) => 
        new PechkaProgramBuilder<TAssembly>(args);
    
    internal PechkaProgramBuilder(string[] args)
    {
        _configuration = new ConfigurationBuilder().AddCommandLine(args).Build();
        _host = Host.CreateDefaultBuilder();
        _args = args;
    }

    private void ResolveHost()
    {
        var runningFromSources = File.Exists(Path.Combine("obj", "project.assets.json"));
        var appDirectory = runningFromSources ? Directory.GetCurrentDirectory() : AppDomain.CurrentDomain.BaseDirectory;
        var cmdLineConfig = _configuration;
        var appAssembly = typeof(TAssembly).Assembly;

        var builder = _host
            .UseContentRoot(appDirectory)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSystemdConsole(opts =>
                {
                    opts.UseUtcTimestamp = true;
                    opts.TimestampFormat = "yyyy-MM-dd HH:mm:ss";
                });
            })
            .ConfigureAppConfiguration((hb, cb) =>
            {
                var configPath = cmdLineConfig["config"];
                cb.Sources.Clear();
                cb.AddJsonFile("config.defaults.json")
                    .AddJsonFile("config.local.json", true);
                if (configPath != null)
                    cb.AddJsonFile(configPath);
                cb
                    .AddEnvironmentVariables(prefix: "ASPNETCORE_")
                    .AddCommandLine(_args);
            });

        builder.ConfigureServices((ctx, services) =>
        {
            services.AddSingleton<RuntimeAppInfo>();
            services.AddSingleton(new RuntimeProgramInfo
            {
                IsRunningFromSource = runningFromSources,
                ContentRoot = appDirectory,
                RootAssembly = appAssembly
            });
            new ServiceRunnerRegistry(appAssembly).Register(services);
            services.AddSingleton<TickingServiceManager>();
            services.AddSingleton<ITickingServiceManager>(p => p.GetRequiredService<TickingServiceManager>());
            services.AddSingleton<TsInterop>();
            services.AddLogging();
            
            _customServicesConfigure(ctx.Configuration, services);
        });
        
        ResolveRoles();
        _customization?.Invoke(_host, _configuration);
    }

    private void ResolveRoles()
    {
        var roles = CmdletManager.IsCommand(_args) ? 
            Array.Empty<string>() : (_configuration["roles"] ?? "all").Split(',');
        
        if (roles.Contains("web") || roles.Contains("all"))
            _host.ConfigureWebHost(web =>
            {
                web
                    .ConfigureServices(services =>
                    {
                        services.AddTransient<IStartupFilter, PechkaStartupFilter>();
                    })
                    .Configure(_customAppConfigure)
                    .UseKestrel();
            });

        if (roles.Contains("services") || roles.Contains("all"))
            _host.ConfigureServices(services =>
                services.AddHostedService(p => p.GetRequiredService<TickingServiceManager>()));
    }

    public IPechkaProgramBuilderExecutable CustomizeHost(Action<IHostBuilder, IConfiguration> f)
    {
        _customization = f;
        return this;
    }

    public int Run()
    {
        ResolveHost();
        if (CmdletManager.IsCommand(_args))
            return CmdletManager.Execute(_ => _host, typeof(TAssembly).Assembly, _args);
        _host.Build().Run();
        return 0;
    }

    public IPechkaProgramBuilderWithServices ConfigureServices(Action<IConfiguration, IServiceCollection> f)
    {
        _customServicesConfigure = f;
        return this;
    }

    public IPechkaProgramBuilderExecutable ConfigureApp(Action<WebHostBuilderContext, IApplicationBuilder> f)
    {
        _customAppConfigure = f;
        return this;
    }
}
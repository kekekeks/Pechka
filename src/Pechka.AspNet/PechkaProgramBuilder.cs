using System;
using System.IO;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    IPechkaProgramBuilderWithServices ConfigureServices(Func<IConfiguration, IServiceCollection, PechkaConfiguration> f);
}

public class PechkaProgramBuilder<TAssembly> : IPechkaProgramBuilderMain, IPechkaProgramBuilderWithServices, IPechkaProgramBuilderExecutable
{
    private readonly IHostBuilder _host;
    private readonly string[] _originalArgs;
    private Func<IConfiguration, IServiceCollection, PechkaConfiguration> _customServicesConfigure;
    private Action<WebHostBuilderContext, IApplicationBuilder> _customAppConfigure;
    private Action<IHostBuilder, IConfiguration>? _customization;

    public static IPechkaProgramBuilderMain Create(string[] args) => 
        new PechkaProgramBuilder<TAssembly>(args);
    
    internal PechkaProgramBuilder(string[] args)
    {
        _host = Host.CreateDefaultBuilder();
        _originalArgs = args;
    }

    private void ResolveHost(string[] args)
    {
        var runningFromSources = File.Exists(Path.Combine("obj", "project.assets.json"));
        var appDirectory = runningFromSources ? Directory.GetCurrentDirectory() : AppDomain.CurrentDomain.BaseDirectory;
        var cmdLineConfig =  new ConfigurationBuilder().AddCommandLine(args).Build();;
        var appAssembly = typeof(TAssembly).Assembly;

        var builder = _host
            .UseContentRoot(appDirectory)
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
                    .AddCommandLine(args);
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
            
            services.AddSingleton<TickingServiceManager>();
            services.AddSingleton<ITickingServiceManager>(p => p.GetRequiredService<TickingServiceManager>());
            services.AddSingleton<TsInterop>();
            services.AddLogging();
            
            
            var pechkaConfig = _customServicesConfigure(ctx.Configuration, services);
            services.AddSingleton(pechkaConfig);
            var pechkaJsonConfig = ctx.Configuration.GetSection("Pechka").Get<PechkaJsonConfig>();
            new ServiceRunnerRegistry(appAssembly).Register(services);
            services.AddSingleton(pechkaJsonConfig ?? new());
            services.AddSingleton<CustomForwardedHeadersMiddleware>();
        });
        
        ResolveRoles(cmdLineConfig);
        _host.UseSystemd();
        _customization?.Invoke(_host, cmdLineConfig);
    }

    private void ResolveRoles(IConfiguration cmdLineConfig)
    {
        var roles = CmdletManager.IsCommand(_originalArgs) ? 
            Array.Empty<string>() : (cmdLineConfig["roles"] ?? "all").Split(',');
        
        if (roles.Contains("web") || roles.Contains("all"))
            _host.ConfigureWebHost(web =>
            {
                web
                    .ConfigureServices(services =>
                    {
                        services.AddTransient<IStartupFilter, PechkaStartupFilter>();
                        services.AddControllers().AddApplicationPart(typeof(TAssembly).Assembly);
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
        if (CmdletManager.IsCommand(_originalArgs))
            return CmdletManager.Execute(args =>
            {
                ResolveHost(args);
                return _host;
            }, typeof(TAssembly).Assembly, _originalArgs);

        ResolveHost(_originalArgs);
        _host.Build().Run();
        return 0;
    }

    public IPechkaProgramBuilderWithServices ConfigureServices(Func<IConfiguration, IServiceCollection, PechkaConfiguration> f)
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
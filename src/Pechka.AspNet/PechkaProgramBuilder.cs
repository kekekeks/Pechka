using System;
using System.IO;
using System.Linq;
using System.Net;
using LinqToDB.Tools;
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
using Pechka.AspNet.Database;

namespace Pechka.AspNet;

public interface IPechkaProgramBuilderExecutable
{
    IPechkaProgramBuilderExecutable CustomizeHost(Action<IHostBuilder, IConfiguration> f);
    int Run();
    IHost CreateHost();
}

public interface IPechkaProgramBuilderWithServices
{
    IPechkaProgramBuilderExecutable ConfigureApp(Action<WebHostBuilderContext, IApplicationBuilder> f);
}

public interface IPechkaProgramBuilderMain
{
    IPechkaProgramBuilderMain WithConfigCustomization(Action<IConfigurationBuilder> cb);
    IPechkaProgramBuilderWithServices ConfigureServices(Func<IConfiguration, IServiceCollection, PechkaConfiguration> f);
}

public class PechkaProgramBuilder<TAssembly> : IPechkaProgramBuilderMain, IPechkaProgramBuilderWithServices, IPechkaProgramBuilderExecutable
{
    private readonly IHostBuilder _host;
    private readonly string[] _originalArgs;
    private Func<IConfiguration, IServiceCollection, PechkaConfiguration> _customServicesConfigure;
    private Action<WebHostBuilderContext, IApplicationBuilder> _customAppConfigure;
    private Action<IHostBuilder, IConfiguration>? _customization;
    private Action<IConfigurationBuilder>? _customConfigBuilder;

    public static IPechkaProgramBuilderMain Create(string[] args) => 
        new PechkaProgramBuilder<TAssembly>(args);
    
    internal PechkaProgramBuilder(string[] args)
    {
        _host = Host.CreateDefaultBuilder();
        _originalArgs = args;
    }

    private void ResolveHost(string[] args)
    {
        var cmdLineConfig =  new ConfigurationBuilder().AddCommandLine(args).Build();
        // --contentRoot lets another process (e.g. a test harness) run the app against its source
        // directory: config.defaults.json, web app paths and from-source detection resolve there
        var appDirectory = cmdLineConfig["contentRoot"] is { } contentRoot
            ? Path.GetFullPath(contentRoot)
            : File.Exists(Path.Combine("obj", "project.assets.json"))
                ? Directory.GetCurrentDirectory()
                : AppDomain.CurrentDomain.BaseDirectory;
        var runningFromSources = File.Exists(Path.Combine(appDirectory, "obj", "project.assets.json"));
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
                _customConfigBuilder?.Invoke(cb);
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
            // The app's calendar clock; after the app delegate so an app-supplied provider wins
            services.TryAddSingleton(TimeProvider.System);
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

        var webRole = roles.Contains("web") || roles.Contains("all");
        // The web role runs migrations (PechkaStartupFilter) and completes the signal then;
        // without it the schema is assumed to be managed by another process
        _host.ConfigureServices(services => services.AddSingleton(new DatabaseReadySignal(!webRole)));

        if (webRole)
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
            {
                services.AddHostedService(p => p.GetRequiredService<TickingServiceManager>());
                services.AddHostedService<PechkaBackgroundWorkersRunner>();
            });
    }

    public IPechkaProgramBuilderExecutable CustomizeHost(Action<IHostBuilder, IConfiguration> f)
    {
        _customization += f;
        return this;
    }

    public IHost CreateHost()
    {
        ResolveHost(_originalArgs);
        return _host.Build();
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

    public IPechkaProgramBuilderMain WithConfigCustomization(Action<IConfigurationBuilder> cb)
    {
        _customConfigBuilder = cb;
        return this;
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
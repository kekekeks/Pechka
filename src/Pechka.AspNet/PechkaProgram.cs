using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pechka.AspNet.BackgroundServices;
using Pechka.AspNet.Cmdlets;

namespace Pechka.AspNet;

public class PechkaProgram
{
    public static int Main<TStartup>(string[] args, Action<IHostBuilder, IConfiguration>? customize = null)
    {
        var hostBuilder = (string[] a, bool cmdlet) => CreateHostBuilder(typeof(TStartup), a, cmdlet, customize);
        if (CmdletManager.IsCommand(args))
            return CmdletManager.Execute(a => hostBuilder(a, true), typeof(TStartup).Assembly, args);
        hostBuilder(args, false).Build().Run();
        return 0;
    }

    private static IHostBuilder CreateHostBuilder(Type startup, string[] args,
        bool cmdlet,
        Action<IHostBuilder, IConfiguration>? customize = null)
    {
        var runningFromSources = File.Exists(Path.Combine("obj", "project.assets.json"));
        var appDirectory = runningFromSources
            ? Directory.GetCurrentDirectory()
            : AppDomain.CurrentDomain.BaseDirectory;

        var cmdLineConfig = new ConfigurationBuilder()
            .AddCommandLine(args).Build();

        var builder = Host.CreateDefaultBuilder()
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
                    .AddCommandLine(args);
            });

        var roles =
            cmdlet ? Array.Empty<string>() : (cmdLineConfig["roles"] ?? "all").Split(',');
        
        if (roles.Contains("web") || roles.Contains("all"))
            builder.ConfigureWebHost(web =>
            {
                web
                    .UseStartup(startup)
                    .ConfigureServices(services =>
                    {
                        services.AddTransient<IStartupFilter, PechkaStartupFilter>();
                    })
                    .UseKestrel();
            });

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<RuntimeAppInfo>();
            services.AddSingleton(new RuntimeProgramInfo
            {
                IsRunningFromSource = runningFromSources,
                ContentRoot = appDirectory,
                RootAssembly = startup.Assembly
            });
            new ServiceRunnerRegistry(startup.Assembly).Register(services);
            services.AddSingleton<TickingServiceManager>();
            services.AddSingleton<ITickingServiceManager>(p => p.GetRequiredService<TickingServiceManager>());
            services.AddSingleton<TsInterop>();
        });

        if (roles.Contains("services") || roles.Contains("all"))
            builder.ConfigureServices(services =>
                services.AddHostedService(p => p.GetRequiredService<TickingServiceManager>()));
        
        customize?.Invoke(builder, cmdLineConfig);

        return builder;
    }
}
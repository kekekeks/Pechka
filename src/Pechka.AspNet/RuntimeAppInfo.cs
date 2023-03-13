using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Pechka.AspNet;

internal class RuntimeAppInfo
{
    public RuntimeProgramInfo Info { get; }

    public RuntimeAppInfo(IServiceProvider provider, RuntimeProgramInfo info)
    {
        Info = info;
        Config = provider.GetService<PechkaConfiguration>() ?? new PechkaConfiguration();
    }
    
    public PechkaConfiguration Config { get; }

    public string GetWebAppRoot() => Config.WebAppRoot ?? Path.Combine(Info.ContentRoot, "webapp");
    public string GetWebAppApiPath() => Path.Combine(GetWebAppRoot(), Config.WebAppApiPath ?? "src/api.ts");
    public string GetWebAppBuildPath() => Path.Combine(GetWebAppRoot(), Config.WebAppBuildPath ?? "build");
}

internal class RuntimeProgramInfo
{
    public Assembly RootAssembly { get; set; }
    public string ContentRoot { get; set; }
    public bool IsRunningFromSource { get; set; }
}
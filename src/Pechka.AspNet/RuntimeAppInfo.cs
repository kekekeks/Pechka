using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Pechka.AspNet;

internal class RuntimeAppInfo
{
    public RuntimeProgramInfo Info { get; }
    public PechkaConfiguration Config { get; }

    public RuntimeAppInfo(IServiceProvider provider, RuntimeProgramInfo info)
    {
        Info = info;
        Config = provider.GetService<PechkaConfiguration>() ?? new PechkaConfiguration();
    }
}

internal class RuntimeProgramInfo
{
    public Assembly RootAssembly { get; set; }
    public string ContentRoot { get; set; }
    public bool IsRunningFromSource { get; set; }
}
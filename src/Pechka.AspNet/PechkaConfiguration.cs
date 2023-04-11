using System;
using System.Collections.Generic;
using CoreRPC.Typescript;

namespace Pechka.AspNet;

public class PechkaConfiguration
{
    public string WebAppApiPath { get; set; }
    public List<PechkaWebAppConfiguration> WebAppPaths { get; set; } = new();
    public Action<TypescriptGenerationOptions>? TypescriptGenerationOptions { get; set; }
}

public class PechkaWebAppConfiguration
{
    public string WebAppBuildPath { get; set; }
    public string WebAppPrefix { get; set; } = string.Empty;
}

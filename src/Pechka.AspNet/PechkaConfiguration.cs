using System;
using System.Collections.Generic;
using System.IO;
using CoreRPC.Typescript;

namespace Pechka.AspNet;

public class PechkaConfiguration
{
    public string WebAppApiPath { get; set; } = Path.Combine("webapp", "src", "api.ts");

    public List<PechkaWebAppConfiguration> WebAppPaths { get; set; } = new()
    {
        new PechkaWebAppConfiguration
        {
            WebAppPrefix = string.Empty,
            WebAppBuildPath = Path.Combine("webapp", "build"),
        },
    };

    public Action<TypescriptGenerationOptions>? TypescriptGenerationOptions { get; set; }
    public bool AutoSetupForwardedHeaders { get; set; } = true;
}

public class PechkaWebAppConfiguration
{
    public string WebAppBuildPath { get; set; }
    public string WebAppPrefix { get; set; } = string.Empty;
}

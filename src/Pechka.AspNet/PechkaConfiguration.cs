using System;
using CoreRPC.Typescript;

namespace Pechka.AspNet;

public class PechkaConfiguration
{
    public string? WebAppRoot { get; set; }
    public string? WebAppApiPath { get; set; }
    public string? WebAppBuildPath { get; set; }
    public Action<TypescriptGenerationOptions>? TypescriptGenerationOptions { get; set; }
}
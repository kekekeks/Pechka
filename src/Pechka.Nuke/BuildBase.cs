namespace Pechka;
using System;
using Nuke.Common;

public abstract class BuildBase : NukeBuild
{
    protected abstract Target Package { get; }

    public static int Main<T>(string[] args) where T : BuildBase
    {
        Environment.SetEnvironmentVariable("NUKE_TELEMETRY_OPTOUT", "1");
        return Execute<T>(x => x.Package);
    }
}
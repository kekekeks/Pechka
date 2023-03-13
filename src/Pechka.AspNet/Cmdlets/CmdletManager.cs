using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CommandLine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Pechka.AspNet.Cmdlets
{
    internal static class CmdletManager
    {
        public static bool IsCommand(string[] args)
        {
            return args.Length > 0 && args[0] == "cmd";
        }

        
        public static int Execute(Func<string[], IHostBuilder> builder, Assembly rootAssembly, string[] args)
        {
            args = args.Skip(1).ToArray();
            var delimiterIndex = Array.IndexOf(args, "--");
            var commandArgs = delimiterIndex == -1 ? args : args.Take(delimiterIndex);
            var configArgs = delimiterIndex == -1 ? Array.Empty<string>() : args.Skip(delimiterIndex + 1).ToArray();

            var cmdlets = new Dictionary<Type, Type>();
            foreach (var t in typeof(CmdletBase<>).Assembly.GetTypes().Concat(rootAssembly.GetTypes()))
            {
                if(t.IsAbstract)
                    continue;
                var baseType = t.BaseType;
                
                while (baseType?.BaseType != typeof(object) && baseType != null) 
                    baseType = baseType?.BaseType;

                if (baseType == null || !baseType.IsConstructedGenericType ||
                    baseType.GetGenericTypeDefinition() != typeof(CmdletBase<>))
                    continue;

                cmdlets.Add(baseType.GetGenericArguments()[0], t);
            }

            var parserResult = Parser.Default.ParseArguments(commandArgs, cmdlets.Keys.ToArray());
            if (parserResult is NotParsed<object>)
                return -1;
            var parsed = ((Parsed<object>) parserResult).Value;
            var cmdletType = cmdlets[parsed.GetType()];

            var host = builder(configArgs)
                .ConfigureServices(s => s.AddSingleton(cmdletType))
                .Build();

            return ((ICmdletExec) host.Services.GetRequiredService(cmdletType)).Execute(parsed);
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using CoreRPC;
using CoreRPC.AspNetCore;
using CoreRPC.Typescript;
using Microsoft.AspNetCore.Builder;
using Newtonsoft.Json.Serialization;

namespace Pechka.AspNet
{
    internal class TsInterop
    {
        private readonly RuntimeAppInfo _info;

        public TsInterop(RuntimeAppInfo info)
        {
            _info = info;
        }
        
        public  string GenerateTsRpc()
        {
            return GenerateInternalTsRpc();
        }

        private void Configure(TypescriptGenerationOptions config)
        {
            var orig = config.ApiFieldNamingPolicy;
            config.DtoClassNamingPolicy = t => t.Name;// t => t == typeof(Result<>) ? "ResultT" : t.Name;
            config.ApiFieldNamingPolicy = type => orig(type).Replace("Rpc", "");
            config.DtoFieldNamingPolicy = TypescriptGenerationOptions.ToCamelCase;
            config.CustomTypeMapping = _ => null;
            config.CustomTsTypeMapping = (type, _) =>
            {
                if (type == typeof(byte[])) return "string";
                if (type == typeof(object)) return "any";
                if (type == typeof(decimal)) return "number";
                if (type == typeof(Guid)) return "string";
                if (type == typeof(DateTimeOffset)) return "string";
                if (type == typeof(DateTime)) return "string";
                return null;
            };
            _info.Config.TypescriptGenerationOptions?.Invoke(config);
        }

        private string GenerateInternalTsRpc()
        {
            return AspNetCoreRpcTypescriptGenerator.GenerateCode(GetRpcTypes(), Configure);
        }


        private IEnumerable<Type> GetRpcTypes()
        {
            var types = _info.Info.RootAssembly.GetTypes()
                .Where(type => typeof(IHttpContextAwareRpc).IsAssignableFrom(type) && !type.IsAbstract)
                .ToList();
            return types;
        }

        public void Register(IApplicationBuilder app, List<IMethodCallInterceptor>? interceptors)
        {
            app.UseCoreRpc("/tsrpc", config =>
            {
                config.RpcTypeResolver = GetRpcTypes;
                config.Interceptors.AddRange(interceptors ?? new List<IMethodCallInterceptor>());
                config.JsonSerializer.ContractResolver = new FixedJsonContractResolver();
            });
        }

        public class FixedJsonContractResolver : CamelCasePropertyNamesContractResolver
        {
            public FixedJsonContractResolver()
            {
                ((CamelCaseNamingStrategy) NamingStrategy).ProcessDictionaryKeys = false;
            }
        }
    }
}
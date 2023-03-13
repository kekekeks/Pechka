using System;
using System.IO;
using System.Threading.Tasks;
using CommandLine;

namespace Pechka.AspNet.Cmdlets
{
    
    internal class CmdletGenerateTsApi : CmdletBase<CmdletGenerateTsApi.GenerateTsApiOptions>
    {
        private readonly RuntimeAppInfo _info;
        private readonly TsInterop _interop;

        [Verb("GenerateTsApi")]
        public class GenerateTsApiOptions
        {
        }

        public CmdletGenerateTsApi(RuntimeAppInfo info, TsInterop interop)
        {
            _info = info;
            _interop = interop;
        }

        protected override int Execute(GenerateTsApiOptions args)
        {
            var devJsRoot = Path.Combine(Directory.GetCurrentDirectory(), "Frontend/packages/corerpc-api");
            if (!Directory.Exists(devJsRoot)) return -1;
            
            File.WriteAllText(Path.Combine(devJsRoot, "api.ts"), _interop.GenerateTsRpc());
            Console.WriteLine("api.ts created!");
            Environment.Exit(0);
            return 0;
        }
    }
}
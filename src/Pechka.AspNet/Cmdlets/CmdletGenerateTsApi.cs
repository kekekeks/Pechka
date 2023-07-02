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
            // NOTE: Maybe just throw exception here?
            var apiPath = _info.Config is { WebAppRoot: not null, WebAppApiPath: not null }
                ? Path.Combine(_info.Config.WebAppRoot, _info.Config.WebAppApiPath)
                : "Frontend/packages/corerpc-api/api.ts";
            
            var devJsRoot = Path.Combine(Directory.GetCurrentDirectory(), apiPath);
            var targetDirectory = Path.GetDirectoryName(devJsRoot);
            
            if (!Directory.Exists(targetDirectory))
            {
                Console.WriteLine($"Target directory {targetDirectory} doesn't exist! Check your PechkaConfiguration -> WebAppApiPath");
                return 1;
            }
            
            File.WriteAllText(Path.Combine(devJsRoot), _interop.GenerateTsRpc());
            Console.WriteLine("api.ts created!");
            return 0;
        }
    }
}
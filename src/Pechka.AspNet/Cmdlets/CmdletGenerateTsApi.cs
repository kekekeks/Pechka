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
            var apits = Path.Combine(_info.Info.ContentRoot, _info.Config.WebAppApiPath);
            var folder = Path.GetDirectoryName(apits);
            if (!Directory.Exists(folder)) return -1;

            File.WriteAllText(apits, _interop.GenerateTsRpc());
            Console.WriteLine("api.ts created!");
            return 0;
        }
    }
}
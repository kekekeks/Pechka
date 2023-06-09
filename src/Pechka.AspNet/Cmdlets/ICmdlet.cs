using System.Threading.Tasks;

namespace Pechka.AspNet.Cmdlets
{
    public interface ICmdletExec
    {
        int Execute(object args);
    }
    
    public abstract class CmdletBase<T> : ICmdletExec
    {
        public int Execute(object args) => Execute((T) args);

        protected abstract int Execute(T args);
    }
    
    public abstract class AsyncCmdletBase<T> : ICmdletExec
    {
        public int Execute(object args) => Execute((T) args).Result;

        protected abstract Task<int> Execute(T args);
    }
}
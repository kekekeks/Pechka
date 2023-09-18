namespace Pechka.AspNet;

public class PechkaJsonConfig
{
    public class HttpConfig
    {
        public string[]? KnownProxies { get; set; }
    }

    public HttpConfig Http { get; set; } = new();
}
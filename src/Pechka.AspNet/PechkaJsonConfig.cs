namespace Pechka.AspNet;

public class PechkaJsonConfig
{
    public class HttpConfig
    {
        public string[]? ValidProxies { get; set; }
    }

    public HttpConfig Http { get; set; } = new();
}
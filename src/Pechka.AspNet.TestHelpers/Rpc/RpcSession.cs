using System.Linq.Expressions;
using System.Net;
using CoreRPC.AspNetCore;
using CoreRPC.Binding;
using CoreRPC.Binding.Default;
using CoreRPC.Routing;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Pechka.AspNet.TestHelpers;

/// <summary>
/// Strongly-typed, expression-based CoreRPC client over one HTTP client + cookie jar — one
/// logical browser session. References the app's real RPC classes and uses CoreRPC's own
/// <see cref="DefaultMethodBinder"/> + <see cref="AspNetCoreTargetNameExtractor"/> to produce
/// exactly the target/method-signature the server dispatches on, so tests cannot drift from the
/// contract. A server exception throws <see cref="RpcServerException"/>; a non-success HTTP
/// status (e.g. a 401 from an unauthenticated call) throws <see cref="RpcTransportException"/>.
/// Apps subclass this (see <see cref="PechkaTestApp.CreateRpcSession"/>) to add typed per-service
/// accessors or extra serializer converters.
/// </summary>
public class RpcSession : IDisposable
{
    private readonly string _rpcPath;

    public RpcSession(string baseUrl, string rpcPath = "/tsrpc")
    {
        _rpcPath = rpcPath;
        Cookies = new CookieContainer();
        Http = new HttpClient(new HttpClientHandler { CookieContainer = Cookies })
        {
            BaseAddress = new Uri(baseUrl)
        };
    }

    public HttpClient Http { get; }
    public CookieContainer Cookies { get; }

    /// <summary>The only direct observation of "a session cookie was never issued" — an absent
    /// and a rolled-back cookie can look identical through the API surface.</summary>
    public IReadOnlyList<string> CookieNames() =>
        Cookies.GetAllCookies().Select(c => c.Name).ToList();

    public async Task<T> Call<TRpc, T>(Expression<Func<TRpc, Task<T>>> call)
    {
        var serializer = CreateSerializer();
        var result = await InvokeAsync(typeof(TRpc), call.Body, serializer);
        return (T)Decode(result, typeof(T), serializer)!;
    }

    public Task Call<TRpc>(Expression<Func<TRpc, Task>> call) =>
        InvokeAsync(typeof(TRpc), call.Body, CreateSerializer());

    /// <summary>Issues one expression-bound call while preserving the raw HTTP response — for
    /// tests whose contract is the status or headers rather than the decoded result.</summary>
    public Task<HttpResponseMessage> CallRaw<TRpc>(Expression<Func<TRpc, object?>> call)
    {
        var body = call.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert
            ? convert.Operand
            : call.Body;
        return PostEnvelopeAsync(BuildEnvelope(typeof(TRpc), body, CreateSerializer()));
    }

    /// <summary>A plain GET probe through this session's cookie jar.</summary>
    public Task<HttpResponseMessage> GetAsync(string relativePath) => Http.GetAsync(relativePath);

    /// <summary>Mirrors the server's serializer setup (Pechka's camelCase contract resolver;
    /// CoreRPC installs <see cref="StringEnumConverter"/> server-side by default, and without it
    /// here the typed client would send enum arguments as integers while the browser sends the
    /// generated TS strings). Override to add app-specific converters.</summary>
    protected virtual JsonSerializer CreateSerializer()
    {
        var serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            ContractResolver = new TsInterop.FixedJsonContractResolver(),
        });
        serializer.Converters.Add(new StringEnumConverter());
        return serializer;
    }

    private async Task<JToken> InvokeAsync(Type rpcType, Expression body, JsonSerializer serializer)
    {
        using var response = await PostEnvelopeAsync(BuildEnvelope(rpcType, body, serializer));
        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new RpcTransportException(response.StatusCode, responseBody);

        using var reader = new JsonTextReader(new StringReader(responseBody))
        {
            DateParseHandling = DateParseHandling.None
        };
        var parsed = (JObject)JToken.ReadFrom(reader);
        if (parsed["Exception"] is { Type: not JTokenType.Null } exception)
            throw new RpcServerException(exception.ToString());
        return parsed["Result"] ?? JValue.CreateNull();
    }

    private JObject BuildEnvelope(Type rpcType, Expression body, JsonSerializer serializer)
    {
        var call = (MethodCallExpression)body;
        IMethodBinder binder = new DefaultMethodBinder();
        ITargetNameExtractor extractor = new AspNetCoreTargetNameExtractor();
        return new JObject
        {
            ["Target"] = extractor.GetTargetName(rpcType),
            ["MethodSignature"] = binder.GetMethodSignature(call.Method),
            ["Arguments"] = new JArray(call.Arguments
                .Select(a => Expression.Lambda<Func<object?>>(
                    Expression.Convert(a, typeof(object))).Compile().Invoke())
                .Select(a => a is null ? JValue.CreateNull() : JToken.FromObject(a, serializer)))
        };
    }

    private async Task<HttpResponseMessage> PostEnvelopeAsync(JObject envelope)
    {
        using var content = new StringContent(envelope.ToString(Formatting.None));
        return await Http.PostAsync(_rpcPath, content);
    }

    private static object? Decode(JToken token, Type type, JsonSerializer serializer)
    {
        if (token.Type == JTokenType.Null)
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        return token.ToObject(type, serializer);
    }

    public void Dispose() => Http.Dispose();
}

/// <summary>Thrown when the RPC endpoint answers with a non-success HTTP status (e.g. 401).</summary>
public class RpcTransportException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public RpcTransportException(HttpStatusCode status, string body)
        : base($"RPC HTTP {(int)status}: {body}") => StatusCode = status;
}

/// <summary>Thrown when the server reports an unhandled exception in the RPC envelope.</summary>
public class RpcServerException : Exception
{
    public RpcServerException(string message) : base(message)
    {
    }
}

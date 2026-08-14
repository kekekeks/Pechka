using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Tests;

public class RetryMiddlewareTests
{
    private sealed class TestResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; set; }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }

    /// <summary>EnableBuffering only wraps non-seekable bodies, which is what a real request has.</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] content) => _inner = new MemoryStream(content);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static PechkaDbTransactionOptions Options(bool enableRetries = true) => new()
    {
        EnableRetries = enableRetries,
        RetryMaxAttempts = 3,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        RetryMaxDelay = TimeSpan.FromMilliseconds(2),
        RetryBudgetMaxRetries = 100,
        RetryBudgetWindow = TimeSpan.FromMinutes(1),
        IsTransientFailure = e => e is FakeTransientException
    };

    private static PechkaTransactionRetryMiddleware Middleware(RequestDelegate next,
        PechkaDbTransactionOptions options) =>
        new(next, options, NullLogger<PechkaTransactionRetryMiddleware>.Instance);

    private static DefaultHttpContext Context(params object[] endpointMetadata)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(endpointMetadata), "test endpoint"));
        return context;
    }

    private static void SetBody(HttpContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new NonSeekableStream(bytes);
    }

    [Fact]
    public async Task A_Transient_Failure_Replays_The_Pipeline_With_The_Buffered_Body()
    {
        var context = Context();
        SetBody(context, "hello");
        var reads = new List<string>();

        await Middleware(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            reads.Add(await reader.ReadToEndAsync());
            if (reads.Count == 1)
                throw new FakeTransientException();
        }, Options()).Invoke(context);

        Assert.Equal(new[] { "hello", "hello" }, reads);
    }

    [Fact]
    public async Task A_Post_Request_Body_Is_Buffered()
    {
        var context = Context();
        SetBody(context, "hello");
        var original = context.Request.Body;

        await Middleware(_ => Task.CompletedTask, Options()).Invoke(context);

        Assert.NotSame(original, context.Request.Body);
        Assert.True(context.Request.Body.CanSeek);
    }

    [Fact]
    public async Task A_Get_Request_Body_Is_Not_Buffered()
    {
        var context = Context();
        context.Request.Method = HttpMethods.Get;
        context.Request.Body = new NonSeekableStream(Encoding.UTF8.GetBytes("hello"));
        var original = context.Request.Body;

        await Middleware(_ => Task.CompletedTask, Options()).Invoke(context);

        Assert.Same(original, context.Request.Body);
    }

    [Fact]
    public async Task A_Started_Response_Is_Never_Retried()
    {
        var context = Context();
        context.Features.Set<IHttpResponseFeature>(new TestResponseFeature { HasStarted = true });
        var passes = 0;

        await Assert.ThrowsAsync<FakeTransientException>(() => Middleware(_ =>
        {
            passes++;
            throw new FakeTransientException();
        }, Options()).Invoke(context));

        Assert.Equal(1, passes);
    }

    [Fact]
    public async Task NoRetry_Metadata_Disables_Retries()
    {
        var context = Context(new NoRetryAttribute());
        var passes = 0;

        await Assert.ThrowsAsync<FakeTransientException>(() => Middleware(_ =>
        {
            passes++;
            throw new FakeTransientException();
        }, Options()).Invoke(context));

        Assert.Equal(1, passes);
    }

    [Fact]
    public async Task NoTransaction_Metadata_Disables_Retries()
    {
        var context = Context(new NoTransactionAttribute());
        var passes = 0;

        await Assert.ThrowsAsync<FakeTransientException>(() => Middleware(_ =>
        {
            passes++;
            throw new FakeTransientException();
        }, Options()).Invoke(context));

        Assert.Equal(1, passes);
    }

    [Fact]
    public async Task Retries_Disabled_In_The_Options_Pass_Through()
    {
        var context = Context();
        SetBody(context, "hello");
        var original = context.Request.Body;
        var passes = 0;

        await Assert.ThrowsAsync<FakeTransientException>(() => Middleware(_ =>
        {
            passes++;
            throw new FakeTransientException();
        }, Options(enableRetries: false)).Invoke(context));

        Assert.Equal(1, passes);
        Assert.Same(original, context.Request.Body);
    }

    [Fact]
    public async Task A_Request_Without_An_Endpoint_Passes_Through()
    {
        var context = new DefaultHttpContext();
        var passes = 0;

        await Assert.ThrowsAsync<FakeTransientException>(() => Middleware(_ =>
        {
            passes++;
            throw new FakeTransientException();
        }, Options()).Invoke(context));

        Assert.Equal(1, passes);
    }

    [Fact]
    public async Task A_Successful_First_Pass_Runs_The_Pipeline_Once()
    {
        var context = Context();
        var passes = 0;

        await Middleware(_ =>
        {
            passes++;
            return Task.CompletedTask;
        }, Options()).Invoke(context);

        Assert.Equal(1, passes);
    }

    [Fact]
    public async Task A_Non_Transient_Failure_Is_Not_Retried()
    {
        var context = Context();
        var passes = 0;

        await Assert.ThrowsAsync<FakePermanentException>(() => Middleware(_ =>
        {
            passes++;
            throw new FakePermanentException();
        }, Options()).Invoke(context));

        Assert.Equal(1, passes);
    }
}

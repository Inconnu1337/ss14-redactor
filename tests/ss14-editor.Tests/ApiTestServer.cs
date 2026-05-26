using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Editor.Tests;

using Content.Editor.Editor;

/// <summary>
/// Spins up a real <see cref="HttpListener"/> on an ephemeral port and routes
/// /api/* requests to the supplied <see cref="ApiRouter"/>. Disposes both the
/// listener and the temp directory together.
/// </summary>
internal sealed class ApiTestServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loop;
    private readonly CancellationTokenSource _cts = new();

    public string BaseUrl { get; }
    public HttpClient Client { get; }
    public ApiRouter Router { get; }

    private ApiTestServer(ApiRouter router, HttpListener listener, string baseUrl)
    {
        Router = router;
        _listener = listener;
        BaseUrl = baseUrl;
        Client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _loop = Task.Run(LoopAsync);
    }

    public static ApiTestServer Start(ApiRouter router)
    {
        for (int port = 32000; port < 33000; port++)
        {
            try
            {
                var prefix = $"http://localhost:{port}/";
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();
                return new ApiTestServer(router, listener, prefix);
            }
            catch (HttpListenerException) { }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("No free port found for test server.");
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            _ = Task.Run(async () =>
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                bool keepAlive = false;
                try
                {
                    if (path.StartsWith("/api/"))
                        keepAlive = await Router.DispatchAsync(path, ctx.Request, ctx.Response);
                    else
                    {
                        ctx.Response.StatusCode = 404;
                    }
                }
                catch (Exception ex)
                {
                    try { ctx.Response.StatusCode = 500; await ctx.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(ex.ToString())); } catch { }
                }
                finally
                {
                    if (!keepAlive) { try { ctx.Response.Close(); } catch { } }
                }
            });
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        Client.Dispose();
    }
}

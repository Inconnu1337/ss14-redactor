using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Editor.Editor;
using Xunit;

namespace Content.Editor.Tests;

public class HttpJsonTests
{
    // HttpJson works over System.Net.HttpListener types that are abstract/sealed
    // with internal constructors, so unit-testing the helpers directly requires
    // spinning up a real listener. We test through a localhost listener on an
    // ephemeral port.

    private static HttpListener StartListener(out string url)
    {
        for (int port = 30000; port < 31000; port++)
        {
            var listener = new HttpListener();
            url = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(url);
            try { listener.Start(); return listener; }
            catch (HttpListenerException) { listener.Close(); }
        }
        throw new System.InvalidOperationException("No free port in 30000-30999");
    }

    [Fact]
    public async Task WriteAsync_SerializesWithCamelCase_AndSetsContentType()
    {
        var listener = StartListener(out var url);
        try
        {
            var serverTask = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync();
                await HttpJson.WriteAsync(ctx.Response, new { FirstName = "Ann", AgeYears = 30 });
                ctx.Response.OutputStream.Close();
            });

            using var client = new HttpClient();
            var resp = await client.GetAsync(url);
            await serverTask;

            Assert.Equal("application/json; charset=utf-8", resp.Content.Headers.ContentType?.ToString());
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("Ann", doc.RootElement.GetProperty("firstName").GetString());
            Assert.Equal(30, doc.RootElement.GetProperty("ageYears").GetInt32());
        }
        finally { listener.Close(); }
    }

    [Fact]
    public async Task ReadBodyAsync_ParsesIncomingJson()
    {
        var listener = StartListener(out var url);
        JsonElement received = default;
        try
        {
            var serverTask = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync();
                received = await HttpJson.ReadBodyAsync(ctx.Request);
                ctx.Response.StatusCode = 204;
                ctx.Response.OutputStream.Close();
            });

            using var client = new HttpClient();
            var content = new StringContent("{\"x\": 42, \"y\": \"hi\"}", Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(url, content);
            await serverTask;

            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
            Assert.Equal(42, received.GetProperty("x").GetInt32());
            Assert.Equal("hi", received.GetProperty("y").GetString());
        }
        finally { listener.Close(); }
    }

    [Fact]
    public async Task WriteErrorAsync_SetsStatusAndBody()
    {
        var listener = StartListener(out var url);
        try
        {
            var serverTask = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync();
                await HttpJson.WriteErrorAsync(ctx.Response, 418, "I'm a teapot");
                ctx.Response.OutputStream.Close();
            });

            using var client = new HttpClient();
            var resp = await client.GetAsync(url);
            await serverTask;

            Assert.Equal(418, (int)resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("I'm a teapot", doc.RootElement.GetProperty("error").GetString());
        }
        finally { listener.Close(); }
    }
}

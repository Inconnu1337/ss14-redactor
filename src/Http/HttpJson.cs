using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

namespace Content.Editor.Editor;

/// <summary>
/// Small helpers for reading/writing JSON over <see cref="HttpListener"/>.
/// </summary>
internal static class HttpJson
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Default encoder escapes apostrophes, non-ASCII letters etc. as \uXXXX.
        // Our responses are always served back to our own JS over fetch+JSON.parse,
        // so html-safety isn't needed; readable raw bodies are more useful when
        // an error message falls back to "raw text" rendering on the client.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task WriteAsync(HttpListenerResponse res, object data)
    {
        var json = JsonSerializer.Serialize(data, s_options);
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentType ??= "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    public static async Task<JsonElement> ReadBodyAsync(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    public static async Task WriteErrorAsync(HttpListenerResponse res, int status, string message)
    {
        res.StatusCode = status;
        await WriteAsync(res, new { error = message });
    }
}

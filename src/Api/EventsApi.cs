using System.Net;
using System.Threading.Tasks;

namespace Content.Editor.Editor;

internal sealed partial class ApiRouter
{
    /// <summary>
    /// Long-lived <c>text/event-stream</c> endpoint. Replaces client-side polling
    /// for file change detection. Connection stays open until the client closes
    /// it; the response is not auto-closed by the dispatcher.
    /// </summary>
    private Task HandleEventsAsync(HttpListenerRequest req, HttpListenerResponse res)
        => _ctx!.Events.SubscribeAsync(res, default);
}

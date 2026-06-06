using System.Net;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;

namespace MailMopper.Services;

/// <summary>
/// OAuth2 code receiver that listens on a fixed port and prints the auth URL
/// to the console instead of opening a browser. Works in headless and
/// containerised environments when the port is forwarded to the host.
/// </summary>
public sealed class LoopbackCodeReceiver(int port, Action<string>? onUrlGenerated = null) : ICodeReceiver
{
    private readonly int _port = port;
    private readonly Action<string>? _onUrlGenerated = onUrlGenerated;

    public string RedirectUri => $"http://localhost:{_port}/authorize/";

    public async Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(
        AuthorizationCodeRequestUrl url, CancellationToken taskCancellationToken)
    {
        var authUri = url.Build().ToString();

        _onUrlGenerated?.Invoke(authUri);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{_port}/authorize/");
        listener.Start();

        if (_onUrlGenerated == null)
        {
            Console.WriteLine();
            Console.WriteLine("Open the following URL in your browser to authenticate:");
            Console.WriteLine();
            Console.WriteLine($"  {authUri}");
            Console.WriteLine();
            Console.WriteLine("Waiting for authorization...");
        }

        var context = await listener.GetContextAsync().WaitAsync(taskCancellationToken);

        var queryString = context.Request.Url?.Query ?? "";
        var responseHtml = "<html><body>Authorization complete. You can close this tab.</body></html>"u8.ToArray();
        context.Response.ContentType = "text/html";
        context.Response.StatusCode = 200;
        await context.Response.OutputStream.WriteAsync(responseHtml, taskCancellationToken);
        context.Response.Close();

        listener.Stop();

        var queryParams = System.Web.HttpUtility.ParseQueryString(queryString);
        var code = queryParams["code"];
        var error = queryParams["error"];
        var state = queryParams["state"];

        if (!string.IsNullOrEmpty(error))
        {
            return new AuthorizationCodeResponseUrl { Error = error };
        }

        return !string.Equals(state, url.State, StringComparison.Ordinal)
            ? new AuthorizationCodeResponseUrl { Error = "state_mismatch" }
            : new AuthorizationCodeResponseUrl { Code = code };
    }
}

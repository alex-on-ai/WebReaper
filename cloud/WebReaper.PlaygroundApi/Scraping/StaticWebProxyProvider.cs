using System.Net;
using WebReaper.Proxy.Abstract;

namespace WebReaper.PlaygroundApi.Scraping;

/// <summary>
/// An <see cref="IProxyProvider"/> that always returns one fixed
/// <see cref="WebProxy"/> (the residential upstream, with credentials). The CDP
/// transport reads its credentials to answer the browser's proxy-auth challenge
/// (CDP <c>Fetch.authRequired</c>); the browser launch adds <c>--proxy-server</c>
/// for the actual routing. Single-URL playground scrape, so no rotation is needed.
/// </summary>
public sealed class StaticWebProxyProvider : IProxyProvider
{
    private readonly WebProxy _proxy;

    public StaticWebProxyProvider(WebProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        _proxy = proxy;
    }

    public Task<WebProxy> GetProxyAsync() => Task.FromResult(_proxy);
}

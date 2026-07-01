namespace OpsApi.Infrastructure;

public sealed class KeycloakBackchannelHandler() : HttpClientHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host == "localhost" && request.RequestUri.Port == 8080)
        {
            var ub = new UriBuilder(request.RequestUri) { Host = "keycloak" };
            request.RequestUri = ub.Uri;
        }
        return base.SendAsync(request, cancellationToken);
    }
}

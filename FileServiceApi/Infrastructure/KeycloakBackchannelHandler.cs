namespace FileServiceApi.Infrastructure;

// KC_HOSTNAME=localhost nedeniyle OIDC discovery'deki jwks_uri localhost:8080 içerebilir.
// Container içinden localhost:8080 Keycloak'a ulaşmaz; bu handler keycloak:8080'e yönlendirir.
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

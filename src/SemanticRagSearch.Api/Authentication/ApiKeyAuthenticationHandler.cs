using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SemanticRagSearch.Api.Errors;

namespace SemanticRagSearch.Api.Authentication;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyTenantStore tenants) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyCredentials.HeaderName, out var values))
            return AuthenticateResult.NoResult();

        if (values.Count != 1 || !ApiKeyCredentials.IsValidFormat(values[0] ?? ""))
            return AuthenticateResult.Fail("Invalid API key.");

        Guid? tenantId = await tenants.FindActiveTenantAsync(
            ApiKeyCredentials.Hash(values[0]!), Context.RequestAborted);
        if (tenantId is null)
            return AuthenticateResult.Fail("Invalid API key.");

        var identity = new ClaimsIdentity(
            [new Claim(ApiKeyCredentials.TenantClaim, tenantId.Value.ToString()),
             new Claim(ClaimTypes.NameIdentifier, tenantId.Value.ToString())], ApiKeyCredentials.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = ApiKeyCredentials.Scheme;
        return Response.WriteAsJsonAsync(new ApiErrorResponse("A valid API key is required."), Context.RequestAborted);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Response.WriteAsJsonAsync(new ApiErrorResponse("The API key cannot access the requested tenant."), Context.RequestAborted);
    }
}

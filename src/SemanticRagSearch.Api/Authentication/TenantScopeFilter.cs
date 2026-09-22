using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SemanticRagSearch.Api.Errors;

namespace SemanticRagSearch.Api.Authentication;

public interface ITenantRequest
{
    Guid TenantId { get; }
}

public sealed class TenantScopeFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!Guid.TryParse(context.HttpContext.User.FindFirst(ApiKeyCredentials.TenantClaim)?.Value, out Guid tenantId))
        {
            context.Result = new JsonResult(new ApiErrorResponse("A valid API key is required."))
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
            return;
        }

        if (context.ActionArguments.Values.OfType<ITenantRequest>()
            .Any(request => request.TenantId != Guid.Empty && request.TenantId != tenantId))
        {
            context.Result = new JsonResult(new ApiErrorResponse("The API key cannot access the requested tenant."))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }
}

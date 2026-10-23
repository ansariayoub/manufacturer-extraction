using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ManufacturerExtraction.Api.Services.Auth;

/// <summary>
/// Runs before every controller action: resolves the caller to an application user (creating the
/// account on a person's first sign-in) and stores it in <see cref="CurrentUserAccessor"/>. Access
/// itself is gated by Microsoft Entra, so only a token that identifies no one is refused here.
/// </summary>
public class AppUserFilter : IAsyncAuthorizationFilter
{
    private readonly AppUserResolver _resolver;
    private readonly CurrentUserAccessor _accessor;

    public AppUserFilter(AppUserResolver resolver, CurrentUserAccessor accessor)
    {
        _resolver = resolver;
        _accessor = accessor;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = await _resolver.ResolveAsync(context.HttpContext.User, context.HttpContext.RequestAborted);
        if (user is null)
        {
            context.Result = new ObjectResult(new { error = "not_authorized", message = "Your Microsoft account could not be identified." })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
            return;
        }

        _accessor.Set(user);
    }
}

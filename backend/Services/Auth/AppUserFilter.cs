using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ManufacturerExtraction.Api.Services.Auth;

/// <summary>
/// Runs before every controller action: resolves the caller to an allow-listed application user and
/// stores it in <see cref="CurrentUserAccessor"/>. A valid Microsoft sign-in is not enough — someone
/// who is not on the allow-list gets 403 here, before any data is touched.
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
            context.Result = new ObjectResult(new { error = "not_authorized", message = "Your account is not allowed to use this application." })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
            return;
        }

        _accessor.Set(user);
    }
}

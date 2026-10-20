using Microsoft.AspNetCore.Mvc;
using ManufacturerExtraction.Api.Dtos;
using ManufacturerExtraction.Api.Services.Auth;

namespace ManufacturerExtraction.Api.Controllers;

/// <summary>
/// Who the signed-in caller is. The frontend calls this right after Microsoft sign-in: a 403 here
/// (raised by <see cref="AppUserFilter"/>) means the person authenticated fine but is not on the
/// application's allow-list, so the UI shows an "access denied" screen instead of the app.
/// </summary>
[ApiController]
[Route("api/me")]
public class MeController : ControllerBase
{
    private readonly CurrentUserAccessor _currentUser;
    private readonly AuthSettings _auth;

    public MeController(CurrentUserAccessor currentUser, AuthSettings auth)
    {
        _currentUser = currentUser;
        _auth = auth;
    }

    [HttpGet]
    public ActionResult<MeDto> Get()
    {
        var me = _currentUser.Current;
        return Ok(new MeDto(me.Email, me.DisplayName, me.ClientId, me.ClientName, me.IsAdmin, _auth.Enabled));
    }
}

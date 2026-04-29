using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Investigator.Services;

/// <summary>
/// Scoped per Blazor circuit. Bridges <see cref="CircuitAuthState"/> into Blazor's
/// <see cref="AuthenticationState"/> so that <c>[Authorize]</c> attributes and
/// <c>AuthorizeRouteView</c> work for both OIDC and token auth modes.
///
/// For OIDC: syncs claims from <c>HttpContext.User</c> (cookie) into
/// <see cref="CircuitAuthState"/> on the first call (during the initial HTTP render).
///
/// For token auth: starts unauthenticated; the Login page sets
/// <see cref="CircuitAuthState"/> then calls <see cref="NotifyStateChanged"/> to
/// re-evaluate authorization.
/// </summary>
public sealed class InvestigatorAuthStateProvider : AuthenticationStateProvider
{
    private readonly CircuitAuthState _circuitAuth;
    private readonly AuthSettings _authSettings;
    private readonly IHttpContextAccessor _httpContext;
    private bool _oidcSynced;

    public InvestigatorAuthStateProvider(
        CircuitAuthState circuitAuth,
        AuthSettings authSettings,
        IHttpContextAccessor httpContext)
    {
        _circuitAuth = circuitAuth;
        _authSettings = authSettings;
        _httpContext = httpContext;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (!_oidcSynced && _authSettings.HasOidc)
        {
            _oidcSynced = true;
            var user = _httpContext.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                var sub = user.FindFirst("sub")?.Value;
                var name = user.Identity.Name;
                _circuitAuth.IsAuthenticated = true;
                _circuitAuth.UserId = sub;
                _circuitAuth.DisplayName = !string.IsNullOrEmpty(name) ? name : sub;
                _circuitAuth.AuthMethod = AuthMode.Oidc;
            }
        }

        if (_circuitAuth.IsAuthenticated)
        {
            var identity = new ClaimsIdentity(
            [
                new Claim("sub", _circuitAuth.UserId ?? ""),
                new Claim(ClaimTypes.Name, _circuitAuth.DisplayName ?? ""),
            ], _circuitAuth.AuthMethod.ToString());

            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }

        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    public void NotifyStateChanged() =>
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
}

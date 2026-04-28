namespace Investigator.Services;

/// <summary>
/// Scoped per Blazor circuit. Single source of truth for whether
/// the current circuit is authenticated, regardless of auth mode
/// (OIDC populates this from the HTTP auth state; token mode
/// populates it from the Login page).
/// </summary>
public class CircuitAuthState
{
    public bool IsAuthenticated { get; set; }
    /// <summary>Stable identifier (OIDC <c>sub</c>, or a fixed string for token/anonymous modes).</summary>
    public string? UserId { get; set; }
    /// <summary>Human-friendly label for the header. Falls back to <see cref="UserId"/> when the IdP provides no name.</summary>
    public string? DisplayName { get; set; }
    public AuthMode AuthMethod { get; set; }
}

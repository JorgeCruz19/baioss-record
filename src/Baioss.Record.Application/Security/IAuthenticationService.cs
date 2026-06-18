using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Security;

public sealed record AuthResult(bool Succeeded, User? User, string? Token, string? Error);

/// <summary>
/// Autenticación y autorización por roles (Administrador, Supervisor, Operador).
/// Emite tokens firmados para la REST API y registra auditoría de login.
/// </summary>
public interface IAuthenticationService
{
    Task<AuthResult> LoginAsync(string username, string password, CancellationToken ct = default);
    bool IsAuthorized(User user, string permission);
}

/// <summary>Resuelve qué permisos otorga cada rol (matriz de autorización).</summary>
public interface IAuthorizationPolicy
{
    bool HasPermission(UserRole role, string permission);
}

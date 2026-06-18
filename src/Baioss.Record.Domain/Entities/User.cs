namespace Baioss.Record.Domain.Entities;

/// <summary>
/// Usuario del sistema con rol para control de acceso (Administrador, Supervisor, Operador).
/// La contraseña se persiste siempre como hash + salt; nunca en claro.
/// </summary>
public sealed class User
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Username { get; set; }
    public required string DisplayName { get; set; }
    public required UserRole Role { get; set; }

    public required string PasswordHash { get; set; }
    public required string PasswordSalt { get; set; }

    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
}

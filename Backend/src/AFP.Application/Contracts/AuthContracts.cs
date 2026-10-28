using AFP.Domain.Enums;

namespace AFP.Application.Contracts;

public sealed record RegisterRequest(string? Username, string? Email, string? DisplayName, string? Password, string? ConfirmPassword);
public sealed record LoginRequest(string? Username, string? Password);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record LogoutRequest(string? RefreshToken);
public sealed record ForgotPasswordRequest(string? Email);
public sealed record ResetPasswordRequest(string? Token, string? NewPassword, string? ConfirmPassword);
public sealed record MessageResponse(string Message);

public record UserDto(
    long Id, string Username, string Email, string DisplayName, IReadOnlyList<UserRole> Roles, UserStatus Status,
    DateTime CreatedAtUtc, DateTime? LastLoginAtUtc);

public sealed record AdminUserDto(
    long Id, string Username, string Email, string DisplayName, IReadOnlyList<UserRole> Roles, UserStatus Status,
    DateTime CreatedAtUtc, DateTime? LastLoginAtUtc, DateTime? ApprovedAtUtc, int FailedLoginCount,
    DateTime? LockoutEndUtc, string? DisabledReason)
    : UserDto(Id, Username, Email, DisplayName, Roles, Status, CreatedAtUtc, LastLoginAtUtc);

/// <summary>OAuth 2.0-style token pair: short-lived JWT access token + rotating opaque refresh token.</summary>
public sealed record TokenResponse(
    string AccessToken, string TokenType, DateTime ExpiresAtUtc, string RefreshToken, DateTime RefreshTokenExpiresAtUtc, UserDto User);

public sealed record RoleDto(int RoleId, string RoleName);
public sealed record ApproveUserRequest(IReadOnlyList<UserRole>? Roles);
public sealed record AssignRolesRequest(IReadOnlyList<UserRole>? Roles);
public sealed record DisableUserRequest(string? Reason);

/// <summary>Request metadata captured for the security audit trail.</summary>
public sealed record ClientInfo(string? IpAddress, string? UserAgent);

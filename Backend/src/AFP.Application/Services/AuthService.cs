using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Application.Mapping;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AFP.Application.Services;

public interface IAuthService
{
    Task<MessageResponse> RegisterAsync(RegisterRequest request, ClientInfo client, CancellationToken ct);
    Task<TokenResponse> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken ct);
    Task<TokenResponse> RefreshAsync(RefreshRequest request, ClientInfo client, CancellationToken ct);
    Task LogoutAsync(LogoutRequest request, CancellationToken ct);
    Task<MessageResponse> ForgotPasswordAsync(ForgotPasswordRequest request, ClientInfo client, CancellationToken ct);
    Task ResetPasswordAsync(ResetPasswordRequest request, ClientInfo client, CancellationToken ct);
    Task<UserDto> GetCurrentUserAsync(CancellationToken ct);
}

public static class AuthEvents
{
    public const string Register = "Register";
    public const string RegisterExisting = "RegisterExistingAccount";
    public const string LoginSucceeded = "LoginSucceeded";
    public const string LoginFailed = "LoginFailed";
    public const string LockedOut = "LockedOut";
    public const string TokenRefreshed = "TokenRefreshed";
    public const string RefreshFailed = "RefreshFailed";
    public const string RefreshTokenReuse = "RefreshTokenReuseDetected";
    public const string Logout = "Logout";
    public const string PasswordResetRequested = "PasswordResetRequested";
    public const string PasswordResetCompleted = "PasswordResetCompleted";
    public const string PasswordResetFailed = "PasswordResetFailed";
    public const string AdminBootstrap = "AdminBootstrap";
}

public static partial class SecureTokens
{
    public static string Create(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    public static bool IsValidUsername(string? username) => username is not null && UsernameRegex().IsMatch(username.Trim());

    [GeneratedRegex("^[A-Za-z0-9._-]{3,64}$")]
    private static partial Regex UsernameRegex();
}

/// <summary>Revocation reasons recorded on refresh tokens.</summary>
public static class RefreshRevocation
{
    public const string Logout = "Logout";
    public const string Reuse = "ReuseDetected";
    public const string PasswordReset = "PasswordReset";
    public const string AccountChanged = "AccountChanged";
}

public sealed class AuthService(
    IUserRepository users,
    IAuditRepository audit,
    IUnitOfWork unitOfWork,
    IPasswordHasher hasher,
    ITokenService tokens,
    IEmailSender email,
    ICurrentUser currentUser,
    IClock clock,
    IOptions<AuthOptions> options,
    ILogger<AuthService> logger) : IAuthService
{
    public const string RegisterMessage =
        "Thank you. If these details can be registered, the account has been created and is awaiting dispatcher approval.";
    public const string ForgotPasswordMessage =
        "If an account exists for that email, a password reset link has been sent. The link expires shortly and can be used once.";

    private readonly AuthOptions _options = options.Value;

    // Keeps the work for unknown usernames comparable to known ones (mitigates timing-based enumeration).
    private static readonly AppUser DummyUser = new() { Id = 0 };
    private static string? _dummyHash;

    public async Task<MessageResponse> RegisterAsync(RegisterRequest request, ClientInfo client, CancellationToken ct)
    {
        var v = new Validator()
            .Required(nameof(request.Username), request.Username, "Username")
            .Email(nameof(request.Email), request.Email)
            .Required(nameof(request.DisplayName), request.DisplayName, "Display name")
            .MaxLength(nameof(request.DisplayName), request.DisplayName, 128, "Display name")
            .Password(nameof(request.Password), request.Password, request.ConfirmPassword, nameof(request.ConfirmPassword));
        if (!string.IsNullOrWhiteSpace(request.Username) && !SecureTokens.IsValidUsername(request.Username))
            v.Add(nameof(request.Username), "Username must be 3–64 characters: letters, digits, '.', '_' or '-'.");
        v.ThrowIfInvalid();

        var username = SecureTokens.NormalizeUsername(request.Username!);
        var normalizedEmail = SecureTokens.NormalizeEmail(request.Email!);
        var now = clock.UtcNow;
        var existing = await users.GetByUsernameAsync(username, ct) ?? await users.GetByEmailAsync(normalizedEmail, ct);
        if (existing is not null)
        {
            // Same response as a new registration: do not disclose that the account exists.
            await AuthAuditAsync(AuthEvents.RegisterExisting, existing.Id, normalizedEmail, false, client, "Registration attempted for existing username/email.", ct);
            await unitOfWork.SaveChangesAsync(ct);
            return new MessageResponse(RegisterMessage);
        }

        var user = new AppUser
        {
            Username = username,
            Email = normalizedEmail,
            DisplayName = request.DisplayName!.Trim(),
            Status = UserStatus.Pending, // roles are granted only by a dispatcher (user_role)
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        user.PasswordHash = hasher.Hash(user, request.Password!);
        await users.AddAsync(user, ct);
        await unitOfWork.SaveChangesAsync(ct);
        await AuthAuditAsync(AuthEvents.Register, user.Id, normalizedEmail, true, client, "Account created; pending approval.", ct);
        await unitOfWork.SaveChangesAsync(ct);
        logger.LogInformation("Registered pending account {UserId}", user.Id);
        return new MessageResponse(RegisterMessage);
    }

    public async Task<TokenResponse> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken ct)
    {
        new Validator()
            .Required(nameof(request.Username), request.Username, "Username")
            .Required(nameof(request.Password), request.Password, "Password")
            .ThrowIfInvalid();

        // Users may sign in with their username or their email address.
        var username = SecureTokens.NormalizeUsername(request.Username!);
        var now = clock.UtcNow;
        var user = await users.GetByUsernameAsync(username, ct)
                   ?? (username.Contains('@') ? await users.GetByEmailAsync(SecureTokens.NormalizeEmail(username), ct) : null);

        if (user is null)
        {
            _dummyHash ??= hasher.Hash(DummyUser, "Dummy-Password-For-Timing-1");
            DummyUser.PasswordHash = _dummyHash;
            hasher.Verify(DummyUser, request.Password!, out _);
            await AuthAuditAsync(AuthEvents.LoginFailed, null, username, false, client, "Unknown username.", ct);
            await unitOfWork.SaveChangesAsync(ct);
            throw InvalidCredentials();
        }

        if (user.IsLockedOut(now))
        {
            await AuthAuditAsync(AuthEvents.LoginFailed, user.Id, user.Email, false, client, "Account locked.", ct);
            await unitOfWork.SaveChangesAsync(ct);
            throw Locked();
        }

        if (!hasher.Verify(user, request.Password!, out var needsRehash))
        {
            user.FailedLoginCount++;
            var lockedNow = user.FailedLoginCount >= _options.MaxFailedLoginAttempts;
            if (lockedNow)
            {
                user.LockoutEndUtc = now.AddMinutes(_options.LockoutMinutes);
                user.FailedLoginCount = 0;
                await AuthAuditAsync(AuthEvents.LockedOut, user.Id, user.Email, false, client,
                    $"Locked for {_options.LockoutMinutes} minutes after repeated failures.", ct);
            }
            else
            {
                await AuthAuditAsync(AuthEvents.LoginFailed, user.Id, user.Email, false, client, "Invalid password.", ct);
            }
            user.UpdatedAtUtc = now;
            await unitOfWork.SaveChangesAsync(ct);
            throw lockedNow ? Locked() : InvalidCredentials();
        }

        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        if (needsRehash) user.PasswordHash = hasher.Hash(user, request.Password!);
        await EnsureCanSignInAsync(user, client, ct);

        user.LastLoginAtUtc = now;
        user.UpdatedAtUtc = now;
        var response = await IssueTokensAsync(user, Guid.NewGuid(), client, ct);
        await AuthAuditAsync(AuthEvents.LoginSucceeded, user.Id, user.Email, true, client, null, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return response;
    }

    public async Task<TokenResponse> RefreshAsync(RefreshRequest request, ClientInfo client, CancellationToken ct)
    {
        new Validator().Required(nameof(request.RefreshToken), request.RefreshToken, "Refresh token").ThrowIfInvalid();
        var now = clock.UtcNow;
        var stored = await users.GetRefreshTokenByHashAsync(SecureTokens.Hash(request.RefreshToken!), ct);
        if (stored is null)
        {
            await AuthAuditAsync(AuthEvents.RefreshFailed, null, null, false, client, "Unknown refresh token.", ct);
            await unitOfWork.SaveChangesAsync(ct);
            throw InvalidRefresh();
        }

        if (stored.ConsumedAtUtc is not null && stored.RevokedAtUtc is null)
        {
            // A rotated token was presented again: assume theft and revoke the whole family.
            foreach (var t in await users.ListUnrevokedRefreshTokensAsync(stored.UserId, stored.FamilyId, ct))
            {
                t.RevokedAtUtc = now;
                t.RevokedReason = RefreshRevocation.Reuse;
            }
            await AuthAuditAsync(AuthEvents.RefreshTokenReuse, stored.UserId, null, false, client, "Rotated refresh token reused; family revoked.", ct);
            await unitOfWork.SaveChangesAsync(ct);
            throw InvalidRefresh();
        }
        if (!stored.IsUsable(now))
        {
            await AuthAuditAsync(AuthEvents.RefreshFailed, stored.UserId, null, false, client, "Expired or revoked refresh token.", ct);
            await unitOfWork.SaveChangesAsync(ct);
            throw InvalidRefresh();
        }

        var user = await users.GetByIdAsync(stored.UserId, ct);
        if (user is null || user.Status != UserStatus.Active || user.ActiveRoles.Count == 0)
        {
            stored.RevokedAtUtc = now;
            stored.RevokedReason = RefreshRevocation.AccountChanged;
            await AuthAuditAsync(AuthEvents.RefreshFailed, stored.UserId, null, false, client, "Account not active.", ct);
            await unitOfWork.SaveChangesAsync(ct);
            throw InvalidRefresh();
        }

        stored.ConsumedAtUtc = now;
        var response = await IssueTokensAsync(user, stored.FamilyId, client, ct);
        await AuthAuditAsync(AuthEvents.TokenRefreshed, user.Id, user.Email, true, client, null, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return response;
    }

    public async Task LogoutAsync(LogoutRequest request, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var now = clock.UtcNow;
        if (currentUser.TokenId is { } jti && !await users.IsTokenRevokedAsync(jti, ct))
        {
            await users.AddRevokedTokenAsync(new RevokedToken
            {
                Jti = jti,
                UserId = userId,
                RevokedAtUtc = now,
                ExpiresAtUtc = currentUser.TokenExpiresAtUtc ?? now.AddHours(1),
            }, ct);
        }
        if (!string.IsNullOrWhiteSpace(request.RefreshToken)
            && await users.GetRefreshTokenByHashAsync(SecureTokens.Hash(request.RefreshToken), ct) is { } refresh
            && refresh.UserId == userId)
        {
            foreach (var t in await users.ListUnrevokedRefreshTokensAsync(userId, refresh.FamilyId, ct))
            {
                t.RevokedAtUtc = now;
                t.RevokedReason = RefreshRevocation.Logout;
            }
        }
        await AuthAuditAsync(AuthEvents.Logout, userId, null, true, new ClientInfo(currentUser.IpAddress, currentUser.UserAgent), null, ct);
        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<MessageResponse> ForgotPasswordAsync(ForgotPasswordRequest request, ClientInfo client, CancellationToken ct)
    {
        new Validator().Email(nameof(request.Email), request.Email).ThrowIfInvalid();
        var normalized = SecureTokens.NormalizeEmail(request.Email!);
        var now = clock.UtcNow;
        var user = await users.GetByEmailAsync(normalized, ct);

        if (user is null || user.Status == UserStatus.Disabled)
        {
            await AuthAuditAsync(AuthEvents.PasswordResetRequested, user?.Id, normalized, false, client,
                user is null ? "Unknown email." : "Account disabled.", ct);
            await unitOfWork.SaveChangesAsync(ct);
            return new MessageResponse(ForgotPasswordMessage);
        }

        // Only the newest link is valid.
        foreach (var previous in await users.GetUsableResetTokensAsync(user.Id, now, ct)) previous.UsedAtUtc = now;

        var rawToken = SecureTokens.Create();
        await users.AddResetTokenAsync(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = SecureTokens.Hash(rawToken),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(_options.PasswordResetTokenMinutes),
            RequestedIp = client.IpAddress,
        }, ct);
        await AuthAuditAsync(AuthEvents.PasswordResetRequested, user.Id, normalized, true, client, null, ct);
        await unitOfWork.SaveChangesAsync(ct);

        var link = $"{_options.PasswordResetUrl}?token={Uri.EscapeDataString(rawToken)}";
        try
        {
            await email.SendAsync(new EmailMessage(user.Email, "Reset your Aircraft Failure Prediction password",
                $"A password reset was requested for the account '{user.Username}'.\n\nOpen this link within {_options.PasswordResetTokenMinutes} minutes " +
                $"to choose a new password (it can be used once):\n{link}\n\nIf you did not request this, you can ignore this email."), ct);
        }
        catch (Exception ex)
        {
            // Never surface delivery failures to the caller (that would disclose account existence).
            logger.LogError(ex, "Failed to send password reset email for user {UserId}", user.Id);
        }
        return new MessageResponse(ForgotPasswordMessage);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, ClientInfo client, CancellationToken ct)
    {
        new Validator()
            .Required(nameof(request.Token), request.Token, "Reset token")
            .Password(nameof(request.NewPassword), request.NewPassword, request.ConfirmPassword, nameof(request.ConfirmPassword))
            .ThrowIfInvalid();

        var now = clock.UtcNow;
        var token = await users.GetResetTokenByHashAsync(SecureTokens.Hash(request.Token!), ct);
        var user = token is null ? null : await users.GetByIdAsync(token.UserId, ct);
        if (token is null || user is null || !token.IsUsable(now) || user.Status == UserStatus.Disabled)
        {
            await AuthAuditAsync(AuthEvents.PasswordResetFailed, user?.Id, user?.Email, false, client, "Invalid or expired token.", ct);
            await unitOfWork.SaveChangesAsync(ct);
            throw new BusinessRuleException("This password reset link is invalid or has expired. Request a new one.", "invalid_or_expired_token");
        }

        token.UsedAtUtc = now;
        user.PasswordHash = hasher.Hash(user, request.NewPassword!);
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.RotateSecurityStamp(); // revokes all access tokens
        user.UpdatedAtUtc = now;
        await RevokeAllRefreshTokensAsync(users, user.Id, now, RefreshRevocation.PasswordReset, ct);
        await AuthAuditAsync(AuthEvents.PasswordResetCompleted, user.Id, user.Email, true, client, null, ct);
        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<UserDto> GetCurrentUserAsync(CancellationToken ct)
    {
        var id = currentUser.RequireUserId();
        var user = await users.GetByIdAsync(id, ct) ?? throw new NotFoundException("User", id);
        return user.ToDto();
    }

    /// <summary>Revokes every outstanding refresh token of a user (password reset, disable, role change).</summary>
    public static async Task RevokeAllRefreshTokensAsync(IUserRepository users, long userId, DateTime nowUtc, string reason, CancellationToken ct)
    {
        foreach (var t in await users.ListUnrevokedRefreshTokensAsync(userId, null, ct))
        {
            t.RevokedAtUtc = nowUtc;
            t.RevokedReason = reason;
        }
    }

    private async Task EnsureCanSignInAsync(AppUser user, ClientInfo client, CancellationToken ct)
    {
        if (user.Status == UserStatus.Disabled)
        {
            await AuthAuditAsync(AuthEvents.LoginFailed, user.Id, user.Email, false, client, "Account disabled.", ct);
            await unitOfWork.SaveChangesAsync(ct);
            throw new AuthenticationFailedException(403, "account_disabled", "This account has been disabled. Contact a dispatcher.");
        }
        if (user.Status == UserStatus.Pending || user.ActiveRoles.Count == 0)
        {
            await AuthAuditAsync(AuthEvents.LoginFailed, user.Id, user.Email, false, client, "Account pending approval.", ct);
            await unitOfWork.SaveChangesAsync(ct);
            throw new AuthenticationFailedException(403, "account_pending", "This account is awaiting approval by a flight dispatcher.");
        }
    }

    private async Task<TokenResponse> IssueTokensAsync(AppUser user, Guid familyId, ClientInfo client, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var access = tokens.Issue(user);
        var rawRefresh = SecureTokens.Create(48);
        var refreshExpires = now.AddDays(_options.RefreshTokenDays);
        await users.AddRefreshTokenAsync(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = SecureTokens.Hash(rawRefresh),
            FamilyId = familyId,
            CreatedAtUtc = now,
            ExpiresAtUtc = refreshExpires,
            CreatedByIp = client.IpAddress,
        }, ct);
        return new TokenResponse(access.AccessToken, "Bearer", access.ExpiresAtUtc, rawRefresh, refreshExpires, user.ToDto());
    }

    private Task AuthAuditAsync(string evt, long? userId, string? emailAddress, bool ok, ClientInfo client, string? detail, CancellationToken ct) =>
        audit.AddAuthAsync(new AuthAudit
        {
            EventType = evt,
            UserId = userId,
            Email = emailAddress,
            Succeeded = ok,
            IpAddress = client.IpAddress,
            UserAgent = client.UserAgent is { Length: > 512 } ua ? ua[..512] : client.UserAgent,
            Detail = detail,
            OccurredAtUtc = clock.UtcNow,
        }, ct);

    private static AuthenticationFailedException InvalidCredentials() =>
        new(401, "invalid_credentials", "The username or password is incorrect.");

    private static AuthenticationFailedException InvalidRefresh() =>
        new(401, "invalid_refresh_token", "The session has expired. Please sign in again.");

    private AuthenticationFailedException Locked() =>
        new(423, "account_locked", $"Too many failed sign-in attempts. Try again in {_options.LockoutMinutes} minutes or reset your password.");
}

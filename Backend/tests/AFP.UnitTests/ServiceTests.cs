using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Application.Mapping;
using AFP.Application.Services;
using AFP.Application.Telemetry;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Infrastructure.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AFP.UnitTests;

public class AuthServiceTests : IDisposable
{
    private readonly TestContext _t = new();
    private static readonly ClientInfo Client = new("10.0.0.1", "test");

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Register_creates_pending_user_without_role()
    {
        var response = await _t.AuthService().RegisterAsync(new("New.User", "New.User@Example.test", "New User", "Correct-Horse-9!", "Correct-Horse-9!"), Client, default);
        var user = await _t.Db.Users.SingleAsync();
        Assert.Equal(AuthService.RegisterMessage, response.Message);
        Assert.Equal("new.user@example.test", user.Email);
        Assert.Equal(UserStatus.Pending, user.Status);
        Assert.Equal("new.user", user.Username);
        Assert.Empty(await _t.Db.UserRoles.Where(r => r.UserId == user.Id).ToListAsync());
        Assert.NotEqual("Correct-Horse-9!", user.PasswordHash);
    }

    [Fact]
    public async Task Register_existing_email_gives_identical_response()
    {
        var existing = await _t.AddUserAsync(UserRole.FlightDispatcher);
        var response = await _t.AuthService().RegisterAsync(new("someone.else", existing.Email, "Someone", "Correct-Horse-9!", "Correct-Horse-9!"), Client, default);
        Assert.Equal(AuthService.RegisterMessage, response.Message);
        Assert.Equal(1, await _t.Db.Users.CountAsync());
    }

    [Fact]
    public async Task Pending_user_cannot_log_in()
    {
        var user = await _t.AddUserAsync(null, UserStatus.Pending);
        var ex = await Assert.ThrowsAsync<AuthenticationFailedException>(() => _t.AuthService().LoginAsync(new(user.Username, "Correct-Horse-9!"), Client, default));
        Assert.Equal(403, ex.StatusCode);
        Assert.Equal("account_pending", ex.Code);
    }

    [Fact]
    public async Task Login_returns_60_minute_token()
    {
        var user = await _t.AddUserAsync(UserRole.FlightDispatcher);
        var result = await _t.AuthService().LoginAsync(new(user.Username, "Correct-Horse-9!"), Client, default);
        Assert.Equal(TestContext.Now.AddMinutes(60), result.ExpiresAtUtc);
        Assert.Equal([UserRole.FlightDispatcher], result.User.Roles);
        Assert.False(string.IsNullOrEmpty(result.RefreshToken));
        Assert.Equal(TestContext.Now.AddDays(7), result.RefreshTokenExpiresAtUtc);
        Assert.Contains(await _t.Db.AuthAudits.ToListAsync(), a => a.EventType == AuthEvents.LoginSucceeded && a.UserId == user.Id);
    }

    [Fact]
    public async Task Five_failures_lock_the_account()
    {
        var user = await _t.AddUserAsync(UserRole.FlightDispatcher);
        var auth = _t.AuthService();
        for (var i = 0; i < 4; i++)
        {
            var e = await Assert.ThrowsAsync<AuthenticationFailedException>(() => auth.LoginAsync(new(user.Username, "wrong"), Client, default));
            Assert.Equal(401, e.StatusCode);
        }
        var locked = await Assert.ThrowsAsync<AuthenticationFailedException>(() => auth.LoginAsync(new(user.Username, "wrong"), Client, default));
        Assert.Equal(423, locked.StatusCode);
        // Even the correct password is refused while locked.
        var still = await Assert.ThrowsAsync<AuthenticationFailedException>(() => auth.LoginAsync(new(user.Username, "Correct-Horse-9!"), Client, default));
        Assert.Equal("account_locked", still.Code);
        _t.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.NotNull(await auth.LoginAsync(new(user.Username, "Correct-Horse-9!"), Client, default));
    }

    [Fact]
    public async Task Unknown_email_login_is_generic_401()
    {
        var ex = await Assert.ThrowsAsync<AuthenticationFailedException>(() => _t.AuthService().LoginAsync(new("nobody", "x"), Client, default));
        Assert.Equal("invalid_credentials", ex.Code);
    }

    [Fact]
    public async Task Forgot_password_does_not_disclose_account_existence_and_stores_only_hash()
    {
        var user = await _t.AddUserAsync(UserRole.FlightDispatcher);
        var auth = _t.AuthService();
        var known = await auth.ForgotPasswordAsync(new(user.Email), Client, default);
        var unknown = await auth.ForgotPasswordAsync(new("ghost@example.test"), Client, default);
        Assert.Equal(known.Message, unknown.Message);
        Assert.Single(_t.Email.Sent);

        var rawToken = Uri.UnescapeDataString(_t.Email.Sent[0].TextBody.Split("token=")[1].Split('\n')[0].Trim());
        var stored = await _t.Db.PasswordResetTokens.SingleAsync();
        Assert.NotEqual(rawToken, stored.TokenHash);
        Assert.Equal(SecureTokens.Hash(rawToken), stored.TokenHash);
    }

    [Fact]
    public async Task Reset_token_is_single_use_and_rotates_security_stamp()
    {
        var user = await _t.AddUserAsync(UserRole.FlightDispatcher);
        var stamp = user.SecurityStamp;
        var auth = _t.AuthService();
        await auth.ForgotPasswordAsync(new(user.Email), Client, default);
        var token = Uri.UnescapeDataString(_t.Email.Sent[0].TextBody.Split("token=")[1].Split('\n')[0].Trim());

        await auth.ResetPasswordAsync(new(token, "Another-Pass-77#", "Another-Pass-77#"), Client, default);
        Assert.NotEqual(stamp, (await _t.Db.Users.SingleAsync()).SecurityStamp);
        var reuse = await Assert.ThrowsAsync<BusinessRuleException>(() => auth.ResetPasswordAsync(new(token, "Third-Pass-77#xx", "Third-Pass-77#xx"), Client, default));
        Assert.Equal("invalid_or_expired_token", reuse.Code);
        Assert.NotNull(await auth.LoginAsync(new(user.Username, "Another-Pass-77#"), Client, default));
    }

    [Fact]
    public async Task Expired_reset_token_is_rejected()
    {
        var user = await _t.AddUserAsync(UserRole.FlightDispatcher);
        var auth = _t.AuthService();
        await auth.ForgotPasswordAsync(new(user.Email), Client, default);
        var token = Uri.UnescapeDataString(_t.Email.Sent[0].TextBody.Split("token=")[1].Split('\n')[0].Trim());
        _t.Clock.Advance(TimeSpan.FromMinutes(31));
        await Assert.ThrowsAsync<BusinessRuleException>(() => auth.ResetPasswordAsync(new(token, "Another-Pass-77#", "Another-Pass-77#"), Client, default));
    }

    [Fact]
    public async Task Logout_revokes_token_and_session_validator_rejects_it()
    {
        var user = await _t.AddUserAsync(UserRole.FlightDispatcher);
        _t.CurrentUser.SignInAs(user);
        _t.CurrentUser.TokenId = "jti-1";
        var validator = new SessionValidator(_t.Users);
        Assert.True(await validator.IsValidAsync(user.Id, "jti-1", user.SecurityStamp, default));
        await _t.AuthService().LogoutAsync(new LogoutRequest(null), default);
        Assert.False(await validator.IsValidAsync(user.Id, "jti-1", user.SecurityStamp, default));
    }

    [Fact]
    public async Task Bootstrap_admin_runs_only_once()
    {
        var service = new AdminBootstrapService(_t.Users, _t.Audit, _t.UnitOfWork, _t.Hasher, _t.Clock);
        var id = await service.CreateFirstDispatcherAsync("Ops.Admin", "admin@example.test", "Admin", "Correct-Horse-9!", default);
        Assert.Equal([UserRole.FlightDispatcher], (await _t.Users.GetByIdAsync(id, default))!.ActiveRoles);
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateFirstDispatcherAsync("second", "second@example.test", "Admin2", "Correct-Horse-9!", default));
    }

    /// <summary>
    /// A fresh installation needs a Data Engineer: only that role approves other accounts. Bootstrap creates the first
    /// one, once; technicians are never bootstrapped (they register and are approved).
    /// </summary>
    [Fact]
    public async Task Bootstrap_creates_the_first_data_engineer_once_and_never_a_technician()
    {
        var service = new AdminBootstrapService(_t.Users, _t.Audit, _t.UnitOfWork, _t.Hasher, _t.Clock);
        var id = await service.CreateFirstAccountAsync(UserRole.DataEngineer, "data.admin", "data@example.test", "Data", "Correct-Horse-9!", default);
        var user = (await _t.Users.GetByIdAsync(id, default))!;
        Assert.Equal([UserRole.DataEngineer], user.ActiveRoles);
        Assert.Equal(UserStatus.Active, user.Status);
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateFirstAccountAsync(UserRole.DataEngineer, "data2", "data2@example.test", "Data2", "Correct-Horse-9!", default));
        // The dispatcher bootstrap is independent of the Data Engineer one.
        await service.CreateFirstAccountAsync(UserRole.FlightDispatcher, "ops", "ops@example.test", "Ops", "Correct-Horse-9!", default);
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.CreateFirstAccountAsync(UserRole.MaintenanceTechnician, "tech", "tech@example.test", "Tech", "Correct-Horse-9!", default));
    }
}

public class UserAdminServiceTests : IDisposable
{
    private readonly TestContext _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Approve_assigns_role_and_disable_revokes_sessions()
    {
        var admin = await _t.AddUserAsync(UserRole.FlightDispatcher);
        _t.CurrentUser.SignInAs(admin);
        var pending = await _t.AddUserAsync(null, UserStatus.Pending);
        var service = new UserAdminService(_t.Users, _t.Auditor, _t.UnitOfWork, _t.CurrentUser, _t.Clock);

        var approved = await service.ApproveAsync(pending.Id, new ApproveUserRequest([UserRole.MaintenanceTechnician]), default);
        Assert.Equal(UserStatus.Active, approved.Status);
        Assert.Equal([UserRole.MaintenanceTechnician], approved.Roles);

        var stamp = (await _t.Users.GetByIdAsync(pending.Id, default))!.SecurityStamp;
        await service.DisableAsync(pending.Id, new DisableUserRequest("Left the company"), default);
        Assert.NotEqual(stamp, (await _t.Users.GetByIdAsync(pending.Id, default))!.SecurityStamp);
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.DisableAsync(admin.Id, new DisableUserRequest("self"), default));
    }
}

public class WorkOrderServiceTests : IDisposable
{
    private readonly TestContext _t = new();
    public void Dispose() => _t.Dispose();

    private async Task<(AppUser Dispatcher, AppUser Tech, Aircraft Aircraft)> ArrangeAsync()
    {
        var dispatcher = await _t.AddUserAsync(UserRole.FlightDispatcher);
        var tech = await _t.AddUserAsync(UserRole.MaintenanceTechnician);
        return (dispatcher, tech, await _t.AddAircraftAsync());
    }

    /// <summary>
    /// The reported-work review loop: a dispatcher sending REPORTED work back returns it to ASSIGNED, the
    /// technician reports again, and BOTH reports survive. Nothing overwrites an earlier report.
    /// </summary>
    [Fact]
    public async Task Reassigned_work_is_reported_again_and_both_reports_are_kept()
    {
        var (dispatcher, tech, aircraft) = await ArrangeAsync();
        var service = _t.WorkOrderService();

        _t.CurrentUser.SignInAs(dispatcher);
        var wo = await service.CreateAsync(new CreateWorkOrderRequest(aircraft.Id, "ENG1", null, null,
            WorkOrderPriority.High, "Oil pressure low", "Investigate", null, tech.Id), default);

        _t.CurrentUser.SignInAs(tech);
        await service.StartInspectionAsync(wo.Id, default);
        await service.RecordInspectionAsync(wo.Id, new RecordInspectionRequest("Regulator sticking"), default);
        await service.ResolveAsync(wo.Id, new ResolveWorkOrderRequest("Cleaned regulator", false, "First attempt notes", "ENG_INSP"), default);

        _t.CurrentUser.SignInAs(dispatcher);
        var reopened = await service.ReopenAsync(wo.Id, new ReasonRequest("Pressure still low on next flight"), default);
        // Sent back for more work: ASSIGNED, so the technician has to start again.
        Assert.Equal(WorkOrderStatus.Assigned, reopened.Status);

        _t.CurrentUser.SignInAs(tech);
        await service.StartInspectionAsync(wo.Id, default);
        await service.ResolveAsync(wo.Id, new ResolveWorkOrderRequest("Replaced regulator", true, "Second attempt notes", "OIL_REPL"), default);

        var reports = await _t.Db.WorkOrderReports.Where(r => r.WorkOrderId == wo.Id).OrderBy(r => r.Sequence).ToListAsync();
        Assert.Equal(2, reports.Count);
        Assert.Equal([1, 2], reports.Select(r => r.Sequence));
        Assert.Equal("Cleaned regulator", reports[0].CorrectiveAction);   // the first report is not overwritten
        Assert.Equal("First attempt notes", reports[0].ResolutionNotes);
        Assert.Equal("Replaced regulator", reports[1].CorrectiveAction);
        Assert.All(reports, r => Assert.Equal(tech.Id, r.TechnicianUserId));   // authenticated identity
    }

    /// <summary>Completing a ticket must never alter what the technician reported.</summary>
    [Fact]
    public async Task Dispatcher_review_is_stored_separately_and_never_overwrites_the_technician_report()
    {
        var (dispatcher, tech, aircraft) = await ArrangeAsync();
        var service = _t.WorkOrderService();

        _t.CurrentUser.SignInAs(dispatcher);
        var wo = await service.CreateAsync(new CreateWorkOrderRequest(aircraft.Id, "ENG1", null, null,
            WorkOrderPriority.Medium, "Routine", "Check", null, tech.Id), default);

        _t.CurrentUser.SignInAs(tech);
        await service.StartInspectionAsync(wo.Id, default);
        await service.RecordInspectionAsync(wo.Id, new RecordInspectionRequest("No defect found"), default);
        await service.ResolveAsync(wo.Id, new ResolveWorkOrderRequest("Inspected", false, "Technician notes stand", "ENG_INSP"), default);

        _t.CurrentUser.SignInAs(dispatcher);
        await service.CloseAsync(wo.Id, new CloseWorkOrderRequest("Dispatcher accepts the work"), default);

        var stored = await _t.Db.WorkOrders.SingleAsync(w => w.Id == wo.Id);
        Assert.Equal("Technician notes stand", stored.ResolutionNotes);          // untouched
        Assert.Equal("Dispatcher accepts the work", stored.DispatcherReviewNotes);
        Assert.Equal("Technician notes stand", (await _t.Db.WorkOrderReports.SingleAsync()).ResolutionNotes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too short")]   // 9 characters
    public async Task Maintenance_notes_under_ten_characters_are_rejected(string notes)
    {
        var (dispatcher, tech, aircraft) = await ArrangeAsync();
        var service = _t.WorkOrderService();
        _t.CurrentUser.SignInAs(dispatcher);
        var wo = await service.CreateAsync(new CreateWorkOrderRequest(aircraft.Id, "ENG1", null, null,
            WorkOrderPriority.Low, "t", "d", null, tech.Id), default);
        _t.CurrentUser.SignInAs(tech);
        await service.StartInspectionAsync(wo.Id, default);

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.ResolveAsync(wo.Id, new ResolveWorkOrderRequest("Did work", false, notes, "ENG_INSP"), default));
        Assert.Equal(0, await _t.Db.WorkOrderReports.CountAsync());
    }

    [Fact]
    public async Task Dispatcher_assigns_technician_works_dispatcher_closes_and_outcome_is_recorded()
    {
        var (dispatcher, tech, aircraft) = await ArrangeAsync();
        var prediction = new AircraftRiskPrediction
        {
            AircraftId = aircraft.Id, PredictionTimestampUtc = TestContext.Now, FeatureCutoffUtc = TestContext.Now, HorizonDays = 30,
            RiskCategory = "Elevated", Basis = PredictionBasis.DescriptiveIndicator,
        };
        _t.Db.RiskPredictions.Add(prediction);
        await _t.Db.SaveChangesAsync();
        var service = _t.WorkOrderService();

        _t.CurrentUser.SignInAs(dispatcher);
        var wo = await service.CreateAsync(new CreateWorkOrderRequest(aircraft.Id, "ENG1-TP", null, prediction.Id, WorkOrderPriority.High,
            "Investigate pressure trend", "Pressure trend flagged", null, tech.Id), default);
        Assert.Equal(WorkOrderStatus.Assigned, wo.Status);
        Assert.StartsWith("WO-2026-", wo.WorkOrderNumber);
        Assert.Equal(["assign", "cancel"], wo.AllowedTransitions.OrderBy(x => x));   // dispatcher view

        _t.CurrentUser.SignInAs(tech);
        var detail = await service.GetAsync(wo.Id, default);
        Assert.Equal(["start-inspection"], detail.AllowedTransitions);              // technician view
        Assert.Equal(aircraft.RegistrationNumber, detail.Aircraft.RegistrationNumber);
        Assert.Equal("Elevated", detail.Prediction!.RiskCategory);                  // AI diagnosis embedded
        await service.StartInspectionAsync(wo.Id, default);
        await service.RecordInspectionAsync(wo.Id, new RecordInspectionRequest("Worn seal on sensor housing"), default);
        var resolved = await service.ResolveAsync(wo.Id, new ResolveWorkOrderRequest("Replaced seal", true, "Leak test passed", "ENG_INSP"), default);
        Assert.Empty(resolved.AllowedTransitions);                                    // closing is the dispatcher's call
        await Assert.ThrowsAsync<ForbiddenException>(() => service.CloseAsync(wo.Id, new CloseWorkOrderRequest(null), default));

        _t.CurrentUser.SignInAs(dispatcher);
        var closed = await service.CloseAsync(wo.Id, new CloseWorkOrderRequest(null), default);
        Assert.Equal(WorkOrderStatus.Closed, closed.Status);
        var log = await _t.Db.MaintenanceLogs.SingleAsync();
        Assert.Equal(MaintenanceEventType.Unscheduled, log.EventType);
        Assert.True(log.IsVerified);
        Assert.True(log.ActualFailureConfirmed);
        Assert.Equal(MaintenanceOutcome.UnscheduledMaintenance, (await _t.Db.RiskPredictions.SingleAsync()).ActualOutcome);
        Assert.NotNull((await _t.Db.Aircraft.SingleAsync()).LastInspectionAtUtc);
        Assert.True(await _t.Db.ActionAudits.CountAsync(a => a.EntityId == wo.Id.ToString()) >= 5);
    }

    [Fact]
    public async Task Technicians_only_see_and_work_their_own_tickets()
    {
        var (dispatcher, tech, aircraft) = await ArrangeAsync();
        var otherTech = await _t.AddUserAsync(UserRole.MaintenanceTechnician);
        var service = _t.WorkOrderService();
        _t.CurrentUser.SignInAs(dispatcher);
        var mine = await service.CreateAsync(new CreateWorkOrderRequest(aircraft.Id, null, null, null, WorkOrderPriority.Low, "Mine", "d", null, tech.Id), default);
        var theirs = await service.CreateAsync(new CreateWorkOrderRequest(aircraft.Id, null, null, null, WorkOrderPriority.Low, "Theirs", "d", null, otherTech.Id), default);
        await service.CreateAsync(new CreateWorkOrderRequest(aircraft.Id, null, null, null, WorkOrderPriority.Low, "Unassigned", "d", null, null), default);
        Assert.Equal(3, (await service.ListAsync(new WorkOrderFilter(null, null, null, null), new PageRequest(), default)).TotalCount);

        _t.CurrentUser.SignInAs(tech);
        var list = await service.ListAsync(new WorkOrderFilter(null, null, null, null), new PageRequest(), default);
        Assert.Equal(mine.Id, Assert.Single(list.Items).Id);
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(theirs.Id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => service.StartInspectionAsync(theirs.Id, default));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.CancelAsync(mine.Id, new ReasonRequest("no"), default));
    }

    [Fact]
    public async Task Dispatcher_cannot_do_the_technicians_work()
    {
        var (dispatcher, tech, aircraft) = await ArrangeAsync();
        var service = _t.WorkOrderService();
        _t.CurrentUser.SignInAs(dispatcher);
        var wo = await service.CreateAsync(new CreateWorkOrderRequest(aircraft.Id, null, null, null, WorkOrderPriority.Low, "t", "d", null, tech.Id), default);
        await Assert.ThrowsAsync<ForbiddenException>(() => service.StartInspectionAsync(wo.Id, default));
    }

    [Fact]
    public async Task Stale_concurrency_stamp_is_rejected()
    {
        var (dispatcher, _, aircraft) = await ArrangeAsync();
        _t.CurrentUser.SignInAs(dispatcher);
        var service = _t.WorkOrderService();
        var wo = await service.CreateAsync(new CreateWorkOrderRequest(aircraft.Id, null, null, null, WorkOrderPriority.Low, "Check", "Routine", null, null), default);
        await service.UpdateAsync(wo.Id, new UpdateWorkOrderRequest(wo.ConcurrencyStamp, "Check 2", null, null, null, null), default);
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.UpdateAsync(wo.Id, new UpdateWorkOrderRequest(wo.ConcurrencyStamp, "Check 3", null, null, null, null), default));
    }

    [Fact]
    public async Task Invalid_transition_is_a_business_rule_error()
    {
        var (dispatcher, _, aircraft) = await ArrangeAsync();
        _t.CurrentUser.SignInAs(dispatcher);
        var service = _t.WorkOrderService();
        var wo = await service.CreateAsync(new CreateWorkOrderRequest(aircraft.Id, null, null, null, WorkOrderPriority.Low, "Check", "Routine", null, null), default);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CloseAsync(wo.Id, new CloseWorkOrderRequest(null), default));
        Assert.Equal("invalid_transition", ex.Code);
    }

    [Fact]
    public async Task Only_active_technicians_can_be_assigned()
    {
        var (dispatcher, _, aircraft) = await ArrangeAsync();
        _t.CurrentUser.SignInAs(dispatcher);
        await Assert.ThrowsAsync<ValidationException>(() => _t.WorkOrderService().CreateAsync(
            new CreateWorkOrderRequest(aircraft.Id, null, null, null, WorkOrderPriority.Low, "t", "d", null, dispatcher.Id), default));
    }

    [Fact]
    public async Task Technician_can_log_maintenance_only_for_aircraft_on_own_open_ticket()
    {
        var (dispatcher, tech, aircraft) = await ArrangeAsync();
        var other = await _t.AddAircraftAsync("DEMO-A02");
        _t.CurrentUser.SignInAs(dispatcher);
        await _t.WorkOrderService().CreateAsync(new CreateWorkOrderRequest(aircraft.Id, null, null, null, WorkOrderPriority.Low, "t", "d", null, tech.Id), default);
        var logs = new MaintenanceLogService(_t.Maintenance, _t.Aircraft, _t.Auditor, _t.UnitOfWork, _t.CurrentUser, _t.Clock);

        _t.CurrentUser.SignInAs(tech);
        CreateMaintenanceLogRequest Log(Guid id) => new(id, null, MaintenanceEventType.Inspection, TestContext.Now, "Borescope done", null, true);
        Assert.Equal(aircraft.Id, (await logs.CreateAsync(Log(aircraft.Id), default)).AircraftId);
        await Assert.ThrowsAsync<ForbiddenException>(() => logs.CreateAsync(Log(other.Id), default));
    }
}

public class TokenAndRoleTests : IDisposable
{
    private readonly TestContext _t = new();
    private static readonly ClientInfo Client = new("10.0.0.1", "test");
    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Refresh_rotates_and_reuse_revokes_the_family()
    {
        var user = await _t.AddUserAsync(UserRole.MaintenanceTechnician);
        var auth = _t.AuthService();
        var first = await auth.LoginAsync(new(user.Username, "Correct-Horse-9!"), Client, default);
        var second = await auth.RefreshAsync(new RefreshRequest(first.RefreshToken), Client, default);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        var reuse = await Assert.ThrowsAsync<AuthenticationFailedException>(() => auth.RefreshAsync(new RefreshRequest(first.RefreshToken), Client, default));
        Assert.Equal("invalid_refresh_token", reuse.Code);
        // Theft detection: the newer token of the same family is revoked as well.
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => auth.RefreshAsync(new RefreshRequest(second.RefreshToken), Client, default));
        Assert.Contains(await _t.Db.AuthAudits.ToListAsync(), a => a.EventType == AuthEvents.RefreshTokenReuse);
        Assert.All(await _t.Db.RefreshTokens.ToListAsync(), r => Assert.Equal(SecureTokens.Hash(r.TokenHash).Length, r.TokenHash.Length));
        Assert.DoesNotContain(await _t.Db.RefreshTokens.ToListAsync(), r => r.TokenHash == first.RefreshToken);
    }

    [Fact]
    public async Task Expired_refresh_token_is_rejected_and_logout_revokes_family()
    {
        var user = await _t.AddUserAsync(UserRole.FlightDispatcher);
        var auth = _t.AuthService();
        var login = await auth.LoginAsync(new(user.Username, "Correct-Horse-9!"), Client, default);
        _t.CurrentUser.SignInAs(user);
        await auth.LogoutAsync(new LogoutRequest(login.RefreshToken), default);
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => auth.RefreshAsync(new RefreshRequest(login.RefreshToken), Client, default));

        var again = await auth.LoginAsync(new(user.Username, "Correct-Horse-9!"), Client, default);
        _t.Clock.Advance(TimeSpan.FromDays(8));
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => auth.RefreshAsync(new RefreshRequest(again.RefreshToken), Client, default));
    }

    [Fact]
    public async Task Role_changes_keep_history_and_revoke_refresh_tokens()
    {
        var dispatcher = await _t.AddUserAsync(UserRole.FlightDispatcher);
        var user = await _t.AddUserAsync(UserRole.MaintenanceTechnician);
        var login = await _t.AuthService().LoginAsync(new(user.Username, "Correct-Horse-9!"), Client, default);
        _t.CurrentUser.SignInAs(dispatcher);
        var admin = new UserAdminService(_t.Users, _t.Auditor, _t.UnitOfWork, _t.CurrentUser, _t.Clock);

        var updated = await admin.AssignRolesAsync(user.Id, new AssignRolesRequest([UserRole.FlightDispatcher]), default);
        Assert.Equal([UserRole.FlightDispatcher], updated.Roles);
        var history = await _t.Db.UserRoles.Where(r => r.UserId == user.Id).OrderBy(r => r.UserRoleId).ToListAsync();
        Assert.Equal(LifecycleStatus.Inactive, history[0].StatusId);   // technician assignment retained as history
        Assert.Equal(dispatcher.Id, history[0].RevokedByUserId);
        Assert.Equal(LifecycleStatus.Active, history[1].StatusId);
        await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            _t.AuthService().RefreshAsync(new RefreshRequest(login.RefreshToken), Client, default));

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            admin.AssignRolesAsync(dispatcher.Id, new AssignRolesRequest([UserRole.MaintenanceTechnician]), default));
        await Assert.ThrowsAsync<ValidationException>(() => admin.AssignRolesAsync(user.Id, new AssignRolesRequest([]), default));
        Assert.Equal(["FlightDispatcher", "MaintenanceTechnician", "DataEngineer"], (await admin.ListRolesAsync(default)).Select(r => r.RoleName));
    }

    [Fact]
    public async Task Users_can_sign_in_with_username_or_email_case_insensitively()
    {
        var user = await _t.AddUserAsync(UserRole.MaintenanceTechnician);
        var auth = _t.AuthService();
        Assert.Equal(user.Id, (await auth.LoginAsync(new(user.Username.ToUpperInvariant(), "Correct-Horse-9!"), Client, default)).User.Id);
        Assert.Equal(user.Id, (await auth.LoginAsync(new(user.Email.ToUpperInvariant(), "Correct-Horse-9!"), Client, default)).User.Id);
    }

    [Fact]
    public async Task Pending_or_role_less_users_cannot_sign_in()
    {
        var noRole = await _t.AddUserAsync(null, UserStatus.Active);
        var ex = await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            _t.AuthService().LoginAsync(new(noRole.Username, "Correct-Horse-9!"), Client, default));
        Assert.Equal("account_pending", ex.Code);
    }
}

public class AircraftServiceTests : IDisposable
{
    private readonly TestContext _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Grounding_flag_grounds_aircraft_and_blocks_availability()
    {
        var tech = await _t.AddUserAsync(UserRole.MaintenanceTechnician);
        _t.CurrentUser.SignInAs(tech);
        var aircraft = await _t.AddAircraftAsync();
        var service = _t.AircraftService();
        var flag = await service.RaiseFlagAsync(aircraft.Id, new RaiseInspectionFlagRequest(InspectionFlagSeverity.Grounding, "Hydraulic leak", null, null, null), default);
        var detail = await service.GetAsync(aircraft.Id, default);
        Assert.Equal(OperationalStatus.Grounded, detail.OperationalStatus);
        Assert.Equal(DispatchReadiness.NotAvailable, detail.DispatchAdvisory.Readiness);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.ChangeStatusAsync(aircraft.Id, new ChangeAircraftStatusRequest(OperationalStatus.Available, "Fixed"), default));
        Assert.Contains("Grounding", ex.Message);

        await service.ClearFlagAsync(flag.Id, new ClearInspectionFlagRequest("Leak repaired, ops check OK"), default);
        // Clearing the flag does not restore availability automatically.
        Assert.Equal(OperationalStatus.Grounded, (await service.GetAsync(aircraft.Id, default)).OperationalStatus);
        var available = await service.ChangeStatusAsync(aircraft.Id, new ChangeAircraftStatusRequest(OperationalStatus.Available, "Released by engineering"), default);
        Assert.Equal(OperationalStatus.Available, available.OperationalStatus);
    }

    [Fact]
    public async Task Duplicate_registration_conflicts()
    {
        await _t.AddAircraftAsync("DEMO-A01");
        await Assert.ThrowsAsync<ConflictException>(() => _t.AircraftService().CreateAsync(
            new UpsertAircraftRequest("demo-a01", "A320neo", "Airbus", "East", null), default));
    }
}

public class ModelAndPredictionTests : IDisposable
{
    private readonly TestContext _t = new();
    public void Dispose() => _t.Dispose();

    private async Task<ModelVersion> AddModelAsync(ModelType type, ModelStatus status, string metrics = "{}")
    {
        var model = new ModelVersion
        {
            ModelName = type == ModelType.AnomalyDetection ? "isolation_forest" : "failure_rf", Version = Guid.NewGuid().ToString("N")[..12],
            ModelType = type, Algorithm = "IsolationForest", Status = status, ArtifactPath = "x", ArtifactSha256 = new string('a', 64),
            Metrics = metrics, CreatedAtUtc = TestContext.Now,
        };
        _t.Db.ModelVersions.Add(model);
        await _t.Db.SaveChangesAsync();
        return model;
    }

    /// <summary>
    /// Readiness Ready + all required metrics present is NOT enough: a test split of 103 positives and
    /// 5 negatives cannot establish precision or false-alert rate, whatever PR-AUC it reports.
    /// </summary>
    [Fact]
    public async Task A_failure_model_whose_test_split_has_too_few_negatives_cannot_be_approved()
    {
        _t.Db.PipelineRuns.Add(new PipelineRun
        {
            JobType = PipelineJobType.DatasetAudit, Status = PipelineRunStatus.Succeeded, RequestedAtUtc = TestContext.Now,
            ResultSummary = """{"failure_prediction_readiness":{"status":"Ready","reasons":[]}}""",
        });
        await _t.Db.SaveChangesAsync();

        // The real shape reported by afp-failure-prediction v20261023063647.
        const string metrics = """
        {"test":{"positives":103,"negatives":5,"precision":0.978,"recall":0.874,"pr_auc":0.994,"false_alert_rate":0.4}}
        """;
        var model = await AddModelAsync(ModelType.FailurePrediction, ModelStatus.Registered, metrics);
        _t.CurrentUser.SignInAs(await _t.AddUserAsync(UserRole.DataEngineer));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => _t.ModelService().ApproveAsync(model.Id, new NotesRequest("reviewed"), default));

        Assert.Equal("insufficient_test_examples", error.Code);
        Assert.Equal(ModelStatus.Registered, _t.Db.ModelVersions.Single(m => m.Id == model.Id).Status);
    }

    [Fact]
    public async Task A_failure_model_with_a_balanced_test_split_passes_the_split_gate()
    {
        _t.Db.PipelineRuns.Add(new PipelineRun
        {
            JobType = PipelineJobType.DatasetAudit, Status = PipelineRunStatus.Succeeded, RequestedAtUtc = TestContext.Now,
            ResultSummary = """{"failure_prediction_readiness":{"status":"Ready","reasons":[]}}""",
        });
        await _t.Db.SaveChangesAsync();

        const string metrics = """
        {"test":{"positives":60,"negatives":45,"precision":0.8,"recall":0.7,"pr_auc":0.82,"false_alert_rate":0.1}}
        """;
        var model = await AddModelAsync(ModelType.FailurePrediction, ModelStatus.Registered, metrics);
        _t.CurrentUser.SignInAs(await _t.AddUserAsync(UserRole.DataEngineer));

        var approved = await _t.ModelService().ApproveAsync(model.Id, new NotesRequest("reviewed"), default);

        Assert.Equal(ModelStatus.Approved, approved.Status);
    }

    [Fact]
    public async Task Failure_prediction_is_blocked_without_dataset_audit()
    {
        var status = await _t.FailureStatus().GetAsync(default);
        Assert.Equal("Blocked", status.Status);
        Assert.Contains(status.Reasons, r => r.Contains("No dataset audit"));
    }

    [Fact]
    public async Task Blocked_audit_reasons_are_reported()
    {
        _t.Db.PipelineRuns.Add(new PipelineRun
        {
            JobType = PipelineJobType.DatasetAudit, Status = PipelineRunStatus.Succeeded, RequestedAtUtc = TestContext.Now,
            ResultSummary = """{"failure_prediction_readiness":{"status":"Blocked","reasons":["Only 0 verified unscheduled maintenance events (minimum 30)."]}}""",
        });
        await _t.Db.SaveChangesAsync();
        var status = await _t.FailureStatus().GetAsync(default);
        Assert.Equal("Blocked", status.Status);
        Assert.Equal("Only 0 verified unscheduled maintenance events (minimum 30).", Assert.Single(status.Reasons));
    }

    [Fact]
    public async Task Leadership_cannot_approve_failure_model_while_blocked()
    {
        _t.CurrentUser.SignInAs(await _t.AddUserAsync(UserRole.FlightDispatcher));
        var model = await AddModelAsync(ModelType.FailurePrediction, ModelStatus.Registered, """{"precision":0.5,"recall":0.5,"pr_auc":0.4,"false_alert_rate":0.1}""");
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => _t.ModelService().ApproveAsync(model.Id, new NotesRequest("ok"), default));
        Assert.Equal("prediction_blocked", ex.Code);
    }

    [Fact]
    public async Task Anomaly_model_activation_retires_previous_active()
    {
        _t.CurrentUser.SignInAs(await _t.AddUserAsync(UserRole.FlightDispatcher));
        var old = await AddModelAsync(ModelType.AnomalyDetection, ModelStatus.Active);
        var next = await AddModelAsync(ModelType.AnomalyDetection, ModelStatus.Registered);
        var service = _t.ModelService();
        await service.ApproveAsync(next.Id, new NotesRequest("Reviewed score distribution"), default);
        var active = await service.ActivateAsync(next.Id, default);
        Assert.Equal(ModelStatus.Active, active.Status);
        Assert.Equal(ModelStatus.Retired, (await _t.Db.ModelVersions.SingleAsync(m => m.Id == old.Id)).Status);
    }

    [Fact]
    public void Unvalidated_probability_is_never_exposed()
    {
        var dto = new AircraftRiskPrediction
        {
            Probability = 0.42, ProbabilityValidated = false, RiskCategory = "High", Basis = PredictionBasis.DescriptiveIndicator,
        }.ToDto();
        Assert.Null(dto.Probability);
        Assert.Contains("not a calibrated failure probability", dto.Disclaimer);
    }
}

public class TelemetryImportProcessorTests : IDisposable
{
    private readonly TestContext _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "afp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _t.Dispose();
        if (Directory.Exists(_dir))
        {
            foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public async Task Import_validates_deduplicates_and_summarises()
    {
        var user = await _t.AddUserAsync(UserRole.FlightDispatcher);
        var aircraft = await _t.AddAircraftAsync("DEMO-A01");
        _t.Db.TelemetryRecords.Add(new TelemetryRecord
        {
            AircraftId = aircraft.Id, SensorId = "S1", RecordedAtUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            TemperatureC = 20, PressureKpa = 100, DataSource = "seed", IngestedAtUtc = TestContext.Now,
        });
        await _t.Db.SaveChangesAsync();

        var csv = string.Join("\n",
            "aircraft_registration,sensor_id,timestamp_utc,temperature,temperature_unit,pressure,pressure_unit,data_source",
            "DEMO-A01,S1,2026-08-01T00:00:00Z,20,C,100,kPa,ACARS",   // duplicate of stored row
            "DEMO-A01,S1,2026-08-01T00:10:00Z,21,C,100.5,kPa,ACARS",
            "DEMO-A01,S1,2026-08-01T00:10:00Z,21,C,100.5,kPa,ACARS",   // in-file duplicate
            "DEMO-A01,S1,2026-08-01T00:20:00Z,70,F,14.6,psi,ACARS",
            "DEMO-A01,S2,2026-08-01T00:20:00Z,22,C,,,ACARS",           // suspect: missing pressure
            "ZZ-UNKNOWN,S1,2026-08-01T00:20:00Z,22,C,100,kPa,ACARS",   // unknown aircraft
            "DEMO-A01,S1,2026-08-01 00:30:00,22,C,100,kPa,ACARS",      // naive timestamp
            "");

        var store = new LocalImportFileStore(Options.Create(new TelemetryImportOptions { StorageDirectory = _dir }));
        var importId = Guid.NewGuid();
        var path = await store.SaveAsync(importId, "batch.csv", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv)), default);
        _t.Db.TelemetryImports.Add(new TelemetryImport
        {
            Id = importId, FileName = "batch.csv", FileSha256 = new string('b', 64), StoredPath = path,
            RequestedByUserId = user.Id, RequestedAtUtc = TestContext.Now,
        });
        await _t.Db.SaveChangesAsync();

        var processor = new TelemetryImportProcessor(_t.Telemetry, _t.Aircraft, _t.Runs, store, _t.UnitOfWork, _t.Clock,
            Options.Create(new TelemetryImportOptions { BatchSize = 2 }), NullLogger<TelemetryImportProcessor>.Instance);
        await processor.ProcessAsync(importId, default);

        _t.Db.ChangeTracker.Clear();
        var import = await _t.Db.TelemetryImports.SingleAsync();
        Assert.Equal(TelemetryImportStatus.Completed, import.Status);
        Assert.Equal(7, import.TotalRows);
        Assert.Equal(3, import.AcceptedRows);
        Assert.Equal(4, import.RejectedRows);
        Assert.Equal(2, import.DuplicateRows);
        Assert.Contains(import.Warnings, w => w.Contains("ZZ-UNKNOWN"));
        Assert.Contains(import.Warnings, w => w.Contains("no pressure value"));

        Assert.Equal(4, await _t.Db.TelemetryRecords.CountAsync());
        Assert.Equal(1, await _t.Db.TelemetryRecords.CountAsync(r => r.QualityStatus == QualityStatus.Suspect));
        var converted = await _t.Db.TelemetryRecords.SingleAsync(r => r.TemperatureUnit == "F");
        Assert.Equal(70, converted.TemperatureValue);          // original preserved
        Assert.Equal(21.1111, converted.TemperatureC!.Value, 3);
        Assert.Equal(4, await _t.Db.TelemetryRejectedRows.CountAsync());
        Assert.Equal(new DateTime(2026, 8, 1, 0, 20, 0, DateTimeKind.Utc), (await _t.Db.Aircraft.SingleAsync()).LastTelemetryAtUtc);
        Assert.Equal(PipelineRunStatus.Succeeded, (await _t.Db.PipelineRuns.SingleAsync()).Status);
        Assert.True(File.Exists(path));                         // original file kept
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
    }
}

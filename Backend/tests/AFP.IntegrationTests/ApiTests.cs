using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AFP.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace AFP.IntegrationTests;

public class AuthorizationMatrixTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    public static TheoryData<string> ProtectedEndpoints => new()
    {
        "/api/v1/auth/me", "/api/v1/aircraft", "/api/v1/telemetry/imports", "/api/v1/anomalies", "/api/v1/predictions",
        "/api/v1/work-orders", "/api/v1/pipelines/runs", "/api/v1/models", "/api/v1/reports/fleet-health",
        "/api/v1/audit/actions", "/api/v1/admin/users", "/api/v1/admin/roles", "/api/v1/dashboards/technician", "/api/v1/development",
    };

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task Anonymous_requests_are_rejected_with_problem_details(string url)
    {
        var response = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Forged_token_is_rejected()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/aircraft")).StatusCode);
    }

    // Dispatcher = the five approved dispatcher tabs; Technician = own tickets only.
    public static TheoryData<UserRole, string, bool> RoleMatrix => new()
    {
        // Tab 1 Health Dashboard, tab 2 Fleet Health, tab 3 Anomalies/Work orders,
        // tab 4 Reports/Maintenance log, tab 5 User management.
        { UserRole.FlightDispatcher, "/api/v1/dashboards/dispatcher", true },
        { UserRole.FlightDispatcher, "/api/v1/aircraft", true },
        { UserRole.FlightDispatcher, "/api/v1/anomalies", true },
        { UserRole.FlightDispatcher, "/api/v1/work-orders", true },
        { UserRole.FlightDispatcher, "/api/v1/maintenance-logs", true },
        { UserRole.FlightDispatcher, "/api/v1/technicians", true },
        { UserRole.FlightDispatcher, "/api/v1/admin/users", false },
        { UserRole.FlightDispatcher, "/api/v1/admin/roles", false },
        { UserRole.FlightDispatcher, "/api/v1/reports/fleet-health", true },
        { UserRole.FlightDispatcher, "/api/v1/reports/maintenance-history", true },
        { UserRole.FlightDispatcher, "/api/v1/dashboards/technician", false },
        // Screens outside the five approved tabs: revoked from the Dispatcher.
        { UserRole.FlightDispatcher, "/api/v1/dashboards/leadership", false },
        { UserRole.FlightDispatcher, "/api/v1/dashboards/data-engineer", false },
        { UserRole.FlightDispatcher, "/api/v1/predictions", false },
        { UserRole.FlightDispatcher, "/api/v1/predictions/metadata", false },
        { UserRole.FlightDispatcher, "/api/v1/pipelines/runs", false },
        { UserRole.FlightDispatcher, "/api/v1/models", false },
        { UserRole.FlightDispatcher, "/api/v1/development", false },
        { UserRole.FlightDispatcher, "/api/v1/telemetry/imports", false },
        { UserRole.FlightDispatcher, "/api/v1/telemetry/data-quality", false },
        { UserRole.FlightDispatcher, "/api/v1/audit/actions", false },
        { UserRole.FlightDispatcher, "/api/v1/audit/auth", false },
        { UserRole.FlightDispatcher, "/api/v1/reports/data-quality", false },
        { UserRole.FlightDispatcher, "/api/v1/reports/model-performance", false },
        { UserRole.MaintenanceTechnician, "/api/v1/dashboards/technician", true },
        { UserRole.MaintenanceTechnician, "/api/v1/work-orders", true },
        { UserRole.MaintenanceTechnician, "/api/v1/auth/me", true },
        { UserRole.MaintenanceTechnician, "/api/v1/dashboards/dispatcher", false },
        { UserRole.MaintenanceTechnician, "/api/v1/aircraft", false },
        { UserRole.MaintenanceTechnician, "/api/v1/anomalies", false },
        { UserRole.MaintenanceTechnician, "/api/v1/predictions", false },
        { UserRole.MaintenanceTechnician, "/api/v1/telemetry/imports", false },
        { UserRole.MaintenanceTechnician, "/api/v1/maintenance-logs", false },
        { UserRole.MaintenanceTechnician, "/api/v1/technicians", false },
        { UserRole.MaintenanceTechnician, "/api/v1/reports/fleet-health", false },
        { UserRole.MaintenanceTechnician, "/api/v1/admin/users", false },
        { UserRole.MaintenanceTechnician, "/api/v1/development", false },
        { UserRole.MaintenanceTechnician, "/api/v1/audit/actions", false },
        // Data Engineer: Data and Model areas only.
        { UserRole.DataEngineer, "/api/v1/dashboards/data-engineer", true },
        { UserRole.DataEngineer, "/api/v1/telemetry/imports", true },
        { UserRole.DataEngineer, "/api/v1/telemetry/data-quality", true },
        { UserRole.DataEngineer, "/api/v1/pipelines/runs", true },
        { UserRole.DataEngineer, "/api/v1/models", true },
        { UserRole.DataEngineer, "/api/v1/development", true },
        { UserRole.DataEngineer, "/api/v1/predictions", true },
        { UserRole.DataEngineer, "/api/v1/predictions/metadata", true },
        { UserRole.DataEngineer, "/api/v1/anomalies", true },
        { UserRole.DataEngineer, "/api/v1/aircraft", true },
        { UserRole.DataEngineer, "/api/v1/reports/data-quality", true },
        { UserRole.DataEngineer, "/api/v1/reports/model-performance", true },
        { UserRole.DataEngineer, "/api/v1/dashboards/dispatcher", false },
        { UserRole.DataEngineer, "/api/v1/dashboards/technician", false },
        { UserRole.DataEngineer, "/api/v1/work-orders", false },
        { UserRole.DataEngineer, "/api/v1/maintenance-logs", false },
        { UserRole.DataEngineer, "/api/v1/technicians", false },
        { UserRole.DataEngineer, "/api/v1/admin/users", true },
        { UserRole.DataEngineer, "/api/v1/admin/roles", true },
        { UserRole.DataEngineer, "/api/v1/audit/actions", false },
        { UserRole.DataEngineer, "/api/v1/reports/fleet-health", false },
        { UserRole.MaintenanceTechnician, "/api/v1/telemetry/imports", false },
    };

    [Theory]
    [MemberData(nameof(RoleMatrix))]
    public async Task Role_permissions_are_enforced_server_side(UserRole role, string url, bool allowed)
    {
        var client = await factory.ClientAsAsync(role);
        var response = await client.GetAsync(url);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Capabilities that fell outside the five approved dispatcher tabs and that no other role has been granted.
    /// They must be unreachable for every role until the business assigns them an owner — not quietly left open.
    /// </summary>
    public static TheoryData<UserRole, string> UnassignedEndpoints()
    {
        var data = new TheoryData<UserRole, string>();
        string[] urls =
        [
            "/api/v1/audit/actions",
            "/api/v1/audit/auth",
            "/api/v1/dashboards/leadership",
        ];
        foreach (var role in new[] { UserRole.FlightDispatcher, UserRole.MaintenanceTechnician, UserRole.DataEngineer })
            foreach (var url in urls)
                data.Add(role, url);
        return data;
    }

    [Theory]
    [MemberData(nameof(UnassignedEndpoints))]
    public async Task Capabilities_without_an_assigned_role_are_refused_for_everyone(UserRole role, string url)
    {
        var client = await factory.ClientAsAsync(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData(UserRole.FlightDispatcher)]
    [InlineData(UserRole.DataEngineer)]
    public async Task Prediction_review_still_has_no_role_holder(UserRole role)
    {
        var client = await factory.ClientAsAsync(role);
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync($"/api/v1/predictions/{id}/review", new { approvalStatus = "Approved", notes = "n" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync($"/api/v1/predictions/{id}/outcome", new { actualOutcome = "UnscheduledMaintenance" })).StatusCode);
    }

    /// <summary>
    /// Model approval is the Data Engineer's (project-owner decision, 2026-10-23). It is deliberately narrow:
    /// no other role holds it, and holding it does not grant any dispatcher capability.
    /// </summary>
    [Theory]
    [InlineData(UserRole.DataEngineer, true)]
    [InlineData(UserRole.FlightDispatcher, false)]
    [InlineData(UserRole.MaintenanceTechnician, false)]
    public async Task Only_the_data_engineer_may_approve_or_reject_a_model(UserRole role, bool permitted)
    {
        var client = await factory.ClientAsAsync(role);
        var id = Guid.NewGuid();

        var approve = await client.PostAsJsonAsync($"/api/v1/models/{id}/approve", new { notes = "reviewed" });
        var reject = await client.PostAsJsonAsync($"/api/v1/models/{id}/reject", new { notes = "reviewed" });

        if (permitted)
        {
            // Authorised: the request gets past the policy and fails on the unknown model id, not on the role.
            Assert.NotEqual(HttpStatusCode.Forbidden, approve.StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, reject.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, reject.StatusCode);
        }
    }

    [Fact]
    public async Task Model_approval_rejects_an_unauthenticated_request()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/v1/models/{Guid.NewGuid()}/approve", new { notes = "n" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Approving must not also grant activation, nor let an unapproved candidate become Active.</summary>
    [Fact]
    public async Task An_unapproved_candidate_cannot_be_activated()
    {
        var client = await factory.ClientAsAsync(UserRole.DataEngineer);
        var model = await factory.CreateModelVersionAsync(ModelType.AnomalyDetection, ModelStatus.Registered);

        var response = await client.PostAsync($"/api/v1/models/{model.Id}/activate", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_transition", problem.GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFP.Infrastructure.Persistence.AppDbContext>();
        Assert.Equal(ModelStatus.Registered, db.ModelVersions.Single(m => m.Id == model.Id).Status);
    }

    [Fact]
    public async Task Dispatcher_cannot_create_or_edit_aircraft_master_data()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var body = new { registrationNumber = "N999ZZ", model = "A320", manufacturer = "Airbus", fleetGroup = "Narrow" };

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/aircraft", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"/api/v1/aircraft/{Guid.NewGuid()}", body)).StatusCode);
    }

    [Fact]
    public async Task Technician_sees_only_own_tickets_with_ai_diagnosis_and_cannot_plan()
    {
        var dispatcher = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var (tech, techUser) = await factory.ClientAndUserAsync(UserRole.MaintenanceTechnician);
        var otherTech = await factory.CreateUserAsync(UserRole.MaintenanceTechnician);
        var aircraft = await factory.CreateAircraftAsync("IT-RBAC1");

        async Task<string> Create(long? technicianId)
        {
            var r = await dispatcher.PostAsJsonAsync("/api/v1/work-orders", new
            {
                aircraftId = aircraft.Id, priority = "High", title = "Inspect", description = "Pressure trend", assignedTechnicianId = technicianId,
            });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }
        var mine = await Create(techUser.Id);
        var theirs = await Create(otherTech.Id);

        var list = await tech.GetFromJsonAsync<JsonElement>("/api/v1/work-orders");
        Assert.Equal(mine, list.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await tech.GetAsync($"/api/v1/work-orders/{theirs}")).StatusCode);

        var detail = await tech.GetFromJsonAsync<JsonElement>($"/api/v1/work-orders/{mine}");
        Assert.Equal("IT-RBAC1", detail.GetProperty("aircraft").GetProperty("registrationNumber").GetString());
        Assert.True(detail.TryGetProperty("anomaly", out _));
        Assert.True(detail.TryGetProperty("prediction", out _));
        Assert.Equal(["start-inspection"], detail.GetProperty("allowedTransitions").EnumerateArray().Select(e => e.GetString()));

        Assert.Equal(HttpStatusCode.OK, (await tech.PostAsJsonAsync($"/api/v1/work-orders/{mine}/start-inspection", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsJsonAsync($"/api/v1/work-orders/{mine}/cancel", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsJsonAsync("/api/v1/work-orders", new { aircraftId = aircraft.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsJsonAsync($"/api/v1/work-orders/{mine}/inspection", new { inspectionFindings = "x" })).StatusCode);

        var dashboard = await tech.GetFromJsonAsync<JsonElement>("/api/v1/dashboards/technician");
        Assert.Equal(1, dashboard.GetProperty("assignedWorkOrders").GetArrayLength());
        Assert.Equal(1, dashboard.GetProperty("counts").GetProperty("inInspection").GetInt32());
    }

    [Fact]
    public async Task Aircraft_master_data_is_the_data_engineers_and_status_stays_the_dispatchers()
    {
        var dispatcher = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var dataEngineer = await factory.ClientAsAsync(UserRole.DataEngineer);
        var tech = await factory.ClientAsAsync(UserRole.MaintenanceTechnician);
        var body = new { registrationNumber = $"T{Guid.NewGuid():N}"[..10], model = "737-8", manufacturer = "Boeing", fleetGroup = "West" };
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsJsonAsync("/api/v1/aircraft", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsJsonAsync("/api/v1/aircraft", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await dataEngineer.PostAsJsonAsync("/api/v1/aircraft", body)).StatusCode);

        var aircraft = await factory.CreateAircraftAsync("IT-STAT1");
        var status = new { status = "Grounded", reason = "Inspection" };
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsJsonAsync($"/api/v1/aircraft/{aircraft.Id}/status", status)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await dispatcher.PostAsJsonAsync($"/api/v1/aircraft/{aircraft.Id}/status", status)).StatusCode);
    }

    /// <summary>
    /// The fleet-health figures must stay traceable to their source readings (BQA-04), so the dispatcher keeps
    /// read access to telemetry even though the telemetry explorer is not one of the five tabs.
    /// </summary>
    [Fact]
    public async Task Dispatcher_can_still_read_the_source_readings_behind_a_fleet_health_figure()
    {
        var dispatcher = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var aircraft = await factory.CreateAircraftAsync("IT-TRACE1");

        Assert.Equal(HttpStatusCode.OK, (await dispatcher.GetAsync($"/api/v1/telemetry/series?aircraftId={aircraft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await dispatcher.GetAsync($"/api/v1/telemetry?aircraftId={aircraft.Id}")).StatusCode);
    }

    [Fact]
    public async Task Roles_come_from_the_seeded_role_table()
    {
        // User administration moved to the Data Engineer (2026-10-26); the variable keeps its name to limit churn.
        var dispatcher = await factory.ClientAsAsync(UserRole.DataEngineer);
        var roles = await dispatcher.GetFromJsonAsync<JsonElement>("/api/v1/admin/roles");
        Assert.Equal([(1, "FlightDispatcher"), (2, "MaintenanceTechnician"), (3, "DataEngineer")],
            roles.EnumerateArray().Select(r => (r.GetProperty("roleId").GetInt32(), r.GetProperty("roleName").GetString()!)));
    }
}

public class AuthFlowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static object Register(string username, string email) =>
        new { username, email, displayName = "New Person", password = ApiFactory.Password, confirmPassword = ApiFactory.Password };

    [Fact]
    public async Task Registration_requires_dispatcher_approval_and_cannot_self_assign_role()
    {
        var client = factory.CreateClient();
        var username = $"newbie.{Guid.NewGuid():N}"[..20];
        // Extra role properties are ignored: the contract has no such field.
        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username, email = $"{username}@example.test", displayName = "New Person", password = ApiFactory.Password,
            confirmPassword = ApiFactory.Password, roles = new[] { "FlightDispatcher" }, role = "FlightDispatcher",
        });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);

        var pending = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.Forbidden, pending.StatusCode);
        Assert.Equal("account_pending", (await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // User administration moved to the Data Engineer (2026-10-26); the variable keeps its name to limit churn.
        var dispatcher = await factory.ClientAsAsync(UserRole.DataEngineer);
        var users = await dispatcher.GetFromJsonAsync<JsonElement>($"/api/v1/admin/users?status=Pending&search={username}");
        var user = users.GetProperty("items")[0];
        Assert.Equal(0, user.GetProperty("roles").GetArrayLength());
        var id = user.GetProperty("id").GetInt64();

        var approve = await dispatcher.PostAsJsonAsync($"/api/v1/admin/users/{id}/approve", new { roles = new[] { "MaintenanceTechnician" } });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["MaintenanceTechnician"], body.GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refreshToken").GetString()));
    }

    [Fact]
    public async Task Refresh_issues_new_pair_and_rejects_reuse()
    {
        var user = await factory.CreateUserAsync(UserRole.MaintenanceTechnician);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = user.Username, password = ApiFactory.Password }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = login.GetProperty("refreshToken").GetString();

        var refreshed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var pair = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        var reuse = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal("invalid_refresh_token", (await reuse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Duplicate_registration_returns_same_response()
    {
        var existing = await factory.CreateUserAsync(UserRole.FlightDispatcher);
        var client = factory.CreateClient();
        var first = await client.PostAsJsonAsync("/api/v1/auth/register", Register(existing.Username, $"x{Guid.NewGuid():N}@example.test"));
        var second = await client.PostAsJsonAsync("/api/v1/auth/register", Register($"fresh.{Guid.NewGuid():N}"[..20], $"y{Guid.NewGuid():N}@example.test"));
        Assert.Equal(second.StatusCode, first.StatusCode);
        Assert.Equal(await second.Content.ReadAsStringAsync(), await first.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Logout_revokes_token_server_side()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_revokes_their_existing_token()
    {
        var (client, user) = await factory.ClientAndUserAsync(UserRole.MaintenanceTechnician);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/dashboards/technician")).StatusCode);

        // User administration moved to the Data Engineer (2026-10-26); the variable keeps its name to limit churn.
        var dispatcher = await factory.ClientAsAsync(UserRole.DataEngineer);
        Assert.Equal(HttpStatusCode.OK, (await dispatcher.PostAsJsonAsync($"/api/v1/admin/users/{user.Id}/disable", new { reason = "Test" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/dashboards/technician")).StatusCode);
    }

    [Fact]
    public async Task Forgot_and_reset_password_flow()
    {
        var user = await factory.CreateUserAsync(UserRole.MaintenanceTechnician);
        var client = factory.CreateClient();
        var known = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = user.Email });
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = "nobody@example.test" });
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());

        var mail = Directory.GetFiles(factory.MailDirectory).Select(File.ReadAllText).Single(m => m.Contains(user.Email));
        var token = Uri.UnescapeDataString(mail.Split("token=")[1].Split('\n')[0].Trim());
        var reset = await client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = "Brand-New-Pass-42$", confirmPassword = "Brand-New-Pass-42$" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var reuse = await client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = "Brand-New-Pass-43$", confirmPassword = "Brand-New-Pass-43$" });
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = user.Username, password = "Brand-New-Pass-42$" })).StatusCode);
    }

    [Fact]
    public async Task Lockout_returns_423()
    {
        var user = await factory.CreateUserAsync(UserRole.FlightDispatcher);
        var client = factory.CreateClient();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 5; i++) last = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = user.Username, password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Locked, last.StatusCode);
        Assert.Equal("account_locked", (await last.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}

public class ErrorHandlingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Validation_errors_use_problem_details_with_field_errors()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register",
            new { email = "bad", displayName = "", password = "weak", confirmPassword = "other" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("password", out _));
        Assert.True(errors.TryGetProperty("confirmPassword", out _));
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Malformed_enum_is_a_400_not_a_500()
    {
        var tech = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var aircraft = await factory.CreateAircraftAsync("IT-ENUM1");
        var response = await tech.PostAsJsonAsync($"/api/v1/aircraft/{aircraft.Id}/status", new { status = "Teleported", reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_resource_is_404_problem()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var response = await client.GetAsync($"/api/v1/aircraft/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Business_rule_violation_is_400_with_code()
    {
        var tech = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var aircraft = await factory.CreateAircraftAsync("IT-WO1");
        var created = await tech.PostAsJsonAsync("/api/v1/work-orders", new
        {
            aircraftId = aircraft.Id, priority = "Low", title = "Check", description = "Routine",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var close = await tech.PostAsJsonAsync($"/api/v1/work-orders/{id}/close", new { });
        Assert.Equal(HttpStatusCode.BadRequest, close.StatusCode);
        Assert.Equal("invalid_transition", (await close.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Health_and_swagger_are_available()
    {
        var client = factory.CreateClient();
        var health = await client.GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("Healthy", health.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }

    [Fact]
    public async Task Pipeline_job_is_queued_with_status_url_and_duplicates_conflict()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var response = await de.PostAsJsonAsync("/api/v1/pipelines/jobs", new { jobType = "DatasetAudit" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Queued", accepted.GetProperty("status").GetString());
        var run = await de.GetFromJsonAsync<JsonElement>(accepted.GetProperty("statusUrl").GetString());
        Assert.Equal("DatasetAudit", run.GetProperty("jobType").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await de.PostAsJsonAsync("/api/v1/pipelines/jobs", new { jobType = "DatasetAudit" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await de.PostAsJsonAsync("/api/v1/pipelines/jobs", new { jobType = "Score", parameters = new { horizon_days = 7 } })).StatusCode);
    }

    [Fact]
    public async Task Prediction_metadata_reports_blocked_status()
    {
        var client = await factory.ClientAsAsync(UserRole.DataEngineer);
        var meta = await client.GetFromJsonAsync<JsonElement>("/api/v1/predictions/metadata");
        Assert.Equal("Blocked", meta.GetProperty("failurePredictionStatus").GetProperty("status").GetString());
        Assert.Equal(30, meta.GetProperty("configuredHorizonDays").GetInt32());
        Assert.Equal(6, meta.GetProperty("riskCategories").GetArrayLength());
    }

    [Fact]
    public async Task Csv_report_export_is_downloadable()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var response = await client.GetAsync("/api/v1/reports/fleet-health?format=csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("registration_number", await response.Content.ReadAsStringAsync());
    }
}

public class TelemetryImportApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Csv_import_runs_in_background_and_reports_summary()
    {
        await factory.CreateAircraftAsync("IT-TEL1");
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var csv = "aircraft_registration,sensor_id,timestamp_utc,temperature,temperature_unit,pressure,pressure_unit\n" +
                  "IT-TEL1,S1,2026-08-01T00:00:00Z,20.5,C,101.3,kPa\n" +
                  "IT-TEL1,S1,2026-08-01T00:10:00Z,20.7,C,101.2,kPa\n" +
                  "IT-TEL1,S1,2026-08-01T00:10:00Z,20.7,C,101.2,kPa\n" +
                  "IT-TEL1,S1,2026-08-01T00:20:00Z,-400,C,101.2,kPa\n";
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "it.csv" } };
        var upload = await de.PostAsync("/api/v1/telemetry/imports", form);
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
        var statusUrl = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("statusUrl").GetString()!;

        JsonElement import = default;
        for (var i = 0; i < 50; i++)
        {
            import = await de.GetFromJsonAsync<JsonElement>(statusUrl);
            if (import.GetProperty("status").GetString() is "Completed" or "Failed") break;
            await Task.Delay(100);
        }
        Assert.Equal("Completed", import.GetProperty("status").GetString());
        Assert.Equal(4, import.GetProperty("totalRows").GetInt32());
        Assert.Equal(2, import.GetProperty("acceptedRows").GetInt32());
        Assert.Equal(2, import.GetProperty("rejectedRows").GetInt32());
        Assert.Equal(1, import.GetProperty("duplicateRows").GetInt32());

        // Same bytes again â†’ 409; missing columns â†’ 400.
        using var again = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "it2.csv" } };
        Assert.Equal(HttpStatusCode.Conflict, (await de.PostAsync("/api/v1/telemetry/imports", again)).StatusCode);
        using var bad = new MultipartFormDataContent { { new ByteArrayContent("a,b\n1,2\n"u8.ToArray()), "file", "bad.csv" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await de.PostAsync("/api/v1/telemetry/imports", bad)).StatusCode);

        var tech = await factory.ClientAsAsync(UserRole.MaintenanceTechnician);
        using var forbidden = new MultipartFormDataContent { { new ByteArrayContent("x"u8.ToArray()), "file", "f.csv" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsync("/api/v1/telemetry/imports", forbidden)).StatusCode);
    }
}

public class RateLimitTests(LowRateLimitApiFactory factory) : IClassFixture<LowRateLimitApiFactory>
{
    [Fact]
    public async Task Auth_endpoints_are_rate_limited()
    {
        var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = "a@example.test" })).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(HttpStatusCode.Accepted, statuses[0]);
    }
}

using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Application.Services;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Infrastructure.Configuration;
using AFP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AFP.UnitTests;

/// <summary>
/// The development (synthetic) lab runs on its OWN database. These tests give it a separate in-memory database
/// from the operational <see cref="TestContext"/> and prove what may and may not cross between the two.
/// </summary>
public sealed class DevelopmentLabTests : IDisposable
{
    private readonly TestContext _t = new();
    private readonly string _devDbName = "dev-" + Guid.NewGuid();
    private readonly DevelopmentDataSource _dev;

    public DevelopmentLabTests()
    {
        _dev = new DevelopmentDataSource(null, b => b.UseInMemoryDatabase(_devDbName));
    }

    public void Dispose()
    {
        _dev.Dispose();
        _t.Dispose();
    }

    private AppDbContext DevDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_devDbName).Options);

    private DevelopmentLabService Service() => new(_dev, _t.Auditor, _t.UnitOfWork, _t.CurrentUser, _t.Clock);

    private async Task<ModelVersion> AddDevModelAsync(ModelType type)
    {
        var m = new ModelVersion
        {
            ModelName = "afp-failure-prediction", Version = "20261027060650", ModelType = type, Algorithm = "RandomForest",
            Status = ModelStatus.Trained, ArtifactPath = "x", ArtifactSha256 = new string('a', 64), Metrics = "{\"test\":{}}",
            CreatedAtUtc = TestContext.Now,
        };
        await using var db = DevDb();
        db.ModelVersions.Add(m);
        await db.SaveChangesAsync();
        return m;
    }

    [Fact]
    public async Task Queued_development_job_lands_in_the_development_database_only_and_is_audited_operationally()
    {
        var engineer = await _t.AddUserAsync(UserRole.DataEngineer);
        _t.CurrentUser.SignInAs(engineer);

        var accepted = await Service().QueueJobAsync(new DevelopmentJobRequest(PipelineJobType.TrainFailure, null), default);

        await using var dev = DevDb();
        var run = await dev.PipelineRuns.SingleAsync();
        Assert.Equal(accepted.Id, run.Id);
        Assert.Equal(PipelineRunStatus.Queued, run.Status);
        Assert.Null(run.RequestedByUserId);                                   // the dev DB has no users
        Assert.Contains($"\"requested_by_user_id\":{engineer.Id}", run.Parameters);
        Assert.Contains("\"target\":\"failure\"", run.Parameters);
        Assert.Empty(_t.Db.PipelineRuns);                                     // nothing in the operational DB ...
        var audit = await _t.Db.ActionAudits.SingleAsync();                   // ... except who queued what
        Assert.Equal("DevelopmentJobQueued", audit.Action);
        Assert.Equal(engineer.Id, audit.UserId);
        Assert.Equal($"/api/v1/development/runs/{run.Id}", accepted.StatusUrl);
    }

    [Theory]
    [InlineData(PipelineJobType.Score)]          // operational scoring: only the Active, approved model
    [InlineData(PipelineJobType.TrainAnomaly)]
    [InlineData(PipelineJobType.RegisterModel)]  // nothing can be promoted from the development lab
    [InlineData(PipelineJobType.TelemetryImport)]
    public async Task Only_development_training_audit_and_candidate_scoring_can_be_queued(PipelineJobType jobType)
    {
        _t.CurrentUser.SignInAs(await _t.AddUserAsync(UserRole.DataEngineer));
        await Assert.ThrowsAsync<ValidationException>(() =>
            Service().QueueJobAsync(new DevelopmentJobRequest(jobType, null), default));
        await using var dev = DevDb();
        Assert.Empty(dev.PipelineRuns);
    }

    [Fact]
    public async Task Candidate_scoring_needs_an_existing_failure_candidate()
    {
        _t.CurrentUser.SignInAs(await _t.AddUserAsync(UserRole.DataEngineer));
        var svc = Service();
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.QueueJobAsync(new DevelopmentJobRequest(PipelineJobType.ScoreCandidate, null), default));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.QueueJobAsync(new DevelopmentJobRequest(PipelineJobType.ScoreCandidate, Guid.NewGuid()), default));
        var anomaly = await AddDevModelAsync(ModelType.AnomalyDetection);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.QueueJobAsync(new DevelopmentJobRequest(PipelineJobType.ScoreCandidate, anomaly.Id), default));
        Assert.Equal("not_a_failure_model", ex.Code);

        var candidate = await AddDevModelAsync(ModelType.FailurePrediction);
        await svc.QueueJobAsync(new DevelopmentJobRequest(PipelineJobType.ScoreCandidate, candidate.Id), default);
        await using var dev = DevDb();
        Assert.Contains($"\"model_version_id\":\"{candidate.Id}\"", (await dev.PipelineRuns.SingleAsync()).Parameters);
    }

    [Fact]
    public async Task A_second_pending_job_of_the_same_type_is_refused()
    {
        _t.CurrentUser.SignInAs(await _t.AddUserAsync(UserRole.DataEngineer));
        var svc = Service();
        await svc.QueueJobAsync(new DevelopmentJobRequest(PipelineJobType.DatasetAudit, null), default);
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            svc.QueueJobAsync(new DevelopmentJobRequest(PipelineJobType.DatasetAudit, null), default));
        Assert.Equal("job_already_pending", ex.Code);
    }

    [Fact]
    public async Task Development_models_are_read_from_the_development_database_not_the_operational_registry()
    {
        var candidate = await AddDevModelAsync(ModelType.FailurePrediction);
        var models = await Service().ListModelsAsync(default);
        Assert.Equal(candidate.Id, Assert.Single(models).Id);
        Assert.Empty(_t.Db.ModelVersions);
        var evaluation = await Service().GetEvaluationAsync(candidate.Id, default);
        Assert.True(evaluation.EvaluationAvailable);
    }

    [Fact]
    public async Task An_unconfigured_lab_reports_it_and_refuses_every_operation()
    {
        var svc = new DevelopmentLabService(new DevelopmentDataSource(null), _t.Auditor, _t.UnitOfWork, _t.CurrentUser, _t.Clock);
        Assert.False(svc.GetStatus().Configured);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ListModelsAsync(default));
        Assert.Equal("development_not_configured", ex.Code);
    }

    [Theory]
    [InlineData("aircraft_predict_db")]   // the operational database
    [InlineData("AIRCRAFT_PREDICT_DB")]
    [InlineData("aircraft_app")]          // always forbidden
    public void Development_connection_refuses_operational_and_protected_databases(string devName)
    {
        var env = new Dictionary<string, string?>
        {
            ["DB_NAME"] = "aircraft_predict_db", ["DEV_DB_NAME"] = devName, ["DB_APP_USER"] = "api", ["API_USER_PASSWORD"] = "pw",
        };
        Assert.Throws<InvalidOperationException>(() => DatabaseTarget.DevelopmentConnectionString(k => env.GetValueOrDefault(k)));
    }

    [Fact]
    public void Development_connection_is_absent_until_configured_and_targets_only_the_named_database()
    {
        var env = new Dictionary<string, string?> { ["DB_NAME"] = "aircraft_predict_db", ["DB_APP_USER"] = "api", ["API_USER_PASSWORD"] = "pw" };
        Assert.Null(DatabaseTarget.DevelopmentConnectionString(k => env.GetValueOrDefault(k)));
        env["DEV_DB_NAME"] = "aircraft_predict_db_synth";
        var cs = DatabaseTarget.DevelopmentConnectionString(k => env.GetValueOrDefault(k))!;
        Assert.Equal("aircraft_predict_db_synth", new Npgsql.NpgsqlConnectionStringBuilder(cs).Database);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AFP.Domain.Enums;

namespace AFP.IntegrationTests;

public class DataEngineerApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Data_engineer_cannot_review_predictions_or_change_aircraft_status()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var aircraft = await factory.CreateAircraftAsync("IT-DE1");
        // Model approval became the Data Engineer's on 2026-10-23 (project-owner decision), so it is no longer
        // denied here; `Only_the_data_engineer_may_approve_or_reject_a_model` covers it. Everything else stays denied.
        Assert.Equal(HttpStatusCode.Forbidden, (await de.PostAsJsonAsync($"/api/v1/predictions/{Guid.NewGuid()}/review", new { approvalStatus = "Approved", notes = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await de.PostAsJsonAsync("/api/v1/anomalies/1/acknowledge", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await de.PostAsJsonAsync($"/api/v1/aircraft/{aircraft.Id}/status", new { status = "Available", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await de.GetAsync($"/api/v1/aircraft/{aircraft.Id}")).StatusCode);
        // Registration/activation is a Data Engineer task (404 = authorized; that model does not exist).
        Assert.Equal(HttpStatusCode.NotFound, (await de.PostAsJsonAsync($"/api/v1/models/{Guid.NewGuid()}/register", new { })).StatusCode);
    }

    [Fact]
    public async Task Raw_json_upload_goes_to_bronze_and_stays_queued_until_the_etl_reports()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var json = "[{\"telemetry_id\":\"IT-" + Guid.NewGuid().ToString("N") + "\",\"tail_number\":\"N120UA\",\"timestamp_epoch\":1788220800," +
                   "\"metrics\":{\"engine_temp_c\":470.1,\"oil_pressure_psi\":24.9,\"vibration_index\":0.6},\"status_flag\":\"NORMAL\"}]";
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(json)), "file", "raw.json" } };
        var upload = await de.PostAsync("/api/v1/telemetry/raw-uploads", form);
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
        var accepted = await upload.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Queued", accepted.GetProperty("status").GetString());

        var import = await de.GetFromJsonAsync<JsonElement>(accepted.GetProperty("statusUrl").GetString());
        Assert.Equal("RawJson", import.GetProperty("source").GetString());
        Assert.Equal("Queued", import.GetProperty("status").GetString());   // never reported complete on upload
        Assert.Contains(factory.RawStore.Objects.Keys, k => k.StartsWith("s3://bronze/telemetry/") && k.EndsWith(".json"));

        using var bad = new MultipartFormDataContent { { new ByteArrayContent("{\"not\":\"an array\"}"u8.ToArray()), "file", "bad.json" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await de.PostAsync("/api/v1/telemetry/raw-uploads", bad)).StatusCode);

        var tech = await factory.ClientAsAsync(UserRole.MaintenanceTechnician);
        using var forbidden = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(json)), "file", "x.json" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsync("/api/v1/telemetry/raw-uploads", forbidden)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync("/api/v1/telemetry/raw-uploads", new StringContent(""))).StatusCode);
    }

    /// <summary>
    /// The Data Engineer's ingestion screen must show where the payload landed and how big it was,
    /// so an upload can be traced to its Bronze object.
    /// </summary>
    [Fact]
    public async Task Import_reports_its_bronze_object_key_and_file_size()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var json = "[{\"telemetry_id\":\"IT-" + Guid.NewGuid().ToString("N") + "\",\"tail_number\":\"N120UA\",\"timestamp_epoch\":1788220800," +
                   "\"metrics\":{\"engine_temp_c\":470.1,\"oil_pressure_psi\":24.9,\"vibration_index\":0.6},\"status_flag\":\"NORMAL\"}]";
        var bytes = Encoding.UTF8.GetBytes(json);

        using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", "lineage.json" } };
        var upload = await de.PostAsync("/api/v1/telemetry/raw-uploads", form);
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
        var accepted = await upload.Content.ReadFromJsonAsync<JsonElement>();

        var import = await de.GetFromJsonAsync<JsonElement>(accepted.GetProperty("statusUrl").GetString());

        var storedPath = import.GetProperty("storedPath").GetString();
        Assert.False(string.IsNullOrWhiteSpace(storedPath));
        Assert.Contains("bronze/telemetry/", storedPath);
        Assert.Equal(bytes.Length, import.GetProperty("fileSizeBytes").GetInt64());
        Assert.Equal("lineage.json", import.GetProperty("fileName").GetString());
    }
}

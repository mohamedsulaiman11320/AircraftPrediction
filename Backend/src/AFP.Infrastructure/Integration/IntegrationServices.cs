using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using AFP.Application.Abstractions;
using AFP.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AFP.Infrastructure.Integration;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\AircraftFailurePrediction\{name} — outside the source tree.</summary>
    public static string DataDirectory(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AircraftFailurePrediction", name);
}

public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>"Pickup" writes messages to a local directory (development); "Smtp" delivers them.</summary>
    public string Mode { get; set; } = "Pickup";
    public string From { get; set; } = "no-reply@afp.local";
    public string? PickupDirectory { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpEnableSsl { get; set; } = true;
    public string? SmtpUsername { get; set; }

    /// <summary>Supply via the Email__SmtpPassword environment variable; never commit it.</summary>
    public string? SmtpPassword { get; set; }
}

/// <summary>Development sender: writes each message to a .txt file so reset links can be opened locally.</summary>
public sealed class PickupDirectoryEmailSender(IOptions<EmailOptions> options, ILogger<PickupDirectoryEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var directory = options.Value.PickupDirectory is { Length: > 0 } dir ? dir : AppPaths.DataDirectory("mail");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.txt");
        var content = $"From: {options.Value.From}\r\nTo: {message.To}\r\nSubject: {message.Subject}\r\nDate: {DateTime.UtcNow:R}\r\n\r\n{message.TextBody}\r\n";
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct);
        logger.LogInformation("Email written to pickup directory: {Path}", path);
    }
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.SmtpHost)) throw new InvalidOperationException("Email:SmtpHost is not configured.");
        using var client = new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = o.SmtpEnableSsl };
        if (!string.IsNullOrEmpty(o.SmtpUsername)) client.Credentials = new NetworkCredential(o.SmtpUsername, o.SmtpPassword);
        using var mail = new MailMessage(o.From, message.To, message.Subject, message.TextBody);
        await client.SendMailAsync(mail, ct);
    }
}

/// <summary>Stores original import files byte-for-byte, marked read-only, in a directory outside source control.</summary>
public sealed partial class LocalImportFileStore(IOptions<TelemetryImportOptions> options) : IImportFileStore
{
    private string Root => Path.GetFullPath(options.Value.StorageDirectory is { Length: > 0 } dir ? dir : AppPaths.DataDirectory("imports"));

    public async Task<string> SaveAsync(Guid importId, string originalFileName, Stream content, CancellationToken ct)
    {
        var safeName = UnsafeChars().Replace(Path.GetFileName(originalFileName), "_");
        if (safeName.Length == 0) safeName = "upload.csv";
        var directory = Path.Combine(Root, DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"), importId.ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, safeName);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await content.CopyToAsync(file, ct);
        }
        File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    public Stream OpenRead(string storedPath) =>
        new FileStream(EnsureInsideRoot(storedPath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    public void Delete(string storedPath)
    {
        var path = EnsureInsideRoot(storedPath);
        // Best effort: on Windows a freshly written file can be briefly locked by antivirus or the indexer.
        for (var attempt = 1; attempt <= 5 && File.Exists(path); attempt++)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100 * attempt);
            }
            catch (IOException)
            {
                // Leave the rejected upload in place rather than failing the request; it is never referenced by an import.
            }
        }
    }

    private string EnsureInsideRoot(string storedPath)
    {
        var full = Path.GetFullPath(storedPath);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Stored import path is outside the import storage directory.");
        return full;
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeChars();
}

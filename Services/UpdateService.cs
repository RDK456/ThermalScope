using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ThermalScope.Services;

/// <summary>What a newer release offers: the smallest package that gets this install to it.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string Notes,
    string PageUrl,
    string PackageName,
    string PackageUrl,
    long PackageSize,
    bool IsDelta,
    string? ChecksumsUrl)
{
    public string SizeText => PackageSize >= 1024 * 1024 ? $"{PackageSize / 1024.0 / 1024.0:0.#} MB" : $"{PackageSize / 1024.0:0} KB";
}

/// <summary>
/// Checks GitHub Releases for a newer version and applies it. When the release carries a delta package built
/// for exactly this version only the changed files are downloaded, otherwise the full package. Downloads are
/// verified against the release's SHA256SUMS.txt, then a PowerShell script shipped in the install folder swaps
/// the files after this process exits (with rollback) and restarts the app.
/// </summary>
public sealed class UpdateService
{
    private const string Repository = "RDK456/ThermalScope";
    private static readonly string StagingRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThermalScope", "update");

    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(CurrentVersion.Build, 0)}";

    // Declared after CurrentVersion: static initialisers run in textual order and CreateClient reads it.
    private static readonly HttpClient Http = CreateClient();

    private static string InstallDirectory => AppContext.BaseDirectory.TrimEnd('\\');
    private static string ApplyScript => Path.Combine(InstallDirectory, "apply-update.ps1");

    /// <summary>True for a real install (manifest + update script present and the folder is writable).</summary>
    public static bool CanSelfUpdate =>
        File.Exists(Path.Combine(InstallDirectory, "manifest.json")) && File.Exists(ApplyScript) && IsWritable(InstallDirectory);

    /// <summary>Returns the newer release, or null when this is the latest (or the check cannot run).</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellation = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false), cancellationToken: cancellation).ConfigureAwait(false);

        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseVersion(tag, out var latest) || latest <= Normalize(CurrentVersion))
        {
            return null;
        }

        var assets = root.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? string.Empty,
                          Url: a.GetProperty("browser_download_url").GetString() ?? string.Empty,
                          Size: a.GetProperty("size").GetInt64()))
            .ToList();

        var current = CurrentVersionText;
        var latestText = $"{latest.Major}.{latest.Minor}.{latest.Build}";
        var delta = assets.FirstOrDefault(a => a.Name.Equals($"ThermalScope-{current}-to-{latestText}.delta.zip", StringComparison.OrdinalIgnoreCase));
        var full = assets.FirstOrDefault(a => a.Name.Equals($"ThermalScope-{latestText}-win-x64.zip", StringComparison.OrdinalIgnoreCase));
        var package = !string.IsNullOrEmpty(delta.Url) ? delta : full;
        if (string.IsNullOrEmpty(package.Url))
        {
            return null; // a release without a package for us
        }

        var sums = assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        return new UpdateInfo(
            latest,
            tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repository}/releases",
            package.Name,
            package.Url,
            package.Size,
            package.Url == delta.Url,
            string.IsNullOrEmpty(sums.Url) ? null : sums.Url);
    }

    /// <summary>Downloads the package and verifies its SHA-256. Returns the path of the verified file.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancellation = default)
    {
        if (update.ChecksumsUrl is null)
        {
            throw new InvalidOperationException("The release has no SHA256SUMS.txt, so the download cannot be verified.");
        }

        Directory.CreateDirectory(StagingRoot);
        var path = Path.Combine(StagingRoot, update.PackageName);
        using (var response = await Http.GetAsync(update.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.PackageSize;
            await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long read = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                read += count;
                if (total > 0)
                {
                    progress?.Report((double)read / total);
                }
            }
        }

        var sums = await Http.GetStringAsync(update.ChecksumsUrl, cancellation).ConfigureAwait(false);
        var expected = sums.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*', ' ').Equals(update.PackageName, StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[0])
            .FirstOrDefault();

        string actual;
        await using (var stream = File.OpenRead(path))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false));
        }

        if (expected is null || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("The downloaded update does not match its published checksum and was discarded.");
        }

        return path;
    }

    /// <summary>Starts the update script and returns; the caller must exit the app straight away.</summary>
    public static bool StartApply(string packagePath)
    {
        try
        {
            var exe = Path.GetFileName(Environment.ProcessPath) ?? "ThermalScope.exe";
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ApplyScript}\" " +
                            $"-ProcessId {Environment.ProcessId} -InstallDir \"{InstallDirectory}\" -Package \"{packagePath}\" -Exe \"{exe}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            return Process.Start(startInfo) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            text = text[..dash]; // pre-release suffix
        }

        if (Version.TryParse(text, out var parsed))
        {
            version = Normalize(parsed);
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ThermalScope/{CurrentVersionText}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace FortniteAFKXPMonitor.Services;

public sealed record UpdateInfo(
    Version Version, string Tag, string ReleaseUrl, string Notes,
    string AssetName, string DownloadUrl, long Size, string? Sha256);

public sealed record UpdateCheck(UpdateInfo? Update, string LatestTag);

/// <summary>
/// Finds the latest GitHub release and installs it over the running app.
///
/// Both platforms replace the binary by renaming, which works while it's running: Windows renames the live exe to
/// "*.old" and puts the new one in its place; Linux renames the new file over the old one (the running process keeps
/// the old inode). The new copy is started with "--wait-for &lt;pid&gt;" so it waits for this one to exit first.
/// Releases are not code-signed, so the trust is "whatever Glitxhhh/FortniteAFKXPMonitor publishes on GitHub over
/// HTTPS"; when GitHub reports a SHA-256 digest for the asset it is checked as well.
/// </summary>
public static class UpdateService
{
    private const string Repo = "Glitxhhh/FortniteAFKXPMonitor";
    private const string BinaryName = "FortniteAFKXPMonitor";

    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    /// <summary>The running version as major.minor.patch (the release workflow builds with -p:Version=X.Y.Z).</summary>
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // A plain client name: no contact details in the User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FortniteAFKXPMonitor", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static Version ReadCurrentVersion()
    {
        Version? v = Assembly.GetEntryAssembly()?.GetName().Version;
        return v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>The release asset for this OS, or null when there isn't one (e.g. a 32-bit or ARM machine).</summary>
    private static string? AssetSuffix()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return null;
        if (OperatingSystem.IsWindows()) return "-win-x64.zip";
        if (OperatingSystem.IsLinux()) return "-linux-x64.tar.gz";
        return null;
    }

    // ---- checking --------------------------------------------------------------------------------

    public static async Task<UpdateCheck> CheckAsync(CancellationToken ct = default, Version? current = null)
    {
        current ??= CurrentVersion;

        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        JsonElement root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest))
            throw new InvalidOperationException($"Couldn't read the release version '{tag}'.");

        if (latest <= current)
            return new UpdateCheck(null, tag);

        string? suffix = AssetSuffix();
        if (suffix is null)
            return new UpdateCheck(null, tag);

        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            string? digest = asset.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null;
            string? sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;

            return new UpdateCheck(new UpdateInfo(
                latest, tag,
                root.TryGetProperty("html_url", out JsonElement url) ? url.GetString() ?? ReleasesPage : ReleasesPage,
                root.TryGetProperty("body", out JsonElement body) ? body.GetString() ?? "" : "",
                name,
                asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.TryGetProperty("size", out JsonElement size) ? size.GetInt64() : 0,
                sha), tag);
        }

        return new UpdateCheck(null, tag);   // a newer release exists but has no build for this system
    }

    // ---- installing ------------------------------------------------------------------------------

    /// <summary>Downloads the release and replaces <paramref name="targetPath"/> (default: the running binary).</summary>
    /// <returns>The path of the installed binary.</returns>
    public static async Task<string> InstallAsync(UpdateInfo info, IProgress<double>? progress = null,
        CancellationToken ct = default, string? targetPath = null)
    {
        string target = targetPath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Can't work out where the app is installed.");

        string work = Directory.CreateTempSubdirectory("afk-update-").FullName;
        try
        {
            string archive = Path.Combine(work, info.AssetName);
            await DownloadAsync(info, archive, progress, ct);

            string extracted = Path.Combine(work, BinaryName + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (info.AssetName.EndsWith(".zip", StringComparison.Ordinal)) ExtractFromZip(archive, extracted);
            else ExtractFromTarGz(archive, extracted);
            VerifyExecutable(extracted);

            // Stage next to the target so the final step is a same-volume rename.
            string staged = target + ".new";
            File.Copy(extracted, staged, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                             | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                             | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            Swap(staged, target);
            return target;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Can't write to {Path.GetDirectoryName(target)} (try running once as administrator, or download the release yourself).", ex);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp files: best effort */ }
        }
    }

    private static async Task DownloadAsync(UpdateInfo info, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The release has no HTTPS download.");

        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Redirects are followed automatically; only accept the file if it ended up on GitHub's own hosts.
        string host = response.RequestMessage?.RequestUri?.Host ?? "";
        if (host != "github.com" && !host.EndsWith(".github.com", StringComparison.Ordinal)
            && !host.EndsWith(".githubusercontent.com", StringComparison.Ordinal))
            throw new InvalidOperationException($"The download was redirected to an unexpected host ({host}).");

        long total = response.Content.Headers.ContentLength ?? info.Size;
        using var sha = SHA256.Create();
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(destination))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                sha.TransformBlock(buffer, 0, read, null, 0);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if (info.Size > 0 && done != info.Size)
                throw new InvalidOperationException($"The download was cut short ({done} of {info.Size} bytes).");
        }

        if (info.Sha256 is { Length: > 0 } expected
            && !string.Equals(Convert.ToHexString(sha.Hash!), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The download doesn't match the checksum GitHub reports for it.");

        progress?.Report(1);
    }

    private static void ExtractFromZip(string archive, string destination)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);
        ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e => e.Name == BinaryName + ".exe")
                                ?? throw new InvalidOperationException("The release archive doesn't contain the app.");
        entry.ExtractToFile(destination, overwrite: true);
    }

    private static void ExtractFromTarGz(string archive, string destination)
    {
        using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile || Path.GetFileName(entry.Name) != BinaryName)
                continue;
            entry.ExtractToFile(destination, overwrite: true);
            return;
        }
        throw new InvalidOperationException("The release archive doesn't contain the app.");
    }

    /// <summary>Refuses to install anything that isn't a plausible executable for this OS.</summary>
    private static void VerifyExecutable(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1_000_000)
            throw new InvalidOperationException("The downloaded app looks too small to be valid.");

        var head = new byte[4];
        using (var f = File.OpenRead(path))
            f.ReadExactly(head);

        bool ok = OperatingSystem.IsWindows()
            ? head[0] == 'M' && head[1] == 'Z'
            : head[0] == 0x7F && head[1] == 'E' && head[2] == 'L' && head[3] == 'F';
        if (!ok)
            throw new InvalidOperationException("The downloaded file isn't an executable for this system.");
    }

    private static void Swap(string staged, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(staged, target, overwrite: true);   // rename(2): atomic, fine while the old one is running
            return;
        }

        // Windows won't let a running exe be overwritten or deleted, but it can be renamed.
        string old = target + ".old";
        if (File.Exists(old))
            File.Delete(old);

        File.Move(target, old);
        try
        {
            File.Move(staged, target);
        }
        catch
        {
            File.Move(old, target);   // put the working copy back
            throw;
        }
    }

    // ---- restarting ------------------------------------------------------------------------------

    /// <summary>Starts the (new) binary; it waits for this process to exit before doing anything.</summary>
    public static void RestartApp(string exePath)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
        };
        psi.ArgumentList.Add("--wait-for");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi)?.Dispose();
    }

    /// <summary>For the "--wait-for &lt;pid&gt;" a freshly installed copy is started with.</summary>
    public static void WaitForExit(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.WaitForExit(15_000);
        }
        catch
        {
            // already gone
        }
    }

    /// <summary>Deletes the leftovers of a previous update ("*.old" on Windows, a stray "*.new").</summary>
    public static void CleanupAfterUpdate()
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        foreach (string leftover in new[] { exe + ".old", exe + ".new" })
        {
            try { if (File.Exists(leftover)) File.Delete(leftover); }
            catch { /* still in use: next launch */ }
        }
    }

    public static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch { /* no browser available */ }
    }
}

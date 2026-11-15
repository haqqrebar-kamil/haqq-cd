using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace HAQQ.CAD.Presentation;

/// <summary>
/// Where this copy of the program looks for newer builds. Written into test releases by
/// <c>tools/installer/make-installer.ps1</c> (embedded resource <c>update-channel.txt</c>: build number on the
/// first line, address of the update manifest on the second). Builds without it never look for updates.
/// </summary>
public sealed record UpdateChannel(long Build, Uri ManifestUrl)
{
    public const string ResourceName = "update-channel.txt";

    public static UpdateChannel? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !long.TryParse(lines[0], out long build) || build <= 0) return null;
        if (!Uri.TryCreate(lines[1], UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) return null;
        return new UpdateChannel(build, url);
    }

    public static UpdateChannel? FromAssembly(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}

/// <summary>The newest build on the server: <c>{"build": 202611150930, "file": "HaqqCad.exe", "sha256": "..."}</c>.</summary>
public sealed record UpdateManifest(long Build, Uri Download, string Sha256)
{
    /// <summary>Reads a manifest. <c>file</c> may be a name beside the manifest or a full https address.</summary>
    public static UpdateManifest? Parse(string json, Uri manifestUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("build", out var b) || !b.TryGetInt64(out long build) || build <= 0) return null;
            if (!root.TryGetProperty("file", out var f) || f.GetString() is not { Length: > 0 } file) return null;
            if (!root.TryGetProperty("sha256", out var h) || h.GetString() is not { Length: 64 } hash) return null;
            if (!hash.All(Uri.IsHexDigit)) return null;
            if (!Uri.TryCreate(manifestUrl, file, out var download) || download.Scheme != Uri.UriSchemeHttps) return null;
            return new UpdateManifest(build, download, hash.ToUpperInvariant());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Checks for, downloads and installs a newer build of the single-file program. Downloads only over https and
/// only installs a file whose SHA-256 matches the manifest. Every failure is reported through
/// <see cref="LastError"/> and leaves the installed program untouched.
/// </summary>
public sealed class Updater
{
    private readonly UpdateChannel _channel;
    private readonly HttpClient _http;

    public Updater(UpdateChannel channel, HttpMessageHandler? handler = null)
    {
        _channel = channel;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(15);
    }

    public string? LastError { get; private set; }

    /// <summary>The manifest of a newer build, or null when this build is current or the server cannot be reached.</summary>
    public async Task<UpdateManifest?> CheckAsync(CancellationToken cancel = default)
    {
        LastError = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            string json = await _http.GetStringAsync(_channel.ManifestUrl, timeout.Token).ConfigureAwait(false);
            var manifest = UpdateManifest.Parse(json, _channel.ManifestUrl);
            if (manifest is null) { LastError = "The update information on the server could not be read."; return null; }
            return manifest.Build > _channel.Build ? manifest : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            LastError = "The update server could not be reached.";
            return null;
        }
    }

    /// <summary>Downloads the new program into <paramref name="folder"/> and checks it. Returns the file path, or null.</summary>
    /// <param name="progress">Receives the part downloaded so far, 0 to 1 (only when the server states the size).</param>
    public async Task<string?> DownloadAsync(UpdateManifest manifest, string folder, CancellationToken cancel = default, IProgress<double>? progress = null)
    {
        LastError = null;
        string target = Path.Combine(folder, $"HaqqCad-{manifest.Build}.exe");
        string partial = target + ".part";
        try
        {
            Directory.CreateDirectory(folder);
            using (var response = await _http.GetAsync(manifest.Download, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var file = File.Create(partial);
                await using var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                var buffer = new byte[1 << 16];
                long done = 0;
                int lastPercent = -1;
                int read;
                while ((read = await body.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    done += read;
                    if (progress is null || total is not > 0) continue;
                    int percent = (int)(done * 100 / total.Value);
                    if (percent != lastPercent) { lastPercent = percent; progress.Report(Math.Min(1.0, done / (double)total.Value)); }
                }
            }

            string actual;
            await using (var check = File.OpenRead(partial))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancel).ConfigureAwait(false));
            if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                LastError = "The downloaded update did not match its checksum and was discarded.";
                return null;
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            LastError = "The update could not be downloaded.";
            return null;
        }
    }

    private static string OldCopy(string programPath) => programPath + ".old";

    /// <summary>
    /// Puts the downloaded program in place of the running one. Windows allows a running program file to be
    /// renamed, so the current file is renamed to <c>.old</c> and the new one takes its name; the new version
    /// is used from the next start. On failure the original is put back.
    /// </summary>
    public static bool Apply(string downloaded, string programPath, out string? error)
    {
        error = null;
        string old = OldCopy(programPath);
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(programPath, old);
            try
            {
                File.Move(downloaded, programPath);
            }
            catch
            {
                File.Move(old, programPath);
                throw;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "The update could not be installed in the program folder: " + ex.Message;
            return false;
        }
    }

    /// <summary>Removes what an earlier update left behind (the previous program file and old downloads).</summary>
    public static void CleanUp(string programPath, string downloadFolder)
    {
        try
        {
            if (File.Exists(OldCopy(programPath))) File.Delete(OldCopy(programPath));
            if (Directory.Exists(downloadFolder))
                foreach (string file in Directory.GetFiles(downloadFolder, "HaqqCad-*")) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

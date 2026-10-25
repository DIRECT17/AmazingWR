using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace AmazingWR.App;

/// <summary>
/// Self-update: download the new EXE, check its SHA-256, then start it with
/// "--apply-update &lt;target&gt; &lt;pid&gt;". That instance waits for the old app to exit, copies
/// itself over the old EXE and launches it. Nothing runs through a shell or script.
/// </summary>
internal static class Updater
{
    private const string ApplyFlag = "--apply-update";
    private static readonly string UpdateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmazingWR", "update");

    public static async Task<string> DownloadAsync(string url, string? expectedSha256, IProgress<double> progress)
    {
        Directory.CreateDirectory(UpdateDir);
        var path = Path.Combine(UpdateDir, "AmazingWR-new.exe");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        using var sha = SHA256.Create();
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var target = File.Create(path))
        {
            var buffer = new byte[128 * 1024];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                sha.TransformBlock(buffer, 0, read, null, 0);
                done += read;
                if (total > 0) progress.Report((double)done / total);
            }
            sha.TransformFinalBlock([], 0, 0);
        }
        var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (!string.IsNullOrEmpty(expectedSha256) && !actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("Downloaded file does not match the published checksum.");
        }
        return path;
    }

    public static void StartApply(string newExe)
    {
        var info = new ProcessStartInfo(newExe) { UseShellExecute = false };
        info.ArgumentList.Add(ApplyFlag);
        info.ArgumentList.Add(Environment.ProcessPath!);
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(info);
    }

    /// <summary>Runs in the freshly downloaded EXE. Returns true when this launch was an update step (the caller must then exit).</summary>
    public static bool TryApply(string[] args)
    {
        if (args.Length != 3 || args[0] != ApplyFlag) return false;
        var target = args[1];
        try
        {
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) return true;
            try { using var old = Process.GetProcessById(int.Parse(args[2])); old.WaitForExit(30_000); }
            catch (ArgumentException) { /* the old app is already gone */ }
            for (var attempt = 0; ; attempt++)
            {
                try { File.Copy(Environment.ProcessPath!, target, overwrite: true); break; }
                catch (IOException) when (attempt < 40) { Thread.Sleep(500); }
            }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("Applying the update failed", ex); }
        return true;
    }

    public static void Cleanup()
    {
        try { if (Directory.Exists(UpdateDir)) foreach (var file in Directory.GetFiles(UpdateDir)) try { File.Delete(file); } catch { /* still in use by the update step */ } }
        catch (Exception ex) { Log.Write("Could not clean the update folder", ex); }
    }
}

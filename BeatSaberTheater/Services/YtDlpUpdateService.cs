using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeatSaberTheater.Util;
using IPA.Utilities;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;
using Zenject;

namespace BeatSaberTheater.Services;

public class YtDlpUpdateService : IInitializable
{
    private readonly LoggingService _loggingService;

    public string YtDlpPath => File.Exists(TheaterYtDlpPath) ? TheaterYtDlpPath : DefaultYtDlpPath;

    private string TheaterLibsPath => Path.Combine(UnityGame.LibraryPath, "Theater");
    private string DefaultYtDlpPath => Path.Combine(UnityGame.LibraryPath, "yt-dlp.exe");
    private string TheaterYtDlpPath => Path.Combine(TheaterLibsPath, "yt-dlp.exe");

    public YtDlpUpdateService(LoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    public async Task<string?> GetCurrentVersion()
    {
        try
        {
            return await Task.Run(() =>
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = YtDlpPath,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return output.Trim();
            });
        }
        catch (Exception ex)
        {
            _loggingService.Error($"Error getting current yt-dlp version: {ex}");
            return null;
        }
    }

    public async Task<string?> GetLatestVersion()
    {
        try
        {
            var request = UnityWebRequest.Get("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest");
            request.SetRequestHeader("User-Agent", "BeatSaberTheater");
            var operation = request.SendWebRequest();
            while (!operation.isDone) await Task.Yield();
            if (request.result != UnityWebRequest.Result.Success)
                throw new Exception(request.error);
            var json = JObject.Parse(request.downloadHandler.text);
            return json["tag_name"]?.ToString();
        }
        catch (Exception ex)
        {
            _loggingService.Error($"Error getting latest yt-dlp version: {ex}");
            return null;
        }
    }

    public async Task<bool> CheckForUpdate()
    {
        var current = await GetCurrentVersion();
        var latest = await GetLatestVersion();
        if (current == null || latest == null) return false;

        try
        {
            var currentVersion = Version.Parse(current);
            var latestVersion = Version.Parse(latest);
            return latestVersion > currentVersion;
        }
        catch
        {
            // If parsing fails, compare strings
            return string.Compare(latest, current, StringComparison.Ordinal) > 0;
        }
    }

    public async Task<bool> DownloadLatest()
    {
        try
        {
            var request = UnityWebRequest.Get("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest");
            request.SetRequestHeader("User-Agent", "BeatSaberTheater");
            var operation = request.SendWebRequest();
            while (!operation.isDone) await Task.Yield();
            if (request.result != UnityWebRequest.Result.Success)
                throw new Exception(request.error);
            var json = JObject.Parse(request.downloadHandler.text);
            var assets = json["assets"] as JArray;
            var exeAsset = assets?.FirstOrDefault(a => a["name"]?.ToString() == "yt-dlp.exe");
            if (exeAsset == null) return false;

            var downloadUrl = exeAsset["browser_download_url"]?.ToString();
            if (downloadUrl == null) return false;

            var dir = Path.GetDirectoryName(TheaterYtDlpPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var downloadRequest = UnityWebRequest.Get(downloadUrl);
            downloadRequest.downloadHandler = new DownloadHandlerFile(TheaterYtDlpPath);
            var downloadOperation = downloadRequest.SendWebRequest();
            while (!downloadOperation.isDone) await Task.Yield();
            if (downloadRequest.result != UnityWebRequest.Result.Success)
                throw new Exception(downloadRequest.error);

            _loggingService.Info("Downloaded latest yt-dlp.exe");

            // Copy ffmpeg.exe to Theater directory if it doesn't exist
            string theaterFfmpegPath = Path.Combine(TheaterLibsPath, "ffmpeg.exe");
            if (!File.Exists(theaterFfmpegPath))
            {
                string baseFfmpegPath = Path.Combine(UnityGame.LibraryPath, "ffmpeg.exe");
                if (File.Exists(baseFfmpegPath))
                {
                    File.Copy(baseFfmpegPath, theaterFfmpegPath);
                    _loggingService.Info("Copied ffmpeg.exe to Theater directory");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _loggingService.Error($"Error downloading yt-dlp: {ex}");
            return false;
        }
    }

    public void Initialize()
    {
        // Start non-blocking update check
        StartUpdateCheck();
    }

    private async void StartUpdateCheck()
    {
        try
        {
            if (await CheckForUpdate())
            {
                await DownloadLatest();
            }
        }
        catch (Exception ex)
        {
            _loggingService.Error($"Error during yt-dlp update check: {ex}");
        }
    }
}
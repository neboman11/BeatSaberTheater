using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
    private readonly PluginConfig _config;
    private readonly LoggingService _loggingService;

    public string YtDlpPath => TheaterYtDlpPath;
    public string DenoDlpPath => Path.Combine(TheaterFileHelpers.TheaterLibsPath, "deno.exe");

    private string TheaterYtDlpPath => Path.Combine(TheaterFileHelpers.TheaterLibsPath, "yt-dlp.exe");

    internal YtDlpUpdateService(PluginConfig config, LoggingService loggingService)
    {
        _config = config;
        _loggingService = loggingService;
    }

    public async Task<string?> GetCurrentVersion()
    {
        try
        {
            if (!File.Exists(YtDlpPath))
            {
                _loggingService.Info("No local yt-dlp.exe was found");
                return null;
            }

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

    private async Task<string?> GetLatestVersion()
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

    private async Task<bool> CheckForUpdate()
    {
        _loggingService.Info("Checking if yt-dlp.exe needs to be updated");
        var current = await GetCurrentVersion();
        var latest = await GetLatestVersion();

        _loggingService.Info($"Resolved yt-dlp versions: Current ({current}), latest ({latest})");
        if (latest == null)
        {
            return false;
        }

        try
        {
            if (current == null)
            {
                // We currently have no yt-dlp binary at all. In this case, we always want to update
                _loggingService.Info("yt-dlp.exe was not found in library path. Scheduling update");
                return true;
            }
            else
            {
                // We already have a version of yt-dlp present in the library folder. In this case, we only want
                // to update if the latest retrieved version is higher than our current one
                var currentVersion = Version.Parse(current);
                var latestVersion = Version.Parse(latest);
                _loggingService.Info($"Newer yt-dlp version available -> {latestVersion > currentVersion}");
                return latestVersion > currentVersion;
            }
        }
        catch
        {
            // If parsing fails, compare strings
            return string.Compare(latest, current, StringComparison.Ordinal) > 0;
        }
    }

    private async Task DownloadLatest()
    {
        try
        {
            _loggingService.Info("Downloading latest yt-dlp.exe");

            var request = UnityWebRequest.Get("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest");
            request.SetRequestHeader("User-Agent", "BeatSaberTheater");
            var operation = request.SendWebRequest();
            while (!operation.isDone) await Task.Yield();
            if (request.result != UnityWebRequest.Result.Success)
            {
                _loggingService.Error($"Could not retrieve latest yt-dlp release from GitHub. Reason: {request.error}");
                throw new Exception(request.error);
            }
            var json = JObject.Parse(request.downloadHandler.text);
            var assets = json["assets"] as JArray;
            var exeAsset = assets?.FirstOrDefault(a => a["name"]?.ToString() == "yt-dlp.exe");
            if (exeAsset == null)
            {
                _loggingService.Error($"Could not find yt-dlp.exe in release file manifest from retrieved latest GitHub version");
                _config.PluginEnabled = false;
                return;
            }

            var downloadUrl = exeAsset["browser_download_url"]?.ToString();
            if (downloadUrl == null)
            {
                _loggingService.Error($"Could not find download URL for latest yt-dlp.exe version from GitHub");
                _config.PluginEnabled = false;
                return;
            }

            var dir = Path.GetDirectoryName(TheaterYtDlpPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            _loggingService.Info($"Downloading yt-dlp from URL: '{downloadUrl}'");
            var downloadRequest = UnityWebRequest.Get(downloadUrl);
            downloadRequest.downloadHandler = new DownloadHandlerFile(TheaterYtDlpPath);
            var downloadOperation = downloadRequest.SendWebRequest();
            while (!downloadOperation.isDone) await Task.Yield();
            if (downloadRequest.result != UnityWebRequest.Result.Success)
            {
                _loggingService.Error($"Could not download yt-dlp.exe. Reason: {downloadRequest.error}");
                throw new Exception(downloadRequest.error);
            }

            _loggingService.Info("Downloaded latest yt-dlp.exe");

            // Copy ffmpeg.exe to Theater directory if it doesn't exist
            string theaterFfmpegPath = Path.Combine(TheaterFileHelpers.TheaterLibsPath, "ffmpeg.exe");
            if (!File.Exists(theaterFfmpegPath))
            {
                string baseFfmpegPath = Path.Combine(UnityGame.LibraryPath, "ffmpeg.exe");
                if (File.Exists(baseFfmpegPath))
                {
                    File.Copy(baseFfmpegPath, theaterFfmpegPath);
                    _loggingService.Info("Copied ffmpeg.exe to Theater directory");
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.Error($"Error downloading yt-dlp: {ex}");
            _config.PluginEnabled = false;
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
            var tasks = new List<Task> { CheckAndDownloadDeno() };
            if (await CheckForUpdate())
            {
                _loggingService.Info("Update check returned true - downloading latest yt-dlp.exe");
                tasks.Add(DownloadLatest());
            }
            else
            {
                _loggingService.Info("Update check returned false - not downloading new yt-dlp.exe");
            }
            await Task.WhenAll(tasks);

            _loggingService.Info("Update check completed");
        }
        catch (Exception ex)
        {
            _loggingService.Error($"Error during update check: {ex}");
            _config.PluginEnabled = false;
        }
    }

    private async Task CheckAndDownloadDeno()
    {
        _loggingService.Info("Starting Deno update");
        string denoPath = Path.Combine(TheaterFileHelpers.TheaterLibsPath, "deno.exe");
        if (File.Exists(denoPath))
        {
            _loggingService.Info($"Deno is already present. Not updating. Found at location: {denoPath}");
            return;
        }

        _loggingService.Info("Deno was not found in library path. Downloading latest release from GitHub");
        try
        {
            var request = UnityWebRequest.Get("https://api.github.com/repos/denoland/deno/releases/latest");
            request.SetRequestHeader("User-Agent", "BeatSaberTheater");
            var operation = request.SendWebRequest();
            while (!operation.isDone) await Task.Yield();
            if (request.result != UnityWebRequest.Result.Success)
            {
                _loggingService.Error($"Could not retrieve Deno releases! Reason: {request.error}");
                throw new Exception(request.error);
            }

            _loggingService.Info("Retrieved Deno release page");
            var json = JObject.Parse(request.downloadHandler.text);
            var assets = json["assets"] as JArray;
            var denoExpectedFileName = "deno-x86_64-pc-windows-msvc.zip";
            var zipAsset = assets?.FirstOrDefault(a =>
            {
                return a["name"]?.ToString() == denoExpectedFileName;
            });
            if (zipAsset == null)
            {
                _loggingService.Error($"Unable to update Deno: Could not find expected zip file in latest release. Looked for: {denoExpectedFileName}");
                return;
            }

            var downloadUrl = zipAsset["browser_download_url"]?.ToString();
            if (downloadUrl == null)
            {
                _loggingService.Error("Could not find download URL for Deno");
                return;
            }

            // Download zip to temp
            string tempZip = Path.GetTempFileName() + ".zip";
            _loggingService.Info($"Downloading Deno from URL '{downloadUrl}' to temporary zip file: '{tempZip}'");
            var downloadRequest = UnityWebRequest.Get(downloadUrl);
            downloadRequest.downloadHandler = new DownloadHandlerFile(tempZip);
            var downloadOperation = downloadRequest.SendWebRequest();
            while (!downloadOperation.isDone) await Task.Yield();
            if (downloadRequest.result != UnityWebRequest.Result.Success)
            {
                _loggingService.Error($"Could not download latest Deno release! Reason: {downloadRequest.error}");
                throw new Exception(downloadRequest.error);
            }

            // Extract deno.exe
            using (var archive = ZipFile.OpenRead(tempZip))
            {
                var denoEntry = archive.GetEntry("deno.exe");
                if (denoEntry != null)
                {
                    if (!Directory.Exists(TheaterFileHelpers.TheaterLibsPath)) Directory.CreateDirectory(TheaterFileHelpers.TheaterLibsPath);
                    denoEntry.ExtractToFile(denoPath, true);
                    _loggingService.Info("Downloaded and extracted deno.exe");
                }
                else
                {
                    var fileListing = archive.Entries.Select(x => x.Name).ToList();
                    _loggingService.Error($"Extracted archive did not contain deno.exe! Contained files: {string.Join(", ", fileListing)}");
                }
            }

            // Clean up temp zip
            _loggingService.Info($"Removing temporary downloaded zip file: '{tempZip}'");
            File.Delete(tempZip);
        }
        catch (Exception ex)
        {
            _loggingService.Error($"Error downloading deno: {ex}");
            _config.PluginEnabled = false;
        }
    }
}
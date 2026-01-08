using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BeatSaberTheater.Util;
using IPA.Utilities;
using Zenject;

namespace BeatSaberTheater.Services;

internal abstract class YoutubeDLServiceBase : IInitializable
{
    private readonly string _ffmpegFilepath = Path.Combine(UnityGame.LibraryPath, "ffmpeg.exe");
    protected readonly PluginConfig _config;

    private bool? _librariesAvailable;

    protected readonly LoggingService _loggingService;
    protected readonly YtDlpUpdateService _ytDlpUpdateService;

    public YoutubeDLServiceBase(LoggingService loggingService, YtDlpUpdateService ytDlpUpdateService, PluginConfig pluginConfig)
    {
        _loggingService = loggingService;
        _ytDlpUpdateService = ytDlpUpdateService;
        _config = pluginConfig;
    }

    public bool LibrariesAvailable()
    {
        if (_librariesAvailable != null) return _librariesAvailable.Value;

        _librariesAvailable = File.Exists(_ytDlpUpdateService.YtDlpPath) && File.Exists(_ffmpegFilepath);
        return _librariesAvailable.Value;
    }

    private static string GetConfigFileArgument(bool autoConfig)
    {
        if (autoConfig)
        {
            return string.Empty; // Let yt-dlp find its own config files
        }

        // Check for config files in UserData and Lib paths
        var userDataConfigPath = Path.Combine(UnityGame.UserDataPath, "yt-dlp.conf");
        var libConfigPath = Path.Combine(TheaterFileHelpers.TheaterLibsPath, "yt-dlp.conf");

        if (File.Exists(userDataConfigPath))
        {
            return $" --config-location \"{userDataConfigPath}\"";
        }

        if (File.Exists(libConfigPath))
        {
            return $" --config-location \"{libConfigPath}\"";
        }

        // No config files found, ignore config
        return " --ignore-config";
    }

    protected void DisposeProcess(Process? process)
    {
        if (process == null) return;

        int processId;
        try
        {
            processId = process.Id;
        }
        catch (Exception)
        {
            return;
        }

        _loggingService.Debug($"[{processId}] Cleaning up process");

        void WorkDelegate()
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception exception)
            {
                if (!exception.Message.Contains("The operation completed successfully") &&
                    !exception.Message.Contains("No process is associated with this object."))
                    _loggingService.Warn(exception);
            }

            try
            {
                process.Dispose();
            }
            catch (Exception exception)
            {
                _loggingService.Warn(exception);
            }
        }

        var thread = new Thread((ThreadStart)WorkDelegate);
        thread.Start();
    }

    protected Process CreateProcess(string arguments, string? workingDirectory = null)
    {
        //Use config file in UserData or Lib instead of the global yt-dlp one, or let yt-dlp auto-resolve
        arguments += GetConfigFileArgument(_config.YtDlpAutoConfig);

        var process = new Process
        {
            StartInfo =
            {
                FileName = _ytDlpUpdateService.YtDlpPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? string.Empty
            },
            EnableRaisingEvents = true,
            PriorityBoostEnabled = true
        };
        process.Disposed += OnProcessDisposed;

        return process;
    }

    protected void StartProcessThreaded(Process process)
    {
        void WorkDelegate()
        {
            var timer = new Stopwatch();
            timer.Start();
            var ret = process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _loggingService.Debug($"Starting thread took {timer.Elapsed.TotalMilliseconds}ms");
            if (!ret) _loggingService.Error("Failed to start thread");
        }

        var thread = new Thread((ThreadStart)WorkDelegate);
        thread.Start();
    }

    protected bool IsProcessRunning(Process? process)
    {
        try
        {
            return process is { HasExited: false };
        }
        catch (Exception e)
        {
            if (!(e is InvalidOperationException)) _loggingService.Warn(e);
        }

        return false;
    }

    private void OnProcessDisposed(object sender, EventArgs eventArgs)
    {
        _loggingService.Debug("Process disposed");
    }

    public void Initialize()
    {
    }
}
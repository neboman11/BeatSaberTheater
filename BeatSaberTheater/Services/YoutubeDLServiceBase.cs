using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BeatSaberTheater.Util;
using IPA.Utilities;
using Zenject;

namespace BeatSaberTheater.Services;

public abstract class YoutubeDLServiceBase : IInitializable
{
    private readonly string _ffmpegFilepath = Path.Combine(UnityGame.LibraryPath, "ffmpeg.exe");
    private readonly string _youtubeDLConfigFilepath = Path.Combine(UnityGame.UserDataPath, "youtube-dl.conf");

    private bool? _librariesAvailable;

    protected readonly LoggingService _loggingService;
    protected readonly YtDlpUpdateService _ytDlpUpdateService;

    public YoutubeDLServiceBase(LoggingService loggingService, YtDlpUpdateService ytDlpUpdateService)
    {
        _loggingService = loggingService;
        _ytDlpUpdateService = ytDlpUpdateService;
    }

    public bool LibrariesAvailable()
    {
        if (_librariesAvailable != null) return _librariesAvailable.Value;

        _librariesAvailable = File.Exists(_ytDlpUpdateService.YtDlpPath) && File.Exists(_ffmpegFilepath);
        return _librariesAvailable.Value;
    }

    private static string GetConfigFileArgument(string path)
    {
        return !File.Exists(path) ? " --ignore-config" : $" --config-location \"{path}\"";
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
        //Use config file in UserData instead of the global yt-dl one
        arguments += GetConfigFileArgument(_youtubeDLConfigFilepath);

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
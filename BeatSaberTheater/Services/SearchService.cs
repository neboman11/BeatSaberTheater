using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using BeatSaberTheater.Download;
using BeatSaberTheater.Util;
using IPA.Utilities.Async;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace BeatSaberTheater.Services;

internal class SearchService : YoutubeDLServiceBase
{
    public readonly List<YTResult> SearchResults = new();
    private Coroutine? _searchCoroutine;
    private Process? _searchProcess;

    public event Action<YTResult>? SearchProgress;
    public event Action? SearchFinished;

    private readonly PluginConfig _config;
    private readonly TheaterCoroutineStarter _coroutineStarter;

    public SearchService(PluginConfig config, TheaterCoroutineStarter coroutineStarter, LoggingService loggingService,
        YtDlpUpdateService ytDlpUpdateService)
        : base(loggingService, ytDlpUpdateService, config)
    {
        _config = config;
        _coroutineStarter = coroutineStarter;
    }

    public void Search(string query)
    {
        if (_searchCoroutine != null) _coroutineStarter.StopCoroutine(_searchCoroutine);

        _searchCoroutine = _coroutineStarter.StartCoroutine(SearchCoroutine(query));
    }

    private IEnumerator SearchCoroutine(string query, int expectedResultCount = 20)
    {
        if (IsProcessRunning(_searchProcess)) DisposeProcess(_searchProcess);

        SearchResults.Clear();
        _loggingService.Debug($"Starting search with query {query}");

        var searchProcessArguments = $"\"ytsearch{expectedResultCount}:{query}\"" +
                                     " -j" + //Instructs yt-dl to return json data without downloading anything
                                     " -i"; //Ignore errors

        _searchProcess = CreateProcess(searchProcessArguments);

        _searchProcess.OutputDataReceived += (sender, e) =>
            UnityMainThreadTaskScheduler.Factory.StartNew(delegate { SearchProcessDataReceived(e); });

        _searchProcess.Exited += (sender, e) =>
            UnityMainThreadTaskScheduler.Factory.StartNew(delegate
            {
                SearchProcessExited((Process)sender);
            });

        _loggingService.Info(
            $"Starting youtube-dl process with arguments: \"{_searchProcess.StartInfo.FileName}\" {_searchProcess.StartInfo.Arguments}");

        StartProcessThreaded(_searchProcess);

        var startProcessTimeout = new DownloadTimeout(10);
        yield return new WaitUntil(() => IsProcessRunning(_searchProcess) || startProcessTimeout.HasTimedOut);
        startProcessTimeout.Stop();

        var timeout = new DownloadTimeout(_config.SearchTimeoutSeconds);
        yield return new WaitUntil(() => !IsProcessRunning(_searchProcess) || timeout.HasTimedOut);
        timeout.Stop();

        SearchFinished?.Invoke();
        DisposeProcess(_searchProcess);
    }

    private void SearchProcessDataReceived(DataReceivedEventArgs e)
    {
        var output = e.Data?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(output)) return;

        if (output.Contains("yt command exited"))
        {
            _loggingService.Debug("Done with Youtube Search, Processing...");
            return;
        }

        if (output.Contains("yt command"))
        {
            _loggingService.Debug($"Running with {output}");
            return;
        }

        var ytResult = ParseSearchResult(output);
        if (ytResult == null) return;

        SearchResults.Add(ytResult);
        SearchProgress?.Invoke(ytResult);
    }

    private YTResult? ParseSearchResult(string searchResultJson)
    {
        if (!(JsonConvert.DeserializeObject(searchResultJson) is JObject result))
        {
            _loggingService.Error("Failed to deserialize " + searchResultJson);
            return null;
        }

        if (result["id"] == null)
        {
            _loggingService.Warn("YT search result had no ID, skipping");
            return null;
        }

        return new YTResult(result);
    }

    private void SearchProcessExited(Process process)
    {
        try { process.WaitForExit(); } catch { /* ignore */ }

        var exitCode = process.ExitCode;
        _loggingService.Info($"Search process exited with exitcode {exitCode}");

        if (exitCode != 0)
        {
            string stderr = string.Empty;
            try { stderr = process.StandardError.ReadToEnd(); } catch { /* ignore */ }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _loggingService.Error("yt-dlp search stderr output:");
                _loggingService.Error(stderr.Trim());
            }
        }

        SearchFinished?.Invoke();
        DisposeProcess(process);
        _searchProcess = null;
    }

    internal void StopSearch()
    {
        DisposeProcess(_searchProcess);
    }
}

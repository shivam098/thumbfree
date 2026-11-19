using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ThumbFree.Core;

namespace ThumbFree.Speech;

/// <summary>A model's bytes on disk (finished files plus partial downloads) out of its total.</summary>
public readonly record struct DownloadProgress(long Done, long Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}

/// <summary>
/// Speech models on this PC (the Android app's ModelStore). Downloads go to a primary folder; models are also found, read
/// only, in shared folders, where Voice Dictation and Voice Assistant keep the same single-file Whisper models, so they
/// aren't downloaded twice. A model is one or more files; each download resumes after a dropped connection and is checked
/// against its pinned SHA-256 before it gets its final name (a mismatch deletes it).
/// </summary>
/// <remarks>
/// Verified files are remembered in <see cref="IndexFileName"/> in the primary folder (path, size, last-write time and
/// hash), so making sure a model is intact on every start doesn't read 660 MB again: a file is hashed once, and again only
/// if its size or time changes. One small index rather than a sidecar per file, because the shared folders belong to other
/// apps and the index is only a cache: if it's lost, files are simply hashed once more.
/// </remarks>
public sealed class ModelStore
{
    /// <summary>The verified-files index in the primary folder (kept by <see cref="RemoveLeftovers"/>).</summary>
    public const string IndexFileName = "verified.tsv";

    private const int MaxAttempts = 5;
    private const long SpaceMargin = 50_000_000;
    private const int ReportEvery = 512 * 1024;

    private static readonly Lazy<HttpClient> DefaultHttp = new(CreateHttpClient);

    private readonly HttpClient _http;
    private readonly object _indexGate = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Download, byte> _downloads = new();
    private Dictionary<string, Verified>? _index;

    /// <param name="primaryFolder">Where downloads go (the app passes %LOCALAPPDATA%\ThumbFree\models).</param>
    /// <param name="sharedFolders">Other apps' model folders, read only (the app passes <see cref="DefaultSharedFolders"/>); none if null.</param>
    /// <param name="http">For tests; by default one shared client with a 20 s connect timeout.</param>
    public ModelStore(string primaryFolder, IEnumerable<string>? sharedFolders = null, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryFolder);
        PrimaryFolder = Path.GetFullPath(primaryFolder);
        SharedFolders = (sharedFolders ?? [])
            .Select(Path.GetFullPath)
            .Where(f => !SamePath(f, PrimaryFolder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _http = http ?? DefaultHttp.Value;
        FreeSpace = DriveFreeSpace;
    }

    /// <summary>Voice Dictation's and Voice Assistant's models folders (ThumbFree's predecessors on Windows).</summary>
    public static IReadOnlyList<string> DefaultSharedFolders { get; } =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceDictation", "models"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAssistant", "models"),
    ];

    public string PrimaryFolder { get; }

    public IReadOnlyList<string> SharedFolders { get; }

    /// <summary>Wait before retrying a failed file: attempt n waits n times this (2, 4, 6, 8 s, as on Android).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A connection that sends nothing for this long counts as dropped (and is retried).</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Free bytes where downloads go, or null if unknown. Tests replace it.</summary>
    internal Func<long?> FreeSpace { get; init; }

    private IEnumerable<string> Folders => SharedFolders.Prepend(PrimaryFolder);

    /// <summary>Where <paramref name="file"/> is: the primary folder first, then the shared folders; null if it isn't on this PC.</summary>
    public string? PathOf(ModelFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Folders.Select(folder => PathIn(folder, file)).FirstOrDefault(path => IsComplete(path, file));
    }

    public bool IsInstalled(ModelFile file) => PathOf(file) is not null;

    /// <summary>
    /// Every file of <paramref name="spec"/> is in one folder at its full size. Cheap (nothing is read): a downloaded file
    /// only gets its final name once its hash matched. <see cref="DownloadAsync(ModelSpec, IProgress{DownloadProgress}?, CancellationToken)"/>
    /// checks the hashes of files it didn't verify itself.
    /// </summary>
    public bool IsInstalled(ModelSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return FolderHolding(spec) is not null;
    }

    /// <summary>
    /// The folder a recognizer loads <paramref name="spec"/> from: for a multi-file model, its own folder
    /// ("…\models\parakeet-tdt-110m"). If the model isn't installed, where a download puts it.
    /// </summary>
    public string DirectoryOf(ModelSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Path.GetDirectoryName(PathIn(FolderHolding(spec) ?? PrimaryFolder, spec.Files[0]))!;
    }

    /// <summary>Bytes of <paramref name="spec"/> already on this PC, finished or partial (a download resumes from there).</summary>
    public long Downloaded(ModelSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Files.Sum(f => IsInstalled(f) ? f.Bytes : PartLength(f));
    }

    /// <summary>
    /// Downloads <paramref name="spec"/>'s missing files into the primary folder, one after another. Also the way to make
    /// sure an installed model is intact before loading it: files already verified are skipped without being read again.
    /// Checks the free space first, resumes partial downloads ("&lt;file&gt;.part", kept after a cancel or a dropped
    /// connection), tries each file up to 5 times (2, 4, 6, 8 s apart), and verifies its SHA-256 before giving it its final
    /// name. Throws <see cref="IOException"/> with a message people can read, or <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task DownloadAsync(ModelSpec spec, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return DownloadFilesAsync(spec.Id, spec.Files, progress, cancellationToken);
    }

    /// <summary>One file, the same way: <see cref="ModelCatalog.Vad"/> (Silero, used with Whisper) is installed with this.</summary>
    public Task DownloadAsync(ModelFile file, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return DownloadFilesAsync(file.Path, [file], progress, cancellationToken);
    }

    /// <summary>
    /// Deletes <paramref name="spec"/>'s files and partial downloads from the primary folder, stopping its download first.
    /// Copies in shared folders belong to other apps and stay (so the model may still be installed).
    /// </summary>
    public void Delete(ModelSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        foreach (var download in _downloads.Keys.Where(d => d.Key == spec.Id))
        {
            try
            {
                download.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It has just finished.
            }

            // A cancelled download closes its file within milliseconds; it must be closed before it can be deleted.
            download.Stopped.Task.Wait(TimeSpan.FromSeconds(5));
        }

        var paths = spec.Files.Select(f => PathIn(PrimaryFolder, f)).Where(p => !IsShared(p)).ToList();
        foreach (var path in paths)
        {
            DeleteFile(path);
            DeleteFile(path + ".part");
        }

        Forget(paths);
        var folder = Path.GetDirectoryName(PathIn(PrimaryFolder, spec.Files[0]))!;
        if (!SamePath(folder, PrimaryFolder) && !IsShared(folder))
        {
            DeleteIfEmpty(folder);
        }
    }

    /// <summary>
    /// Deletes what this version doesn't use from the primary folder: files that aren't a current model file (or its
    /// partial download), such as the Whisper English models older versions used, then empty folders. Shared folders
    /// belong to other apps and are never touched, even one inside the primary folder.
    /// </summary>
    public void RemoveLeftovers()
    {
        if (!Directory.Exists(PrimaryFolder))
        {
            return;
        }

        var keep = ModelCatalog.KnownPaths
            .SelectMany(p => new[] { p, p + ".part" })
            .Append(IndexFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var removed = new List<string>();
        foreach (var file in Directory.EnumerateFiles(PrimaryFolder, "*", options).Where(f => !IsShared(f)).ToList())
        {
            if (!keep.Contains(Path.GetRelativePath(PrimaryFolder, file).Replace('\\', '/')) && DeleteFile(file))
            {
                removed.Add(file);
            }
        }

        foreach (var folder in Directory.EnumerateDirectories(PrimaryFolder, "*", options).Where(f => !IsShared(f)).OrderByDescending(d => d.Length).ToList())
        {
            DeleteIfEmpty(folder);
        }

        Forget(removed);
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20) }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ThumbFree");
        return http;
    }

    private static string PathIn(string folder, ModelFile file) => Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));

    private static bool IsComplete(string path, ModelFile file)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == file.Bytes;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use; it goes next time.
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private long PartLength(ModelFile file)
    {
        var part = new FileInfo(PathIn(PrimaryFolder, file) + ".part");
        return part.Exists ? part.Length : 0;
    }

    private long? DriveFreeSpace()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(PrimaryFolder)!).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? FolderHolding(ModelSpec spec) => Folders.FirstOrDefault(folder => spec.Files.All(f => IsComplete(PathIn(folder, f), f)));

    /// <summary>Whether <paramref name="path"/> is a shared folder or inside one.</summary>
    private bool IsShared(string path) =>
        SharedFolders.Any(f => SamePath(path, f) || path.StartsWith(Path.TrimEndingDirectorySeparator(f) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private async Task DownloadFilesAsync(string key, IReadOnlyList<ModelFile> files, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var download = new Download(key, cancellation);
        _downloads[download] = 0;
        try
        {
            var token = cancellation.Token;
            var needed = files.Where(f => !IsInstalled(f)).Sum(f => f.Bytes - PartLength(f));
            if (needed > 0 && FreeSpace() is { } free && free < needed + SpaceMargin)
            {
                throw new IOException($"Not enough free space: {(needed + SpaceMargin) / 1_000_000} MB needed, {free / 1_000_000} MB free.");
            }

            var total = files.Sum(f => f.Bytes);
            long settled = 0;
            for (var i = 0; i < files.Count; i++)
            {
                var others = settled + files.Skip(i + 1).Sum(f => IsInstalled(f) ? f.Bytes : PartLength(f));
                await EnsureFileAsync(files[i], done => progress?.Report(new DownloadProgress(others + done, total)), token).ConfigureAwait(false);
                settled += files[i].Bytes;
            }

            progress?.Report(new DownloadProgress(total, total));
        }
        finally
        {
            _downloads.TryRemove(download, out _);
            download.Stopped.TrySetResult();
        }
    }

    /// <summary>Makes sure <paramref name="file"/> is on this PC and intact, downloading it if needed.</summary>
    private async Task EnsureFileAsync(ModelFile file, Action<long> onBytes, CancellationToken cancellationToken)
    {
        var gate = _fileGates.GetOrAdd(file.Path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var folder in Folders)
            {
                var existing = PathIn(folder, file);
                if (!IsComplete(existing, file))
                {
                    continue;
                }

                if (await IsIntactAsync(existing, file, cancellationToken).ConfigureAwait(false))
                {
                    onBytes(file.Bytes);
                    return;
                }

                if (SamePath(folder, PrimaryFolder) && !IsShared(existing))
                {
                    // Damaged on disk: download it again.
                    DeleteFile(existing);
                    Forget([existing]);
                }
            }

            var target = PathIn(PrimaryFolder, file);
            var part = target + ".part";
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await FetchAsync(file, part, onBytes, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (Exception e) when ((e is IOException or HttpRequestException) && !cancellationToken.IsCancellationRequested)
                {
                    if (attempt >= MaxAttempts)
                    {
                        throw new IOException($"Couldn't download {file.Name}: {e.Message}", e);
                    }

                    await Task.Delay(RetryDelay * attempt, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!string.Equals(await HashAsync(part, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                DeleteFile(part);
                throw new IOException($"The download of {file.Name} was damaged, so it was deleted. Try again.");
            }

            File.Move(part, target, overwrite: true);
            Remember(target, file);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>One attempt: appends to <paramref name="part"/> from where it ends (HTTP Range), or starts over if the server can't resume.</summary>
    private async Task FetchAsync(ModelFile file, string part, Action<long> onBytes, CancellationToken cancellationToken)
    {
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have >= file.Bytes)
        {
            return;
        }

        // Every attempt: the leftover cleanup may have removed an empty folder.
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
        }

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(ReadTimeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                DeleteFile(part);
                throw new IOException("The server couldn't resume the download.");
            }

            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            {
                throw new IOException($"The server answered {(int)response.StatusCode}.");
            }

            var append = response.StatusCode == HttpStatusCode.PartialContent;
            if (append && response.Content.Headers.ContentRange?.From != have)
            {
                DeleteFile(part);
                throw new IOException("The server resumed at the wrong place.");
            }

            var done = append ? have : 0;
            onBytes(done);
            await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
            var buffer = new byte[1 << 16];
            var reported = done;
            while (done < file.Bytes)
            {
                stall.CancelAfter(ReadTimeout);
                var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                done += read;
                if (done - reported >= ReportEvery)
                {
                    reported = done;
                    onBytes(done);
                }
            }

            onBytes(done);
            if (done < file.Bytes)
            {
                throw new IOException("The connection dropped.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The connection stalled.");
        }
    }

    private async Task<bool> IsIntactAsync(string path, ModelFile file, CancellationToken cancellationToken)
    {
        if (IsRemembered(path, file))
        {
            return true;
        }

        if (!string.Equals(await HashAsync(path, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Remember(path, file);
        return true;
    }

    private bool IsRemembered(string path, ModelFile file)
    {
        var info = new FileInfo(path);
        lock (_indexGate)
        {
            return info.Exists
                && Index().TryGetValue(info.FullName, out var verified)
                && verified.Bytes == info.Length
                && verified.WriteTicks == info.LastWriteTimeUtc.Ticks
                && string.Equals(verified.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void Remember(string path, ModelFile file)
    {
        var info = new FileInfo(path);
        lock (_indexGate)
        {
            Index()[info.FullName] = new Verified(file.Sha256.ToLowerInvariant(), info.Length, info.LastWriteTimeUtc.Ticks);
            SaveIndex();
        }
    }

    private void Forget(IEnumerable<string> paths)
    {
        lock (_indexGate)
        {
            var index = Index();
            var changed = false;
            foreach (var path in paths)
            {
                changed |= index.Remove(Path.GetFullPath(path));
            }

            if (changed)
            {
                SaveIndex();
            }
        }
    }

    private Dictionary<string, Verified> Index()
    {
        if (_index is not null)
        {
            return _index;
        }

        _index = new Dictionary<string, Verified>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(PrimaryFolder, IndexFileName)))
            {
                var cells = line.Split('\t');
                if (cells.Length == 4
                    && long.TryParse(cells[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
                    && long.TryParse(cells[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                {
                    _index[cells[3]] = new Verified(cells[0], bytes, ticks);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No index yet (or unreadable): files are hashed once more.
        }

        return _index;
    }

    private void SaveIndex()
    {
        var path = Path.Combine(PrimaryFolder, IndexFileName);
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(PrimaryFolder);
            File.WriteAllLines(temp, _index!.Select(e => string.Join('\t', e.Value.Sha256, e.Value.Bytes.ToString(CultureInfo.InvariantCulture), e.Value.WriteTicks.ToString(CultureInfo.InvariantCulture), e.Key)));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only a cache (a read-only models folder, say): files are hashed again next time.
            DeleteFile(temp);
        }
    }

    private sealed record Verified(string Sha256, long Bytes, long WriteTicks);

    /// <summary>A running download, so <see cref="Delete"/> can stop it and wait until its file is closed.</summary>
    private sealed class Download(string key, CancellationTokenSource cancellation)
    {
        public string Key { get; } = key;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ThumbFree.Core;

namespace ThumbFree.Speech.Tests;

/// <summary>Downloads against a local HttpListener: resume, retries, hash checks, shared folders and cleanup.</summary>
public sealed class ModelStoreTests : IDisposable
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false });

    private readonly TestServer _server = new();
    private readonly Scratch _scratch = new();

    public void Dispose()
    {
        _server.Dispose();
        _scratch.Dispose();
    }

    [Fact]
    public async Task Downloads_files_one_after_another_and_names_them_once_verified()
    {
        var encoder = Bytes(1);
        var tokens = Bytes(2, 1_000);
        var spec = Spec(Serve("model-x/encoder.onnx", encoder), Serve("model-x/tokens.txt", tokens));
        var store = Store();
        var reports = new List<DownloadProgress>();

        Assert.False(store.IsInstalled(spec));
        await store.DownloadAsync(spec, new Reporter<DownloadProgress>(reports.Add));

        Assert.True(store.IsInstalled(spec));
        Assert.Equal(_scratch.File("models/model-x"), store.DirectoryOf(spec));
        Assert.Equal(_scratch.File("models/model-x/tokens.txt"), store.PathOf(spec.Files[1]));
        Assert.Equal(encoder, File.ReadAllBytes(_scratch.File("models/model-x/encoder.onnx")));
        Assert.Equal(tokens, File.ReadAllBytes(_scratch.File("models/model-x/tokens.txt")));
        Assert.Empty(Directory.GetFiles(_scratch.Path, "*.part", SearchOption.AllDirectories));
        Assert.Equal(new[] { "model-x_encoder.onnx -", "model-x_tokens.txt -" }, _server.Requests);
        Assert.Equal(new DownloadProgress(spec.Bytes, spec.Bytes), reports[^1]);
        Assert.All(reports, r => Assert.InRange(r.Done, 0, spec.Bytes));
    }

    [Fact]
    public async Task Installs_a_single_file_such_as_the_voice_activity_model()
    {
        var vad = Serve("ggml-silero-test.bin", Bytes(3, 5_000));
        var store = Store();

        await store.DownloadAsync(vad);

        Assert.Equal(_scratch.File("models/ggml-silero-test.bin"), store.PathOf(vad));
        Assert.True(store.IsInstalled(vad));
    }

    [Fact]
    public async Task Resumes_after_a_dropped_connection()
    {
        var bytes = Bytes(4);
        var spec = Spec(Serve("big.bin", bytes));
        _server.Behave = n => n == 1 ? Reply.DropHalfway : Reply.Normal;

        await Store().DownloadAsync(spec);

        Assert.Equal(bytes, File.ReadAllBytes(_scratch.File("models/big.bin")));
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal("big.bin -", _server.Requests[0]);
        Assert.InRange(ResumedAt(_server.Requests[1]), 1, bytes.Length - 1);
    }

    [Fact]
    public async Task A_stalled_connection_is_retried_from_where_it_stopped()
    {
        var bytes = Bytes(5);
        var spec = Spec(Serve("big.bin", bytes));
        _server.Behave = n => n == 1 ? Reply.StallHalfway : Reply.Normal;

        await Store(readTimeout: TimeSpan.FromMilliseconds(300)).DownloadAsync(spec);

        Assert.Equal(bytes, File.ReadAllBytes(_scratch.File("models/big.bin")));
        Assert.InRange(ResumedAt(_server.Requests[1]), 1, bytes.Length - 1);
    }

    [Fact]
    public async Task Starts_over_when_the_server_cannot_resume()
    {
        var bytes = Bytes(6);
        var spec = Spec(Serve("big.bin", bytes));
        _server.Behave = n => n == 1 ? Reply.DropHalfway : Reply.IgnoreRange;

        await Store().DownloadAsync(spec);

        Assert.Equal(bytes, File.ReadAllBytes(_scratch.File("models/big.bin")));
        Assert.StartsWith("big.bin bytes=", _server.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_keeps_the_partial_download_for_next_time()
    {
        var bytes = Bytes(7);
        var spec = Spec(Serve("big.bin", bytes));
        _server.Behave = n => n == 1 ? Reply.StallHalfway : Reply.Normal;
        var part = _scratch.File("models/big.bin.part");
        using var cancel = new CancellationTokenSource();
        var download = Store().DownloadAsync(spec, cancellationToken: cancel.Token);
        await WaitUntil(() => File.Exists(part) && new FileInfo(part).Length > 0);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        var kept = new FileInfo(part).Length;
        await Store().DownloadAsync(spec);

        Assert.InRange(kept, 1, bytes.Length - 1);
        Assert.Equal($"big.bin bytes={kept}-", _server.Requests[1]);
        Assert.Equal(bytes, File.ReadAllBytes(_scratch.File("models/big.bin")));
    }

    [Fact]
    public async Task A_damaged_download_is_deleted_with_a_clear_message()
    {
        var spec = Spec(Serve("model.bin", Bytes(8), pinned: Bytes(9)));

        var error = await Assert.ThrowsAsync<IOException>(() => Store().DownloadAsync(spec));

        Assert.Contains("model.bin was damaged", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_scratch.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Retries_failed_requests_then_gives_up_after_five_tries()
    {
        var good = Spec(Serve("good.bin", Bytes(10)));
        _server.Behave = n => n <= 2 ? Reply.ServerError : Reply.Normal;
        await Store().DownloadAsync(good);
        Assert.Equal(3, _server.Requests.Count);

        var bad = Spec(Serve("bad.bin", Bytes(11)));
        _server.Behave = _ => Reply.ServerError;
        var error = await Assert.ThrowsAsync<IOException>(() => Store().DownloadAsync(bad));

        Assert.Equal("Couldn't download bad.bin: The server answered 500.", error.Message);
        Assert.Equal(5, _server.Requests.Count(r => r.StartsWith("bad.bin", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Checks_the_free_space_before_starting()
    {
        var spec = Spec(Serve("big.bin", Bytes(12)));
        var store = new ModelStore(_scratch.File("models"), null, Http) { FreeSpace = () => 1_000 };

        var error = await Assert.ThrowsAsync<IOException>(() => store.DownloadAsync(spec));

        Assert.StartsWith("Not enough free space", error.Message, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Skips_files_already_present_and_verified_even_after_a_restart()
    {
        var spec = Spec(Serve("model-x/encoder.onnx", Bytes(13)), Serve("model-x/tokens.txt", Bytes(14, 900)));
        await Store().DownloadAsync(spec);

        await Store().DownloadAsync(spec);
        File.Delete(_scratch.File("models/" + ModelStore.IndexFileName));
        await Store().DownloadAsync(spec);

        Assert.Equal(2, _server.Requests.Count);
    }

    [Fact]
    public async Task Remembers_verified_files_by_size_and_time_instead_of_hashing_them_again()
    {
        var bytes = Bytes(15);
        var spec = Spec(Serve("model.bin", bytes));
        await Store().DownloadAsync(spec);
        var path = _scratch.File("models/model.bin");

        // Same size and time: trusted from the index, not read (that's what keeps a 660 MB check instant).
        var time = File.GetLastWriteTimeUtc(path);
        var damaged = File.ReadAllBytes(path);
        damaged[^1] ^= 0xFF;
        File.WriteAllBytes(path, damaged);
        File.SetLastWriteTimeUtc(path, time);
        await Store().DownloadAsync(spec);
        Assert.Single(_server.Requests);

        // A new time: hashed again, found damaged, downloaded again.
        File.SetLastWriteTimeUtc(path, time.AddMinutes(1));
        await Store().DownloadAsync(spec);
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Uses_models_from_shared_folders_without_changing_them()
    {
        var bytes = Bytes(16);
        var spec = Spec(Serve("ggml-shared.bin", bytes));

        // One shared folder inside the models folder, where a cleanup could reach it, and one next to it.
        var inside = SharedFolder("models/voice-assistant", ("ggml-shared.bin", bytes));
        var alongside = SharedFolder("voice-dictation", ("ggml-shared.bin", bytes));
        var before = Snapshot(inside, alongside);
        var store = Store([inside, alongside]);

        Assert.True(store.IsInstalled(spec));
        Assert.Equal(Path.Combine(inside, "ggml-shared.bin"), store.PathOf(spec.Files[0]));
        Assert.Equal(inside, store.DirectoryOf(spec));
        await store.DownloadAsync(spec);
        store.RemoveLeftovers();
        store.Delete(spec);

        Assert.Empty(_server.Requests);
        Assert.True(store.IsInstalled(spec));
        Assert.Equal(before, Snapshot(inside, alongside));
    }

    [Fact]
    public async Task A_damaged_shared_copy_is_left_alone_and_the_model_downloaded()
    {
        var bytes = Bytes(17);
        var spec = Spec(Serve("ggml-shared.bin", bytes));
        var shared = SharedFolder("voice-assistant", ("ggml-shared.bin", Bytes(18)));
        var before = Snapshot(shared);
        var store = Store([shared]);

        await store.DownloadAsync(spec);

        Assert.Equal(_scratch.File("models/ggml-shared.bin"), store.PathOf(spec.Files[0]));
        Assert.Equal(before, Snapshot(shared));
    }

    [Fact]
    public async Task Delete_stops_the_download_and_removes_the_model()
    {
        var bytes = Bytes(19);
        var done = Spec(Serve("model-x/tokens.txt", Bytes(20, 500)));
        var store = Store();
        await store.DownloadAsync(done);
        store.Delete(done);
        Assert.False(store.IsInstalled(done));
        Assert.False(Directory.Exists(_scratch.File("models/model-x")));

        var running = Spec(Serve("model-y/encoder.onnx", bytes));
        _server.Behave = _ => Reply.StallHalfway;
        var download = store.DownloadAsync(running);
        await WaitUntil(() => File.Exists(_scratch.File("models/model-y/encoder.onnx.part")));
        store.Delete(running);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.False(Directory.Exists(_scratch.File("models/model-y")));
    }

    [Fact]
    public void RemoveLeftovers_keeps_current_models_and_never_touches_shared_folders()
    {
        string[] keep =
        [
            ModelCatalog.EnglishFast.Files[3].Path,
            ModelCatalog.EnglishFast.Files[0].Path + ".part",
            ModelCatalog.Vad.Path,
            ModelCatalog.Hinglish.Files[0].Path,
            ModelStore.IndexFileName,
        ];
        string[] leftovers = ["ggml-small.en-q8_0.bin", "ggml-small.en-q8_0.bin.partial", "parakeet-tdt-110m/old.onnx", "old-model/encoder.onnx"];
        foreach (var path in keep.Concat(leftovers))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_scratch.File("models/" + path))!);
            File.WriteAllText(_scratch.File("models/" + path), "x");
        }

        Directory.CreateDirectory(_scratch.File("models/empty/inner"));
        var inside = SharedFolder("models/voice-assistant");
        var alongside = SharedFolder("voice-dictation");
        var before = Snapshot(inside, alongside);

        Store([inside, alongside]).RemoveLeftovers();

        Assert.All(keep, path => Assert.True(File.Exists(_scratch.File("models/" + path)), path));
        Assert.All(leftovers, path => Assert.False(File.Exists(_scratch.File("models/" + path)), path));
        Assert.False(Directory.Exists(_scratch.File("models/old-model")));
        Assert.False(Directory.Exists(_scratch.File("models/empty")));
        Assert.Equal(before, Snapshot(inside, alongside));
    }

    private static byte[] Bytes(int seed, int length = 300_000)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static ModelSpec Spec(params ModelFile[] files) => new("test-model", "Test model", Language.English, Engine.Parakeet, files);

    private static long ResumedAt(string request) => long.Parse(Regex.Match(request, @"bytes=(\d+)-$").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Every file and folder under <paramref name="folders"/>, with its content hash and last-write time.</summary>
    private static List<string> Snapshot(params string[] folders) =>
        folders
            .SelectMany(f => Directory.EnumerateFileSystemEntries(f, "*", SearchOption.AllDirectories).Append(f))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(p => File.Exists(p)
                ? $"{p} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))} {File.GetLastWriteTimeUtc(p).Ticks}"
                : $"{p} folder {Directory.GetLastWriteTimeUtc(p).Ticks}")
            .ToList();

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "timed out waiting");
    }

    /// <summary>
    /// Another app's models folder: <paramref name="files"/>, an old model and a partial download ThumbFree doesn't use,
    /// an empty folder, all dated a year ago (so any write shows).
    /// </summary>
    private string SharedFolder(string relative, params (string Name, byte[] Bytes)[] files)
    {
        var folder = _scratch.File(relative);
        Directory.CreateDirectory(Path.Combine(folder, "old"));
        foreach (var (name, bytes) in files.Append(("ggml-small.en-q8_0.bin", Bytes(98, 1_000))).Append(("ggml-small.en-q8_0.bin.partial", Bytes(99, 500))))
        {
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
        }

        var yearAgo = DateTime.UtcNow.AddYears(-1);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            File.SetLastWriteTimeUtc(file, yearAgo);
        }

        Directory.SetLastWriteTimeUtc(Path.Combine(folder, "old"), yearAgo);
        Directory.SetLastWriteTimeUtc(folder, yearAgo);
        return folder;
    }

    private ModelFile Serve(string path, byte[] bytes, byte[]? pinned = null)
    {
        var url = _server.Add(path.Replace('/', '_'), bytes);
        var expected = pinned ?? bytes;
        return new ModelFile(path, url, Convert.ToHexStringLower(SHA256.HashData(expected)), expected.Length);
    }

    private ModelStore Store(IReadOnlyList<string>? shared = null, TimeSpan? readTimeout = null) =>
        new(_scratch.File("models"), shared, Http)
        {
            RetryDelay = TimeSpan.FromMilliseconds(10),
            ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(10),
        };

    private sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

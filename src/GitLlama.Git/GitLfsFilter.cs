using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LibGit2Sharp;

namespace GitLlama.Git;

/// <summary>
/// Runs files marked <c>filter=lfs</c> through git-lfs when LibGit2Sharp stages or checks them out, the way
/// command-line Git does. Without it, staging an LFS file would commit its full content instead of a pointer.
/// </summary>
/// <remarks>
/// Files are sent to one long-running <c>git lfs filter-process</c> per repository, using Git's long-running
/// filter protocol, instead of starting git-lfs for every file. LibGit2Sharp keeps a filter's current stream in
/// shared fields, so only one file can be filtered at a time across the process; each file holds a gate from
/// <see cref="Create"/> until <see cref="Complete"/>.
/// </remarks>
public sealed class GitLfsFilter : Filter
{
    [ThreadStatic]
    private static ReadOnlyScope? _readOnlyScope;

    /// <summary>
    /// Status and previews need the pointer, not a second on-disk copy of each large file.
    /// Keep this scope on the synchronous thread making the libgit2 calls. Actual staging must
    /// run outside it so git-lfs stores the objects that will later be uploaded.
    /// Custom LFS extensions still use git-lfs because they can transform the content.
    /// </summary>
    internal static IDisposable ReadOnly(Repository repository) => new ReadOnlyScope(repository);

    private sealed class ReadOnlyScope : IDisposable
    {
        private readonly ReadOnlyScope? _previous = _readOnlyScope;
        private readonly Dictionary<string, bool> _repositories = new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        public ReadOnlyScope(Repository repository)
        {
            if (!repository.Info.IsBare)
            {
                _repositories[Path.GetFullPath(repository.Info.WorkingDirectory)] = SupportsHashing(repository);
            }

            _readOnlyScope = this;
        }

        public bool CanHash(string root)
        {
            root = Path.GetFullPath(root);
            if (!_repositories.TryGetValue(root, out var enabled))
            {
                // A parent status read can also filter files inside its submodules.
                using var repository = new Repository(root);
                _repositories[root] = enabled = SupportsHashing(repository);
            }
            return enabled;
        }

        private static bool SupportsHashing(Repository repository) => !repository.Config.Any(
            entry => entry.Key.StartsWith("lfs.extension.", StringComparison.OrdinalIgnoreCase));

        public void Dispose() => _readOnlyScope = _previous;
    }

    private static readonly TimeSpan GateTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly Lazy<GitLfsFilter> Instance = new(() =>
    {
        var filter = new GitLfsFilter();
        GlobalSettings.RegisterFilter(filter);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => filter.StopAll();
        return filter;
    });
    private readonly object _sync = new();
    private readonly Dictionary<string, FilterProcess> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _idleTimer;
    private SemaphoreSlim _gate = new(1, 1);
    private Session? _active;
    private int _skippedDownloads;

    private GitLfsFilter()
        : base("lfs", [new FilterAttributeEntry("lfs")]) =>
        _idleTimer = new Timer(_ => StopIdle(), null, IdleTimeout, IdleTimeout);

    /// <summary>Registers the filter once for the process. Safe to call repeatedly.</summary>
    public static void EnsureRegistered() => _ = Instance.Value;

    /// <summary>Files checked out as pointers because their content couldn't be downloaded, since the last call.</summary>
    public static int TakeSkippedDownloads() => Interlocked.Exchange(ref Instance.Value._skippedDownloads, 0);

    /// <summary>The number of git-lfs processes currently kept running.</summary>
    public static int RunningProcesses
    {
        get
        {
            lock (Instance.Value._sync)
            {
                return Instance.Value._processes.Count;
            }
        }
    }

    /// <summary>Stops every git-lfs process. They start again when next needed.</summary>
    public static void Shutdown() => Instance.Value.StopAll();

    protected override void Create(string path, string root, FilterMode mode)
    {
        var hashOnly = mode == FilterMode.Clean && (_readOnlyScope?.CanHash(root) ?? false);
        var gate = Enter();
        var session = new Session(mode == FilterMode.Clean, path, root, gate, hashOnly);
        try
        {
            if (session.Hash is null)
            {
                session.Process = Acquire(root);
                session.Process.Begin(session.IsClean ? "clean" : "smudge", path);
            }
        }
        catch (System.ComponentModel.Win32Exception) when (!session.IsClean)
        {
            // Without git-lfs the pointer is checked out as-is, as Git does when LFS isn't installed.
            session.Process = null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Leave(session);
            throw new InvalidOperationException(
                $"{path} is tracked by Git LFS. Install Git LFS (https://git-lfs.com) to stage it."
            );
        }
        catch
        {
            Discard(session.Process);
            Leave(session);
            throw;
        }

        lock (_sync)
        {
            _active = session;
        }
    }

    protected override void Clean(string path, string root, Stream input, Stream output) => Write(input);

    protected override void Smudge(string path, string root, Stream input, Stream output) => Write(input);

    protected override void Complete(string path, string root, Stream output)
    {
        var session = Current();
        if (session is null)
        {
            return;
        }

        try
        {
            Finish(session, output);
        }
        catch
        {
            Discard(session.Process);
            throw;
        }
        finally
        {
            Leave(session);
        }
    }

    private void Finish(Session session, Stream output)
    {
        if (session.Hash is not null)
        {
            // LFS pointers are at most 1024 bytes. Delegate small inputs to git-lfs so
            // existing pointers, including extensions and older versions, retain its semantics.
            if (session.Length > 1024)
            {
                var oid = Convert.ToHexString(session.Hash.GetHashAndReset()).ToLowerInvariant();
                var pointer = $"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize {session.Length.ToString(CultureInfo.InvariantCulture)}\n";
                output.Write(Encoding.UTF8.GetBytes(pointer));
                return;
            }

            session.Process = Acquire(session.Root);
            session.Process.Begin("clean", session.Path);
            session.Pointer.Position = 0;
            session.Process.WriteContent(session.Pointer);
        }

        if (session.Process is not { } process)
        {
            WritePointer(session, output);
            return;
        }

        var counter = new CountingStream(output, session.IsClean ? 0 : (int)session.Pointer.Length + 1);
        bool succeeded;
        try
        {
            succeeded = process.End(counter);
        }
        catch (EndOfStreamException) when (!session.IsClean)
        {
            // git-lfs stopped partway; start a new one for the next file and keep the pointer for this one.
            Discard(process);
            succeeded = false;
        }

        if (succeeded && (session.IsClean || !counter.StartsWith(session.Pointer)))
        {
            return;
        }

        if (session.IsClean)
        {
            throw new InvalidOperationException(
                $"Git LFS couldn't process {session.Path}: {process.RecentErrors()}".TrimEnd(' ', ':')
            );
        }

        // The content couldn't be downloaded (offline or no access). git-lfs returns the pointer in that case;
        // keep it so checkout still succeeds and the user can download it later.
        if (counter.Written == 0)
        {
            WritePointer(session, output);
        }

        Interlocked.Increment(ref _skippedDownloads);
    }

    private static void WritePointer(Session session, Stream output)
    {
        session.Pointer.Position = 0;
        session.Pointer.CopyTo(output);
    }

    private void Write(Stream input)
    {
        var session = Current() ?? throw new InvalidOperationException("Git LFS filter wasn't started.");
        try
        {
            if (session.IsClean)
            {
                if (session.Hash is not null)
                {
                    var buffer = new byte[65536];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        session.Hash.AppendData(buffer, 0, read);
                        var keep = (int)Math.Min(read, 1024 - session.Pointer.Length);
                        if (keep > 0)
                        {
                            session.Pointer.Write(buffer, 0, keep);
                        }

                        session.Length += read;
                    }
                    return;
                }

                session.Process!.WriteContent(input);
                return;
            }

            // Smudge input is a small pointer; keep a copy in case the download fails.
            var start = session.Pointer.Length;
            session.Pointer.Position = start;
            input.CopyTo(session.Pointer);
            if (session.Process is not null)
            {
                session.Pointer.Position = start;
                session.Process.WriteContent(session.Pointer);
            }
        }
        catch
        {
            // libgit2 won't call Complete after a failed write, so release the file here.
            Discard(session.Process);
            Leave(session);
            throw;
        }
    }

    /// <summary>Returns the running filter process for <paramref name="root"/>, starting it if needed.</summary>
    private FilterProcess Acquire(string root)
    {
        lock (_sync)
        {
            if (_processes.TryGetValue(root, out var existing) && existing.IsAlive)
            {
                return existing;
            }

            existing?.Dispose();
            _processes.Remove(root);
        }

        var started = FilterProcess.Start(root);
        lock (_sync)
        {
            _processes[root] = started;
        }

        return started;
    }

    /// <summary>Stops a process whose protocol state is unknown after an error, so the next file starts fresh.</summary>
    private void Discard(FilterProcess? process)
    {
        if (process is null)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var entry in _processes.Where(entry => entry.Value == process).ToArray())
            {
                _processes.Remove(entry.Key);
            }
        }

        process.Dispose();
    }

    private void StopIdle()
    {
        List<FilterProcess> idle;
        lock (_sync)
        {
            idle = _processes
                .Where(entry => _active?.Process != entry.Value && entry.Value.IdleFor > IdleTimeout)
                .Select(entry => entry.Value)
                .ToList();
            foreach (var process in idle)
            {
                _processes.Remove(process.Root);
            }
        }

        idle.ForEach(process => process.Dispose());
    }

    private void StopAll()
    {
        List<FilterProcess> all;
        lock (_sync)
        {
            all = _processes.Values.Where(process => _active?.Process != process).ToList();
            foreach (var process in all)
            {
                _processes.Remove(process.Root);
            }
        }

        all.ForEach(process => process.Dispose());
    }

    /// <summary>Waits for the gate. A session left open on this thread by a failed operation is discarded.</summary>
    private SemaphoreSlim Enter()
    {
        lock (_sync)
        {
            if (_active is { } stale && stale.Thread == Environment.CurrentManagedThreadId)
            {
                stale.Released = true;
                _active = null;
                DiscardLocked(stale.Process);
                stale.Hash?.Dispose();
                stale.Pointer.Dispose();
                return stale.Gate;
            }
        }

        var gate = _gate;
        if (gate.Wait(GateTimeout))
        {
            return gate;
        }

        // The holder never finished. Abandon it and start a fresh gate rather than blocking forever.
        lock (_sync)
        {
            DiscardLocked(_active?.Process);
            _active = null;
            _gate = new SemaphoreSlim(0, 1);
            return _gate;
        }
    }

    private void DiscardLocked(FilterProcess? process)
    {
        if (process is null)
        {
            return;
        }

        _processes.Remove(process.Root);
        process.Dispose();
    }

    private void Leave(Session session)
    {
        lock (_sync)
        {
            if (session.Released)
            {
                return;
            }

            session.Released = true;
            if (_active == session)
            {
                _active = null;
            }
        }

        session.Gate.Release();
        session.Hash?.Dispose();
        session.Pointer.Dispose();
    }

    private Session? Current()
    {
        lock (_sync)
        {
            return _active?.Thread == Environment.CurrentManagedThreadId ? _active : null;
        }
    }

    private sealed class Session(bool isClean, string path, string root, SemaphoreSlim gate, bool hashOnly)
    {
        public bool IsClean { get; } = isClean;
        public string Path { get; } = path;
        public string Root { get; } = root;
        public IncrementalHash? Hash { get; } = hashOnly
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        public long Length { get; set; }
        public SemaphoreSlim Gate { get; } = gate;
        public int Thread { get; } = Environment.CurrentManagedThreadId;
        public bool Released { get; set; }
        public FilterProcess? Process { get; set; }
        public MemoryStream Pointer { get; } = new();
    }

    /// <summary>One <c>git lfs filter-process</c> speaking Git's long-running filter protocol (version 2).</summary>
    private sealed class FilterProcess : IDisposable
    {
        private const int MaxPayload = 65516;
        private readonly Process _process;
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly StringBuilder _errors = new();
        private long _lastUsed = Environment.TickCount64;

        private FilterProcess(string root, Process process)
        {
            Root = root;
            _process = process;
            _input = process.StandardInput.BaseStream;
            _output = process.StandardOutput.BaseStream;
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (_errors)
                {
                    _errors.AppendLine(e.Data);
                    if (_errors.Length > 4000)
                    {
                        _errors.Remove(0, _errors.Length - 4000);
                    }
                }
            };
            process.BeginErrorReadLine();
        }

        public string Root { get; }
        public bool IsAlive => !_process.HasExited;
        public TimeSpan IdleFor => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastUsed));

        public static FilterProcess Start(string root)
        {
            var start = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root,
            };
            start.ArgumentList.Add("lfs");
            start.ArgumentList.Add("filter-process");
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            // Return the pointer instead of exiting when content can't be downloaded.
            start.Environment["GIT_LFS_SKIP_DOWNLOAD_ERRORS"] = "1";
            var process = Process.Start(start) ?? throw new InvalidOperationException("Git LFS didn't start.");
            var filter = new FilterProcess(root, process);
            try
            {
                filter.Handshake();
            }
            catch
            {
                filter.Dispose();
                throw;
            }

            return filter;
        }

        /// <summary>Sends a request header; the file's content follows with <see cref="WriteContent"/>.</summary>
        public void Begin(string command, string path)
        {
            Interlocked.Exchange(ref _lastUsed, Environment.TickCount64);
            lock (_errors)
            {
                _errors.Clear();
            }

            WriteText("command=" + command);
            WriteText("pathname=" + path.Replace('\\', '/'));
            WriteFlush();
        }

        public void WriteContent(Stream content)
        {
            var buffer = new byte[MaxPayload];
            int read;
            while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
            {
                WritePacket(buffer, read);
            }
        }

        /// <summary>Ends the content and copies the filtered result to <paramref name="output"/>.</summary>
        /// <returns>True when git-lfs filtered the file successfully.</returns>
        public bool End(Stream output)
        {
            WriteFlush();
            if (Status(ReadTextList()) != "success")
            {
                return false;
            }

            while (ReadPacket() is { } packet)
            {
                output.Write(packet, 0, packet.Length);
            }

            // A trailing status replaces the first one only when it's sent; an empty list keeps "success".
            var final = Status(ReadTextList());
            Interlocked.Exchange(ref _lastUsed, Environment.TickCount64);
            return final is null or "success";
        }

        public string RecentErrors()
        {
            lock (_errors)
            {
                return _errors.ToString().Trim();
            }
        }

        public void Dispose()
        {
            try
            {
                // Closing input asks git-lfs to exit; stop it if it doesn't.
                _input.Close();
                if (!_process.WaitForExit(2000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException) { }

            _process.Dispose();
        }

        private void Handshake()
        {
            WriteText("git-filter-client");
            WriteText("version=2");
            WriteFlush();
            var welcome = ReadTextList();
            if (!welcome.Contains("git-filter-server") || !welcome.Contains("version=2"))
            {
                throw new InvalidOperationException("Git LFS didn't accept the filter protocol.");
            }

            WriteText("capability=clean");
            WriteText("capability=smudge");
            WriteFlush();
            var capabilities = ReadTextList();
            if (!capabilities.Contains("capability=clean") || !capabilities.Contains("capability=smudge"))
            {
                throw new InvalidOperationException("Git LFS doesn't support clean and smudge filters.");
            }
        }

        private static string? Status(IReadOnlyList<string> list) =>
            list.LastOrDefault(line => line.StartsWith("status=", StringComparison.Ordinal))?["status=".Length..];

        private void WriteText(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            WritePacket(bytes, bytes.Length);
        }

        private void WritePacket(byte[] payload, int count)
        {
            _input.Write(Encoding.ASCII.GetBytes((count + 4).ToString("x4")));
            _input.Write(payload, 0, count);
        }

        // A flush packet ends a message, so push it through the pipe for git-lfs to act on.
        private void WriteFlush()
        {
            _input.Write("0000"u8);
            _input.Flush();
        }

        private List<string> ReadTextList()
        {
            var lines = new List<string>();
            while (ReadPacket() is { } packet)
            {
                lines.Add(Encoding.UTF8.GetString(packet).TrimEnd('\n'));
            }

            return lines;
        }

        /// <summary>Reads one packet's payload, or null for a flush packet.</summary>
        private byte[]? ReadPacket()
        {
            var header = new byte[4];
            _output.ReadExactly(header);
            var length = Convert.ToInt32(Encoding.ASCII.GetString(header), 16);
            if (length == 0)
            {
                return null;
            }

            if (length < 4)
            {
                throw new InvalidOperationException("Git LFS sent an invalid response.");
            }

            var payload = new byte[length - 4];
            _output.ReadExactly(payload);
            return payload;
        }
    }

    /// <summary>Passes writes through, counting them and keeping the first <paramref name="keep"/> bytes.</summary>
    private sealed class CountingStream(Stream inner, int keep) : Stream
    {
        private readonly MemoryStream _start = new();
        public long Written { get; private set; }

        /// <summary>Whether everything written is exactly <paramref name="expected"/>.</summary>
        public bool StartsWith(MemoryStream expected) =>
            Written == expected.Length && _start.ToArray().AsSpan().SequenceEqual(expected.ToArray());

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            if (_start.Length < keep)
            {
                _start.Write(buffer, offset, (int)Math.Min(count, keep - _start.Length));
            }

            Written += count;
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProjectDoorango;

public record RepositoryFile(string Path, long Size, string Sha);
public record RepositorySnapshot(string Commit, string Url, RepositoryFile[] Files)
{
    public string Revision => Commit[..7];
    public long ClientSize => Files.Where(f => f.Path.StartsWith("game/", StringComparison.Ordinal)).Sum(f => f.Size);
}
public record TransferProgress(string Message, double Percent);
public record Preferences(string Gateway = "http://127.0.0.1:8190", bool AutoStart = true);

public sealed class LauncherService : IDisposable
{
    public const string Repository = "Sksandeep144/ProjectDoorango";
    readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly Action<string> log;
    Process? server;
    public string Root { get; }
    public string Game => Path.Combine(Root, "game");
    public string Server => Path.Combine(Root, "server");
    public string State => Path.Combine(Root, ".launcher");
    public bool GameInstalled => File.Exists(Path.Combine(Game, "DurangoV2.exe")) &&
        File.Exists(Path.Combine(Game, "UnityPlayer.dll")) && Directory.Exists(Path.Combine(Game, "DurangoV2_Data"));
    public bool ServerInstalled => File.Exists(Path.Combine(Server, "DurangoServer.csproj"));
    public bool OwnsServer => server is { HasExited: false };
    public bool GameRunning
    {
        get
        {
            foreach (var process in Process.GetProcessesByName("DurangoV2"))
                using (process)
                {
                    try { if (string.Equals(process.MainModule?.FileName, Path.Combine(Game, "DurangoV2.exe"), StringComparison.OrdinalIgnoreCase)) return true; }
                    catch (System.ComponentModel.Win32Exception) { return true; }
                }
            return false;
        }
    }

    public LauncherService(string root, Action<string> log)
    {
        Root = Path.GetFullPath(root);
        this.log = log;
        Directory.CreateDirectory(State);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectDoorango-Launcher/0.1");
    }

    public Preferences LoadPreferences()
    {
        Preferences prefs = new();
        string path = Path.Combine(State, "settings.json");
        if (File.Exists(path))
            try { prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? prefs; }
            catch (JsonException) { log("Saved launcher settings could not be read. Using defaults."); }
        string config = Path.Combine(Game, "offserver.txt");
        if (File.Exists(config))
            foreach (string line in File.ReadLines(config))
                if (line.TrimStart().StartsWith("gateway=", StringComparison.OrdinalIgnoreCase))
                {
                    prefs = prefs with { Gateway = line[(line.IndexOf('=') + 1)..].Trim() };
                    break;
                }
        return prefs;
    }

    public static Uri ParseGateway(string gateway)
    {
        if (!Uri.TryCreate(gateway.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException("Enter a server address such as http://127.0.0.1:8190, without a path or login details.");
        return uri;
    }

    public static bool CanStartLocal(string gateway)
    {
        var uri = ParseGateway(gateway);
        return uri.IsLoopback && uri.Scheme == "http" && uri.Port != 8191 && uri.Host != "[::1]";
    }

    public void SavePreferences(Preferences prefs)
    {
        string gateway = ParseGateway(prefs.Gateway).GetLeftPart(UriPartial.Authority);
        Directory.CreateDirectory(Game);
        string config = Path.Combine(Game, "offserver.txt");
        string original = File.Exists(config) ? File.ReadAllText(config) : "# ProjectDoorango connection\nname=ProjectDoorango\n";
        AtomicWrite(config, UpdateGateway(original, gateway));
        AtomicWrite(Path.Combine(State, "settings.json"), JsonSerializer.Serialize(prefs with { Gateway = gateway }));
    }

    public static string UpdateGateway(string content, string gateway)
    {
        ParseGateway(gateway);
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        lines.RemoveAll(l => l.TrimStart().StartsWith("gateway=", StringComparison.OrdinalIgnoreCase));
        lines.Add("gateway=" + gateway.TrimEnd('/'));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    static void AtomicWrite(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, true);
    }

    public string InstalledVersion()
    {
        string path = Path.Combine(State, "client-version.txt");
        return GameInstalled ? (File.Exists(path) ? File.ReadAllText(path).Trim() : "Existing installation") : "Not installed";
    }

    public async Task<bool> IsOnlineAsync(string gateway, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await http.GetAsync(new Uri(ParseGateway(gateway), "knock"), timeout.Token);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return json.RootElement.TryGetProperty("server_version", out _);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return false; }
    }

    async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await http.GetAsync(url, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub returned {(int)response.StatusCode}. Check your connection or try again later (API limits may apply).");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
    }

    public async Task<RepositorySnapshot> LatestSnapshotAsync(CancellationToken token)
    {
        using var commit = await GetJsonAsync($"https://api.github.com/repos/{Repository}/commits/main", token);
        string sha = commit.RootElement.GetProperty("sha").GetString()!;
        string treeSha = commit.RootElement.GetProperty("commit").GetProperty("tree").GetProperty("sha").GetString()!;
        if (sha.Length != 40 || !sha.All(Uri.IsHexDigit) || treeSha.Length != 40 || !treeSha.All(Uri.IsHexDigit))
            throw new IOException("Invalid repository revision.");
        using var tree = await GetJsonAsync($"https://api.github.com/repos/{Repository}/git/trees/{treeSha}?recursive=1", token);
        if (tree.RootElement.GetProperty("truncated").GetBoolean())
            throw new IOException("GitHub returned an incomplete file list. Please try again later.");
        var files = new List<RepositoryFile>();
        foreach (var entry in tree.RootElement.GetProperty("tree").EnumerateArray())
        {
            string path = entry.GetProperty("path").GetString()!;
            if (entry.GetProperty("type").GetString() != "blob" ||
                !(path.StartsWith("game/", StringComparison.Ordinal) || path.StartsWith("server/", StringComparison.Ordinal))) continue;
            if (entry.GetProperty("mode").GetString() is not ("100644" or "100755"))
                throw new IOException("Repository contains unsupported file links.");
            string blob = entry.GetProperty("sha").GetString()!;
            if (blob.Length != 40 || !blob.All(Uri.IsHexDigit)) throw new IOException("Invalid repository file hash.");
            files.Add(new(path, entry.GetProperty("size").GetInt64(), blob));
        }
        if (!files.Any(f => f.Path == "game/DurangoV2.exe") || !files.Any(f => f.Path == "server/DurangoServer.csproj"))
            throw new IOException("ProjectDoorango does not contain both the client and server files.");
        return new(sha, $"https://codeload.github.com/{Repository}/zip/{sha}", files.ToArray());
    }

    public async Task DownloadAsync(string url, string path, long expectedSize, string? digest,
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        long total = expectedSize > 0 ? expectedSize : response.Content.Headers.ContentLength ?? 0;
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (total > 0 && drive.AvailableFreeSpace < total + 256L * 1024 * 1024)
            throw new IOException("Not enough free disk space for this download.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
        byte[] buffer = new byte[131072];
        long received = 0;
        var watch = Stopwatch.StartNew();
        long lastReport = -1000;
        while (true)
        {
            using var stalled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            stalled.CancelAfter(TimeSpan.FromSeconds(45));
            int count = await input.ReadAsync(buffer, stalled.Token);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            hash.AppendData(buffer, 0, count);
            received += count;
            if (total > 0 && received > total) throw new IOException("Download exceeded its published size.");
            if (watch.ElapsedMilliseconds - lastReport > 150)
            {
                lastReport = watch.ElapsedMilliseconds;
                double rate = received / Math.Max(1, watch.Elapsed.TotalSeconds);
                string amount = total > 0 ? $"{received / 1048576d:F1} / {total / 1048576d:F1} MB" : $"{received / 1048576d:F1} MB";
                progress.Report(new($"Downloading · {amount} · {rate / 1048576d:F1} MB/s", total > 0 ? received * 75d / total : -1));
            }
        }
        if (expectedSize > 0 && received != expectedSize) throw new IOException("The download is incomplete. Please retry.");
        string actual = Convert.ToHexString(hash.GetHashAndReset());
        if (digest != null && !actual.Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new IOException("SHA-256 verification failed. Your installation has not been changed.");
        progress.Report(new(digest == null ? "Download complete" : "SHA-256 verified", 76));
    }

    public static async Task ExtractAsync(string archivePath, string destination, IProgress<TransferProgress> progress, CancellationToken token, string? entryPrefix = null)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 60000) throw new IOException("Archive contains too many files.");
        var selected = archive.Entries.Where(e => entryPrefix == null || e.FullName.StartsWith(entryPrefix, StringComparison.Ordinal)).ToArray();
        long size = selected.Sum(e => e.Length);
        if (size > 20L * 1024 * 1024 * 1024) throw new IOException("Archive expands beyond the installation limit.");
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!).AvailableFreeSpace < size + 256L * 1024 * 1024)
            throw new IOException("Not enough free disk space to unpack this download.");
        Directory.CreateDirectory(destination);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            string target = SafeArchivePath(destination, entry.FullName);
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new IOException("Archive links are not supported.");
            if (!names.Add(target)) throw new IOException("Archive contains duplicate file paths.");
        }
        long done = 0;
        foreach (var entry in selected)
        {
            token.ThrowIfCancellationRequested();
            string target = SafeArchivePath(destination, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = File.Create(target);
            await input.CopyToAsync(output, token);
            done += entry.Length;
            progress.Report(new("Unpacking · " + entry.Name, 76 + (size == 0 ? 0 : 22d * done / size)));
        }
    }

    public static string SafeArchivePath(string root, string name)
    {
        string normalized = name.Replace('\\', '/');
        foreach (string part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                part.EndsWith(' ') || part.EndsWith('.') || stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
                throw new IOException("Archive contains an unsafe path.");
        }
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(prefix, normalized));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Archive path escapes its installation folder.");
        return path;
    }

    public async Task InstallClientAsync(RepositorySnapshot snapshot, Preferences prefs, IProgress<TransferProgress> progress, CancellationToken token)
    {
        if (GameRunning) throw new InvalidOperationException("Close the game before installing client files.");
        string job = NewJob();
        try
        {
            string stagedGame = await PreparePackageAsync(snapshot, "game", job, progress, token);
            if (!File.Exists(Path.Combine(stagedGame, "DurangoV2.exe"))) throw new IOException("Client archive is missing the game executable.");
            if (!File.Exists(Path.Combine(stagedGame, "UnityPlayer.dll")) || !Directory.Exists(Path.Combine(stagedGame, "DurangoV2_Data")))
                throw new IOException("The client archive is missing required Unity files.");
            token.ThrowIfCancellationRequested();
            if (GameRunning) throw new InvalidOperationException("Close the game before replacing its files.");
            string config = Path.Combine(Game, "offserver.txt");
            string preserved = File.Exists(config) ? File.ReadAllText(config) : "name=ProjectDoorango\n";
            File.WriteAllText(Path.Combine(stagedGame, "offserver.txt"), UpdateGateway(preserved, prefs.Gateway));
            InstallDirectory(stagedGame, Game, Path.Combine(State, "backups"));
            AtomicWrite(Path.Combine(State, "client-version.txt"), snapshot.Commit);
            log("ProjectDoorango client @ " + snapshot.Revision + " installed. Previous installation retained in backups.");
            progress.Report(new("Client ready", 100));
        }
        finally { DeleteJob(job); }
    }

    public static void InstallDirectory(string source, string target, string backupRoot)
    {
        Directory.CreateDirectory(backupRoot);
        string backup = Path.Combine(backupRoot, "game-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        bool previous = Directory.Exists(target);
        if (previous) Directory.Move(target, backup);
        try { Directory.Move(source, target); }
        catch { if (previous) Directory.Move(backup, target); throw; }
    }

    public async Task InstallServerAsync(IProgress<TransferProgress> progress, CancellationToken token, RepositorySnapshot? snapshot = null)
    {
        if (Directory.Exists(Server)) throw new InvalidOperationException("A server folder already exists. It is kept intact to protect local changes.");
        string job = NewJob();
        try
        {
            snapshot ??= await LatestSnapshotAsync(token);
            string staged = await PreparePackageAsync(snapshot, "server", job, progress, token);
            if (!File.Exists(Path.Combine(staged, "DurangoServer.csproj")) || !Directory.Exists(Path.Combine(staged, "data", "terrains")))
                throw new IOException("The repository archive has no complete server package.");
            token.ThrowIfCancellationRequested();
            Directory.Move(staged, Server);
            log("Server source and assets installed from ProjectDoorango @ " + snapshot.Revision);
            progress.Report(new("Server files ready · runtime installs on first start", 100));
        }
        finally { DeleteJob(job); }
    }

    async Task<string> PreparePackageAsync(RepositorySnapshot snapshot, string folder, string job,
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        if (snapshot.Commit.Length != 40 || !snapshot.Commit.All(Uri.IsHexDigit)) throw new IOException("Invalid commit identifier.");
        string cache = Path.Combine(State, "downloads");
        Directory.CreateDirectory(cache);
        string zip = Path.Combine(cache, snapshot.Commit + ".zip");
        if (!File.Exists(zip))
        {
            string partial = Path.Combine(job, "repository.zip");
            await DownloadAsync(snapshot.Url, partial, 0, null, progress, token);
            File.Move(partial, zip, true);
        }
        else log("Reusing downloaded ProjectDoorango archive @ " + snapshot.Revision);
        string unpacked = Path.Combine(job, "unpacked");
        string package = "ProjectDoorango-" + snapshot.Commit + "/" + folder + "/";
        string staged = Path.Combine(unpacked, "ProjectDoorango-" + snapshot.Commit, folder);
        try
        {
            await Task.Run(() => ExtractAsync(zip, unpacked, progress, token, package), token);
            await Task.Run(() => VerifyFilesAsync(staged, snapshot.Files, folder, progress, token), token);
        }
        catch (InvalidDataException) { File.Delete(zip); throw; }
        return staged;
    }

    public static async Task VerifyFilesAsync(string directory, RepositoryFile[] files, string folder,
        IProgress<TransferProgress> progress, CancellationToken token)
    {
        var expected = files.Where(f => f.Path.StartsWith(folder + "/", StringComparison.Ordinal)).ToArray();
        if (expected.Length == 0 || !Directory.Exists(directory)) throw new InvalidDataException("Archive is missing the requested package.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int completed = 0;
        foreach (var file in expected)
        {
            token.ThrowIfCancellationRequested();
            string path = SafeArchivePath(directory, file.Path[(folder.Length + 1)..]);
            if (!names.Add(path) || !File.Exists(path) || new FileInfo(path).Length != file.Size)
                throw new InvalidDataException("Downloaded file is missing or has the wrong size: " + file.Path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(System.Text.Encoding.ASCII.GetBytes($"blob {file.Size}\0"));
            await using var input = File.OpenRead(path);
            byte[] buffer = new byte[131072];
            int read;
            while ((read = await input.ReadAsync(buffer, token)) != 0) hash.AppendData(buffer, 0, read);
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("File does not match the selected GitHub commit: " + file.Path);
            completed++;
            if (completed % 25 == 0 || completed == expected.Length)
                progress.Report(new($"Verifying ProjectDoorango files · {completed} / {expected.Length}", 98));
        }
        if (Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(p => !names.Contains(Path.GetFullPath(p))))
            throw new InvalidDataException("Archive contains files absent from the selected GitHub commit.");
    }

    string NewJob()
    {
        string job = Path.Combine(State, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        return job;
    }
    void DeleteJob(string job)
    {
        string prefix = Path.GetFullPath(Path.Combine(State, "staging")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(job).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid staging path.");
        try { Directory.Delete(job, true); }
        catch (IOException) { log("Temporary download files remain in " + job); }
    }

    async Task EnsureRuntimeAsync(CancellationToken token)
    {
        string runtime = Path.Combine(State, "server-runtime-v0.1.0");
        if (File.Exists(Path.Combine(runtime, "DurangoServer.exe"))) return;
        string job = NewJob();
        try
        {
            var progress = new Progress<TransferProgress>(p => log(p.Message));
            string zip = Path.Combine(job, "runtime.zip");
            await DownloadAsync("https://github.com/" + Repository + "/releases/download/v0.1.0/ProjectDoorango-Server-win-x64.zip",
                zip, 43674377, "cfcff814eaeb1baa49237cfbd4473890191869dd08187c382147655f36460207", progress, token);
            string staged = Path.Combine(job, "runtime");
            await ExtractAsync(zip, staged, progress, token);
            if (!File.Exists(Path.Combine(staged, "DurangoServer.exe")) ||
                !SupportsLauncherControl(Path.Combine(staged, "DurangoServer.dll")))
                throw new InvalidDataException("Invalid server runtime package.");
            token.ThrowIfCancellationRequested();
            Directory.Move(staged, runtime);
        }
        finally { DeleteJob(job); }
    }

    public static string DotnetPath()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        return File.Exists(path) ? path : "dotnet";
    }

    public async Task StartServerAsync(string gateway, CancellationToken token, int gamePort = 8191)
    {
        if (!CanStartLocal(gateway)) throw new InvalidOperationException("Local startup needs an HTTP localhost gateway; game port 8191 is reserved.");
        if (await IsOnlineAsync(gateway, token)) { log("A server is already online. Connecting to it; this launcher will not stop it."); return; }
        if (OwnsServer) throw new InvalidOperationException("The server process is already running. Check its activity log.");
        if (!ServerInstalled) throw new InvalidOperationException("Download the server files from Downloads first.");
        bool development = Directory.Exists(Path.Combine(Root, ".git"));
        string assembly;
        string executable;
        if (!development)
        {
            await EnsureRuntimeAsync(token);
            assembly = Path.Combine(State, "server-runtime-v0.1.0", "DurangoServer.dll");
            executable = Path.Combine(State, "server-runtime-v0.1.0", "DurangoServer.exe");
        }
        else
        {
        log("Building the local server…");
        var build = MakeProcess(DotnetPath(), Server, "build", "DurangoServer.csproj", "-c", "Release", "--nologo", "-v", "quiet");
        using (build)
        {
            try { build.Start(); }
            catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Install the .NET 9 SDK from Settings, then try again."); }
            build.BeginOutputReadLine(); build.BeginErrorReadLine();
            try { await build.WaitForExitAsync(token); }
            catch (OperationCanceledException) { if (!build.HasExited) build.Kill(true); await build.WaitForExitAsync(); throw; }
            if (build.ExitCode != 0) throw new InvalidOperationException("Server build failed. Check Activity; make sure the .NET 9 SDK is installed.");
        }
        assembly = Path.Combine(Server, "bin", "Release", "net9.0", "DurangoServer.dll");
        executable = DotnetPath();
        }
        var address = ParseGateway(gateway);
        if (!SupportsLauncherControl(assembly))
            throw new InvalidOperationException("These server files predate graceful launcher control. Update the ProjectDoorango server before starting it here; the current local preview includes the required update.");
        server?.Dispose();
        string[] arguments = [
            "--data", Path.Combine(Server, "data"), "--terrains", Path.Combine(Server, "data", "terrains"),
            "--gateway-port", address.Port.ToString(), "--game-port", gamePort.ToString(), "--public-host", "127.0.0.1", "--url-prefix", $"http://127.0.0.1:{address.Port}/"];
        server = MakeProcess(executable, Server, development ? [assembly, .. arguments] : arguments);
        server.StartInfo.RedirectStandardInput = true;
        server.Start(); server.BeginOutputReadLine(); server.BeginErrorReadLine();
        log("Waiting for the server to become ready…");
        try
        {
            for (int i = 0; i < 90; i++)
            {
                token.ThrowIfCancellationRequested();
                if (server.HasExited) throw new InvalidOperationException("Server exited during startup. Check Activity for the cause.");
                if (await IsOnlineAsync(gateway, token)) { log("Local server is ready."); return; }
                await Task.Delay(1000, token);
            }
            throw new TimeoutException("Server is still starting. Check Activity and refresh status before playing.");
        }
        catch (OperationCanceledException) { await StopServerAsync(); throw; }
    }

    Process MakeProcess(string executable, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };
        return process;
    }

    public static bool SupportsLauncherControl(string assembly)
    {
        using var stream = File.OpenRead(assembly);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (metadata.GetString(type.Namespace) != "DurangoServerNx" || metadata.GetString(type.Name) != "Program") continue;
            foreach (var fieldHandle in type.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                if (metadata.GetString(field.Name) != "LauncherControlProtocol" || field.GetDefaultValue().IsNil) continue;
                var constant = metadata.GetConstant(field.GetDefaultValue());
                return constant.TypeCode == ConstantTypeCode.Int32 && metadata.GetBlobReader(constant.Value).ReadInt32() == 1;
            }
        }
        return false;
    }

    public async Task StopServerAsync()
    {
        if (!OwnsServer) { log("No server started by this launcher is running."); return; }
        log("Saving the world and stopping the server…");
        await server!.StandardInput.WriteLineAsync("stop");
        await server.StandardInput.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await server.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new InvalidOperationException("The server did not finish saving within 30 seconds. It has not been force-stopped. Leave the launcher open and check Activity."); }
        log("Server stopped.");
    }

    public void LaunchGame()
    {
        if (!GameInstalled) throw new InvalidOperationException("Download the game client first.");
        if (GameRunning) throw new InvalidOperationException("The game is already running.");
        Process.Start(new ProcessStartInfo(Path.Combine(Game, "DurangoV2.exe")) { WorkingDirectory = Game, UseShellExecute = true });
        log("Game launched.");
    }

    public void Dispose() { http.Dispose(); server?.Dispose(); }
}


using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ProjectDoorango;

internal static class LauncherChecks
{
    public static async Task<int> Run(string reportPath)
    {
        string root = Path.Combine(Path.GetTempPath(), "DoorangoChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var messages = new List<string>();
        void Check(bool success, string name)
        {
            if (!success) throw new Exception(name);
            messages.Add("PASS " + name);
        }
        async Task Reject(Func<Task> action, string name)
        {
            try { await action(); }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
            { Check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        var progress = new InlineProgress();
        try
        {
            using var service = new LauncherService(root, _ => { });
            Directory.CreateDirectory(service.Game);
            string config = "# keep comment\r\naccount=keep-this-key\r\nname=My world\r\ngateway=http://old:80\r\ngateway=http://duplicate:80\r\n";
            File.WriteAllText(Path.Combine(service.Game, "offserver.txt"), config);
            service.SavePreferences(new("http://127.0.0.1:8190", true));
            string saved = File.ReadAllText(Path.Combine(service.Game, "offserver.txt"));
            Check(saved.Contains("account=keep-this-key") && saved.Contains("# keep comment") && saved.Contains("name=My world"), "gateway edits preserve account, comments and name");
            Check(saved.Split("gateway=").Length == 2, "duplicate gateway lines collapse to one");
            Check(service.LoadPreferences().Gateway == "http://127.0.0.1:8190", "settings round-trip");
            foreach (string address in new[] { "file:///C:/test", "https://user:password@example.com", "https://example.com/path", "not a url" })
                await Reject(() => { LauncherService.ParseGateway(address); return Task.CompletedTask; }, "reject invalid gateway " + address.Split(':')[0]);
            Check(LauncherService.CanStartLocal("http://localhost:8190") && !LauncherService.CanStartLocal("https://example.com"), "remote gateways cannot start a local server");
            foreach (string bad in new[] { "../escape.txt", "x/../../escape.txt", "C:/escape.txt", "/escape.txt", "x\\..\\escape.txt", "x:stream", "CON.txt", "dir/file. " })
                await Reject(() => { LauncherService.SafeArchivePath(root, bad); return Task.CompletedTask; }, "reject unsafe archive path " + bad);
            string zipPath = Path.Combine(root, "test.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("package/data/test.txt").Open());
                writer.Write("verified file");
            }
            string unpacked = Path.Combine(root, "unpacked");
            await LauncherService.ExtractAsync(zipPath, unpacked, progress, CancellationToken.None);
            Check(File.ReadAllText(Path.Combine(unpacked, "package/data/test.txt")) == "verified file", "valid ZIP extracts correctly");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                await Reject(() => LauncherService.ExtractAsync(zipPath, Path.Combine(root, "cancelled"), progress, cancel.Token), "cancelled extraction stops before writing files");
            }
            string evilZip = Path.Combine(root, "evil.zip");
            using (var zip = ZipFile.Open(evilZip, ZipArchiveMode.Create)) zip.CreateEntry("../escape.txt");
            await Reject(() => LauncherService.ExtractAsync(evilZip, Path.Combine(root, "evil"), progress, CancellationToken.None), "ZIP traversal rejected before extraction");
            Check(!File.Exists(Path.Combine(root, "escape.txt")), "ZIP traversal writes nothing outside staging");
            string duplicateZip = Path.Combine(root, "duplicate.zip");
            using (var zip = ZipFile.Open(duplicateZip, ZipArchiveMode.Create)) { zip.CreateEntry("a.txt"); zip.CreateEntry("A.txt"); }
            await Reject(() => LauncherService.ExtractAsync(duplicateZip, Path.Combine(root, "dupes"), progress, CancellationToken.None), "case-insensitive duplicate ZIP paths rejected");

            string old = Path.Combine(root, "old");
            string fresh = Path.Combine(root, "fresh");
            string backups = Path.Combine(root, "backups");
            Directory.CreateDirectory(old); Directory.CreateDirectory(fresh);
            File.WriteAllText(Path.Combine(old, "old.txt"), "old");
            File.WriteAllText(Path.Combine(fresh, "new.txt"), "new");
            LauncherService.InstallDirectory(fresh, old, backups);
            Check(File.Exists(Path.Combine(old, "new.txt")) && Directory.GetFiles(backups, "old.txt", SearchOption.AllDirectories).Length == 1, "installation retains previous client backup");
            await Reject(() => { LauncherService.InstallDirectory(Path.Combine(root, "missing"), old, backups); return Task.CompletedTask; }, "failed folder swap reports failure");
            Check(File.Exists(Path.Combine(old, "new.txt")), "failed folder swap rolls back original installation");

            byte[] payload = Encoding.UTF8.GetBytes("verified download contents");
            string digest = Convert.ToHexString(SHA256.HashData(payload));
            async Task ServeFixture(byte[] body, Func<string, Task> use)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task response = Task.Run(async () =>
                {
                    using var socket = await listener.AcceptTcpClientAsync();
                    await using var stream = socket.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                    while (await reader.ReadLineAsync() is { Length: > 0 }) { }
                    byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header); await stream.WriteAsync(body);
                });
                try { await use($"http://127.0.0.1:{port}/file"); }
                finally { await response; listener.Stop(); }
            }
            Task DownloadFixture(string expectedDigest) => ServeFixture(payload, url => service.DownloadAsync(url, Path.Combine(root, "download.bin"), payload.Length, expectedDigest, progress, CancellationToken.None));
            await DownloadFixture(digest);
            Check(File.ReadAllBytes(Path.Combine(root, "download.bin")).SequenceEqual(payload), "download verifies expected size and SHA-256");
            await Reject(() => DownloadFixture(new string('0', 64)), "corrupt checksum blocks installation");
            string clientZip = Path.Combine(root, "client.zip");
            string revision = new string('a', 40);
            var fixture = new Dictionary<string, string>
            {
                ["game/DurangoV2.exe"] = "fixture",
                ["game/UnityPlayer.dll"] = "fixture",
                ["game/DurangoV2_Data/settings.txt"] = "fixture",
                ["game/offserver.txt"] = "account=do-not-use-this\ngateway=http://wrong:80",
                ["server/DurangoServer.csproj"] = "fixture project",
                ["server/data/terrains/test.zip"] = "fixture terrain"
            };
            using (var zip = ZipFile.Open(clientZip, ZipArchiveMode.Create))
                foreach (var (path, text) in fixture)
                {
                    using var writer = new StreamWriter(zip.CreateEntry($"ProjectDoorango-{revision}/{path}").Open());
                    writer.Write(text);
                }
            byte[] clientBytes = File.ReadAllBytes(clientZip);
            RepositoryFile[] repoFiles = fixture.Select(pair =>
            {
                byte[] bytes = Encoding.UTF8.GetBytes(pair.Value);
                byte[] blob = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0").Concat(bytes).ToArray();
                return new RepositoryFile(pair.Key, bytes.Length, Convert.ToHexString(SHA1.HashData(blob)));
            }).ToArray();
            RepositorySnapshot snapshot = new(revision, "http://127.0.0.1:1/not-used-when-cached", repoFiles);
            await ServeFixture(clientBytes, url => service.InstallClientAsync(snapshot with { Url = url }, new(), progress, CancellationToken.None));
            Check(service.GameInstalled && service.InstalledVersion() == revision, "complete repository download, verify, extract and install flow");
            Check(File.ReadAllText(Path.Combine(service.Game, "offserver.txt")).Contains("account=keep-this-key"), "downloaded client config cannot replace the existing account");
            Check(Directory.GetFiles(Path.Combine(service.State, "backups"), "offserver.txt", SearchOption.AllDirectories).Length == 1, "full client installation preserves previous folder");
            Check(!Directory.EnumerateFileSystemEntries(Path.Combine(service.State, "staging")).Any(), "successful installation cleans staging");
            await service.InstallServerAsync(progress, CancellationToken.None, snapshot);
            Check(service.ServerInstalled, "server installation reuses the client archive without another download");
            Check(!Directory.Exists(Path.Combine(service.Server, "game")), "server install extracts only the server subtree");
            var corrupt = snapshot with { Files = repoFiles.Select(f => f.Path == "game/DurangoV2.exe" ? f with { Sha = new string('0', 40) } : f).ToArray() };
            await Reject(() => service.InstallClientAsync(corrupt, new(), progress, CancellationToken.None), "wrong Git blob hash blocks client replacement");
            Check(service.InstalledVersion() == revision && service.GameInstalled, "failed update retains installed version and game");
            Check(!File.Exists(Path.Combine(service.State, "downloads", revision + ".zip")), "failed Git verification removes the cached archive");
            Check(!await service.IsOnlineAsync("http://127.0.0.1:1"), "offline status returns cleanly");
            await Reject(() => service.StartServerAsync("https://example.com", CancellationToken.None), "server start rejects remote gateway");
            await service.StopServerAsync();
            Check(true, "stop without an owned server is harmless");
            messages.Add($"RESULT: {messages.Count} checks passed");
            File.WriteAllLines(reportPath, messages);
            return 0;
        }
        catch (Exception e)
        {
            messages.Add("FAIL " + e);
            File.WriteAllLines(reportPath, messages);
            return 1;
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class InlineProgress : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) { }
    }

    public static async Task<int> ServerSmoke(string projectRoot, string reportPath)
    {
        string root = Path.Combine(Path.GetTempPath(), "DoorangoServerSmoke-" + Guid.NewGuid().ToString("N"));
        var logs = new List<string>();
        using var service = new LauncherService(root, s => { lock (logs) logs.Add(s); });
        try
        {
            string source = Path.Combine(projectRoot, "server");
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj" or "AppData-nx" or "patches")) continue;
                string target = Path.Combine(service.Server, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            static int Port()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
            }
            int gatewayPort = Port(), gamePort;
            do { gamePort = Port(); } while (gatewayPort == gamePort);
            string gateway = "http://127.0.0.1:" + gatewayPort;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await service.StartServerAsync(gateway, timeout.Token, gamePort);
            if (!service.OwnsServer || !await service.IsOnlineAsync(gateway)) throw new Exception("Readiness failed");
            logs.Add("PASS real isolated server startup and readiness");
            await service.StopServerAsync();
            if (service.OwnsServer || await service.IsOnlineAsync(gateway)) throw new Exception("Server still running");
            if (!logs.Any(s => s.Contains("launcher stop"))) throw new Exception("Graceful shutdown path was not reached");
            logs.Add("PASS real server graceful stop and port release");
            var latest = await service.LatestSnapshotAsync(timeout.Token);
            if (latest.ClientSize <= 0 || latest.Files.Length < 2 || latest.Commit.Length != 40) throw new Exception("Invalid live repository snapshot");
            logs.Add($"PASS live ProjectDoorango snapshot: {latest.Revision}, {latest.Files.Length} verified-file entries");
            File.WriteAllLines(reportPath, logs);
            return 0;
        }
        catch (Exception e) { lock (logs) logs.Add("FAIL " + e); File.WriteAllLines(reportPath, logs); return 1; }
        finally
        {
            if (service.OwnsServer) await service.StopServerAsync();
            Directory.Delete(root, true);
        }
    }

    public static async Task<int> DownloadSmoke(string projectRoot, string reportPath)
    {
        string root = Path.Combine(projectRoot, ".launcher", "download-check-" + Guid.NewGuid().ToString("N"));
        var gate = new object();
        void Log(string text) { lock (gate) File.AppendAllText(reportPath, text + Environment.NewLine); }
        File.WriteAllText(reportPath, "Live ProjectDoorango download / installation check\n");
        using var service = new LauncherService(root, Log);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var snapshot = await service.LatestSnapshotAsync(timeout.Token);
            Log($"Snapshot {snapshot.Commit}: {snapshot.Files.Count(f => f.Path.StartsWith("game/"))} client files");
            var progress = new FileProgress(Log);
            await service.InstallClientAsync(snapshot, new(), progress, timeout.Token);
            if (!service.GameInstalled || service.InstalledVersion() != snapshot.Commit) throw new Exception("Client installation did not complete");
            Log("PASS full live GitHub client archive downloaded, every client file verified, and installed in isolation");
            await service.InstallServerAsync(progress, timeout.Token, snapshot);
            if (!service.ServerInstalled) throw new Exception("Server installation did not complete");
            Log("PASS server installed from the same cached archive with per-file verification");
            string cache = Path.Combine(projectRoot, ".launcher", "downloads");
            Directory.CreateDirectory(cache);
            File.Move(Path.Combine(service.State, "downloads", snapshot.Commit + ".zip"), Path.Combine(cache, snapshot.Commit + ".zip"), true);
            Log("PASS verified archive retained in launcher cache for future installs");
            return 0;
        }
        catch (Exception e) { Log("FAIL " + e); return 1; }
        finally { Directory.Delete(root, true); }
    }

    sealed class FileProgress(Action<string> log) : IProgress<TransferProgress>
    {
        DateTime last;
        public void Report(TransferProgress p)
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 2) return;
            last = DateTime.UtcNow;
            log(p.Message);
        }
    }
}

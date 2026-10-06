using LmpCommon.Enums;
using LmpCommon.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Definition;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace HeadlessTest.Infrastructure
{
    /// <summary>Owns one isolated real server. No live configuration or universe is copied.</summary>
    internal sealed class ServerProcess : IAsyncDisposable
    {
        private readonly TestContext _context;
        private readonly Queue<string> _tail = new Queue<string>();
        private readonly object _tailLock = new object();
        private readonly CancellationTokenSource _drainCancellation = new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _artifacts;
        private Process _process;
        private Task _stdout = Task.CompletedTask;
        private Task _stderr = Task.CompletedTask;
        private bool _disposed;
        public int Port { get; private set; }
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LMPHeadless_" + Guid.NewGuid().ToString("N"));
        public string LogTail { get { lock (_tailLock) return string.Join(Environment.NewLine, _tail); } }

        private ServerProcess(TestContext context)
        {
            _context = context;
            _artifacts = Path.Combine(context.TestRunResultsDirectory ?? Path.Combine(Path.GetTempPath(), "LMPHeadlessResults"), "headless-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_artifacts);
        }

        public static async Task<ServerProcess> StartAsync(TestContext context, CancellationToken cancellationToken = default, Action<GeneralSettingsDefinition> configureGeneral = null)
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var server = new ServerProcess(context);
                try
                {
                    await server.StartCoreAsync(cancellationToken, configureGeneral);
                    return server;
                }
                catch (Exception e)
                {
                    var collision = server.LogTail.Contains($"Port {server.Port} is already in use", StringComparison.Ordinal)
                        || server.LogTail.Contains("SocketException (10048)", StringComparison.Ordinal);
                    try { await server.DisposeAsync(); }
                    catch (Exception cleanup) { context.WriteLine("Startup cleanup failed: " + cleanup); throw new AggregateException(e, cleanup); }
                    if (!collision || attempt == 3 || cancellationToken.IsCancellationRequested)
                        throw new InvalidOperationException("Isolated server startup failed. " + server.LogTail, e);
                    context.WriteLine($"Verified port collision on attempt {attempt}; retrying with a fresh port and root.");
                }
            }
            throw new InvalidOperationException("Server startup attempts exhausted.");
        }

        private async Task StartCoreAsync(CancellationToken cancellationToken, Action<GeneralSettingsDefinition> configureGeneral)
        {
            var runtime = Path.Combine(AppContext.BaseDirectory, "server");
            var entry = Path.Combine(runtime, "Server.dll");
            if (!File.Exists(entry) || !File.Exists(Path.Combine(runtime, "Server.runtimeconfig.json")) || !File.Exists(Path.Combine(runtime, "Server.deps.json")))
                throw new FileNotFoundException("Server runtime staging is incomplete. Build HeadlessTest before running with --no-build.", entry);
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.Combine(Root, "Config"));
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                Port = ((IPEndPoint)socket.LocalEndPoint).Port;
            }
            WriteSettings("ConnectionSettings.xml", new ConnectionSettingsDefinition { ListenAddress = "127.0.0.1", Port = Port, Upnp = false });
            var general = new GeneralSettingsDefinition { ServerName = "Headless isolated test", GameMode = GameMode.Career, MaxPlayers = 8, Password = "", AdminPassword = "", ModControl = false, AutoDekessler = 0, AutoNuke = 0 };
            configureGeneral?.Invoke(general);
            WriteSettings("GeneralSettings.xml", general);
            WriteSettings("MasterServerSettings.xml", new MasterServerSettingsDefinition { RegisterWithMasterServer = false });
            WriteSettings("WebsiteSettings.xml", new WebsiteSettingsDefinition { EnableWebsite = false });
            WriteSettings("DebugSettings.xml", new DebugSettingsDefinition { VerboseDiagnostics = true, CustomMasterServer = "127.0.0.1:9" });
            var start = new ProcessStartInfo(ResolveDotnetHost())
            {
                WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            start.ArgumentList.Add(entry);
            start.ArgumentList.Add("--data-directory");
            start.ArgumentList.Add(Root);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated server process.");
            _context.WriteLine($"Server pid={_process.Id} root={Root} port={Port} artifacts={_artifacts}");
            _stdout = DrainAsync(_process.StandardOutput, "stdout");
            _stderr = DrainAsync(_process.StandardError, "stderr");
            var exit = _process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(_ready.Task, exit, _stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(35), cancellationToken);
            if (completed == _stdout || completed == _stderr)
            {
                await completed;
                throw new InvalidOperationException("Server output closed before readiness." + Environment.NewLine + LogTail);
            }
            if (completed == exit)
            {
                await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                ThrowIfExited();
            }
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            ThrowIfExited();
        }

        private void WriteSettings(string filename, object settings)
            => LunaXmlSerializer.WriteToXmlFile(settings, Path.Combine(Root, "Config", filename));

        private async Task DrainAsync(StreamReader reader, string stream)
        {
            using var writer = new StreamWriter(Path.Combine(_artifacts, stream + ".log")) { AutoFlush = true };
            var written = 0;
            while (true)
            {
                var line = await reader.ReadLineAsync(_drainCancellation.Token);
                if (line == null) break;
                if (line.Length > 4096) line = line.Substring(0, 4096) + " [truncated]";
                lock (_tailLock)
                {
                    _tail.Enqueue(stream + ": " + line);
                    while (_tail.Count > 200) _tail.Dequeue();
                }
                if (written < 2 * 1024 * 1024)
                {
                    await writer.WriteLineAsync(line);
                    written += line.Length + 1;
                    if (written >= 2 * 1024 * 1024) await writer.WriteLineAsync("[capture limit reached; continuing to drain]");
                }
                if (line.Contains("All systems up and running.", StringComparison.Ordinal)) _ready.TrySetResult(true);
            }
        }

        public void ThrowIfExited()
        {
            if (_process == null || _process.HasExited)
                throw new InvalidOperationException($"Isolated server exited (code={(_process?.HasExited == true ? _process.ExitCode : -1)}).{Environment.NewLine}{LogTail}");
            if (_stdout.IsFaulted || _stderr.IsFaulted)
                throw new InvalidOperationException("Server log capture failed.", _stdout.Exception ?? _stderr.Exception);
        }

        public void AttachTranscript(string name, string content)
        {
            var filename = Path.GetFileName(name);
            if (string.IsNullOrWhiteSpace(filename)) throw new ArgumentException("Transcript filename required.", nameof(name));
            var path = Path.Combine(_artifacts, filename);
            File.WriteAllText(path, content ?? "");
            _context.AddResultFile(path);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            var errors = new List<Exception>();
            var exited = _process == null;
            try
            {
                if (_process != null)
                {
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    exited = true;
                    await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception e) { errors.Add(e); }
            finally
            {
                _drainCancellation.Cancel();
                try { await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) { /* Our drain cancellation is deliberate. */ }
                catch (Exception e) { errors.Add(e); }
                _process?.Dispose();
                try
                {
                    foreach (var path in Directory.GetFiles(_artifacts)) _context.AddResultFile(path);
                    _context.WriteLine("Server tail:" + Environment.NewLine + LogTail);
                    // Root is a fresh fixture-owned GUID path; attachments are outside it.
                    // Preserve the root if process exit was not confirmed, rather than masking
                    // the termination failure with locked-file deletion errors.
                    if (exited && Directory.Exists(Root)) Directory.Delete(Root, true);
                    else if (!exited) _context.WriteLine("Server exit unconfirmed; retained root: " + Root);
                }
                catch (Exception e) { errors.Add(e); }
                _drainCancellation.Dispose();
            }
            if (errors.Count > 0) throw new AggregateException("Isolated server cleanup failed.", errors);
        }

        private static string ResolveDotnetHost()
        {
            var explicitHost = Environment.GetEnvironmentVariable("LMP_TEST_DOTNET");
            if (!string.IsNullOrWhiteSpace(explicitHost))
            {
                if (!Path.IsPathFullyQualified(explicitHost) || !File.Exists(explicitHost))
                    throw new FileNotFoundException("LMP_TEST_DOTNET must name an existing absolute .NET 10 dotnet host path.", explicitHost);
                return explicitHost;
            }
            var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
            var installation = runtime.Parent?.Parent?.Parent;
            var host = installation == null ? null : Path.Combine(installation.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (host != null && File.Exists(host)) return host;
            throw new FileNotFoundException("Cannot resolve the current .NET 10 host. Set LMP_TEST_DOTNET to its absolute executable path.");
        }
    }
}

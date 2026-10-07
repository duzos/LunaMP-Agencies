// Entry point of the agencies updater helper. It is started by the client or the server with the new build's own copy of
// this exe, waits for that process to exit (Windows locks loaded DLLs), installs the verified package and relaunches.
// Exit codes: 0 installed, 1 not installed (or bad arguments), 2 another helper already owns this target.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace LmpAgenciesUpdater
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("LmpAgenciesUpdater: " + e.Message);
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            HelperOptions options;
            try
            {
                options = HelperCommandLine.Parse(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine("LmpAgenciesUpdater: " + e.Message);
                return 1;
            }

            InstallResult result;
            try
            {
                Mutex mutex;
                if (!UpdateInstaller.TryAcquire(options.Target, out mutex)) return 2;

                try
                {
                    result = UpdateInstaller.Run(options, WaitForExit);
                }
                finally
                {
                    // released before the relaunch: a restarted server may start its own updater straight away
                    try { mutex.ReleaseMutex(); } catch (Exception) { /* nothing more to do */ }
                    mutex.Dispose();
                }
            }
            catch (Exception e)
            {
                result = new InstallResult { Success = false, Build = options.Build, Message = "Update failed: " + e.Message };
                if (!string.IsNullOrEmpty(options.Result))
                {
                    try { HelperCommandLine.WriteResult(options.Result, false, options.Build, 0, result.Message); }
                    catch (Exception) { /* the outcome is still logged below */ }
                }
            }

            Say(result.Message);

            // A server must come back whatever happened (it is unattended); a client only relaunches after an install.
            // Never after a wait timeout: the old process is still running and a second copy would fight it.
            if (!string.IsNullOrEmpty(options.Relaunch) && !result.TimedOut && (result.Success || options.Mode == HelperMode.Server))
                Relaunch(options);

            return result.Success ? 0 : 1;
        }

        /// <summary>Polls until the process is gone. False when the timeout (null = never) runs out first.</summary>
        private static bool WaitForExit(int pid, TimeSpan? timeout)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    using (var process = Process.GetProcessById(pid))
                    {
                        if (process.HasExited) return true;
                    }
                }
                catch (ArgumentException)
                {
                    return true; // no such process
                }
                catch (InvalidOperationException)
                {
                    return true; // it exited while we looked
                }

                if (timeout.HasValue && clock.Elapsed >= timeout.Value) return false;
                Thread.Sleep(500);
            }
        }

        private static void Relaunch(HelperOptions options)
        {
            try
            {
                var info = new ProcessStartInfo(options.Relaunch, options.RelaunchArgs ?? "")
                {
                    UseShellExecute = true,
                    WorkingDirectory = options.RelaunchDir ?? Path.GetDirectoryName(options.Relaunch)
                };
                Process.Start(info);
            }
            catch (Exception e)
            {
                Say("LmpAgenciesUpdater: could not relaunch " + options.Relaunch + ": " + e.Message);
            }
        }

        // Console output must never decide whether a server is relaunched.
        private static void Say(string line)
        {
            try { Console.WriteLine(line); } catch { }
        }
    }
}

// Self-contained on purpose: this file is compiled into the net48 helper exe and, as a linked file, into LmpCommon
// (netstandard2.0, net472, net10.0). Keep it C# 7.3, BCL only, with no dependency on any LmpCommon type.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LmpAgenciesUpdater
{
    public enum HelperMode
    {
        Client,
        Server
    }

    public sealed class HelperOptions
    {
        public HelperMode Mode;
        public string Zip, Sha256, Target, Extract, Backup, Result;
        public int Pid, Build;
        /// <summary>Seconds to wait for the process to exit; 0 = wait forever.</summary>
        public int WaitTimeoutSeconds;
        public string Relaunch;
        /// <summary>Raw Windows command-line string for the relaunched process (not base64: the encoding only exists on the wire).</summary>
        public string RelaunchArgs;
        public string RelaunchDir;
    }

    /// <summary>
    /// The helper's command line and result file.
    /// Forward compatibility: build N writes the arguments and helper N+1 (from the new zip) parses them. The REQUIRED flag
    /// set is frozen at agencies.2 (--mode --zip --sha256 --target --extract --backup --result --pid --build); every later
    /// flag must be optional with a default, and unknown flags are skipped.
    /// </summary>
    public static class HelperCommandLine
    {
        private const string ModeFlag = "--mode";
        private const string ZipFlag = "--zip";
        private const string Sha256Flag = "--sha256";
        private const string TargetFlag = "--target";
        private const string ExtractFlag = "--extract";
        private const string BackupFlag = "--backup";
        private const string ResultFlag = "--result";
        private const string PidFlag = "--pid";
        private const string BuildFlag = "--build";
        private const string WaitTimeoutFlag = "--wait-timeout-seconds";
        private const string RelaunchFlag = "--relaunch";
        private const string RelaunchArgsFlag = "--relaunch-args";
        private const string RelaunchDirFlag = "--relaunch-dir";

        /// <summary>
        /// Quotes one argument by the CommandLineToArgvW / C runtime rules: quote when it is empty or has a space, tab,
        /// newline, vertical tab or quote; inside the quotes double every backslash run that is followed by a quote or by the
        /// closing quote, and escape each quote with one more backslash. Anything else is returned untouched.
        /// </summary>
        public static string QuoteArg(string a)
        {
            if (a == null) a = "";
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return a;

            var sb = new StringBuilder(a.Length + 2);
            sb.Append('"');
            var i = 0;
            while (true)
            {
                var backslashes = 0;
                while (i < a.Length && a[i] == '\\') { backslashes++; i++; }

                if (i == a.Length)
                {
                    // the closing quote follows: its backslashes must not escape it
                    sb.Append('\\', backslashes * 2);
                    break;
                }

                if (a[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(a[i]);
                }
                i++;
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>QuoteArg on each argument, joined with single spaces.</summary>
        public static string JoinArgs(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            if (args == null) return "";
            foreach (var arg in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(QuoteArg(arg));
            }
            return sb.ToString();
        }

        /// <summary>
        /// The command line for the helper. Optional flags (timeout, relaunch, relaunch args and dir) are left out when
        /// they are 0, null or empty. The relaunch arguments travel as base64 of their UTF-8 bytes so that no quoting
        /// layer can mangle them.
        /// </summary>
        public static string Build(HelperOptions o)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));

            string mode;
            switch (o.Mode)
            {
                case HelperMode.Client: mode = "client"; break;
                case HelperMode.Server: mode = "server"; break;
                default: throw new ArgumentException("Unknown helper mode " + o.Mode + ".");
            }

            var sb = new StringBuilder();
            Append(sb, ModeFlag, mode);
            Append(sb, ZipFlag, Required(ZipFlag, o.Zip));
            Append(sb, Sha256Flag, Required(Sha256Flag, o.Sha256));
            Append(sb, TargetFlag, Required(TargetFlag, o.Target));
            Append(sb, ExtractFlag, Required(ExtractFlag, o.Extract));
            Append(sb, BackupFlag, Required(BackupFlag, o.Backup));
            Append(sb, ResultFlag, Required(ResultFlag, o.Result));
            Append(sb, PidFlag, o.Pid.ToString(CultureInfo.InvariantCulture));
            Append(sb, BuildFlag, o.Build.ToString(CultureInfo.InvariantCulture));

            if (o.WaitTimeoutSeconds != 0) Append(sb, WaitTimeoutFlag, o.WaitTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(o.Relaunch)) Append(sb, RelaunchFlag, o.Relaunch);
            if (!string.IsNullOrEmpty(o.RelaunchArgs)) Append(sb, RelaunchArgsFlag, Convert.ToBase64String(Encoding.UTF8.GetBytes(o.RelaunchArgs)));
            if (!string.IsNullOrEmpty(o.RelaunchDir)) Append(sb, RelaunchDirFlag, o.RelaunchDir);
            return sb.ToString();
        }

        /// <summary>
        /// The inverse of <see cref="Build"/>. Every flag takes exactly one value, so unknown flags are skipped together
        /// with their value. Throws <see cref="ArgumentException"/> for a missing or empty required flag, a flag without a
        /// value, a token that is not a flag, or a value that cannot be read; a null array throws ArgumentNullException.
        /// A repeated flag takes its last value.
        /// </summary>
        public static HelperOptions Parse(string[] argv)
        {
            if (argv == null) throw new ArgumentNullException(nameof(argv));

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < argv.Length; i += 2)
            {
                var flag = argv[i];
                if (flag == null || flag.Length <= 2 || !flag.StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Expected a --flag but found '" + flag + "'.");
                if (i + 1 >= argv.Length)
                    throw new ArgumentException("Flag " + flag + " needs a value.");
                values[flag] = argv[i + 1] ?? "";
            }

            var o = new HelperOptions();

            var mode = Required(ModeFlag, Lookup(values, ModeFlag));
            if (string.Equals(mode, "client", StringComparison.OrdinalIgnoreCase)) o.Mode = HelperMode.Client;
            else if (string.Equals(mode, "server", StringComparison.OrdinalIgnoreCase)) o.Mode = HelperMode.Server;
            else throw new ArgumentException("Unknown mode '" + mode + "'.");

            o.Zip = Required(ZipFlag, Lookup(values, ZipFlag));
            o.Sha256 = Required(Sha256Flag, Lookup(values, Sha256Flag));
            o.Target = Required(TargetFlag, Lookup(values, TargetFlag));
            o.Extract = Required(ExtractFlag, Lookup(values, ExtractFlag));
            o.Backup = Required(BackupFlag, Lookup(values, BackupFlag));
            o.Result = Required(ResultFlag, Lookup(values, ResultFlag));
            o.Pid = ParseInt(PidFlag, Required(PidFlag, Lookup(values, PidFlag)));
            o.Build = ParseInt(BuildFlag, Required(BuildFlag, Lookup(values, BuildFlag)));

            var timeout = Lookup(values, WaitTimeoutFlag);
            if (!string.IsNullOrEmpty(timeout))
            {
                o.WaitTimeoutSeconds = ParseInt(WaitTimeoutFlag, timeout);
                if (o.WaitTimeoutSeconds < 0) throw new ArgumentException("Flag " + WaitTimeoutFlag + " must not be negative.");
            }

            var relaunch = Lookup(values, RelaunchFlag);
            if (!string.IsNullOrEmpty(relaunch)) o.Relaunch = relaunch;

            var relaunchArgs = Lookup(values, RelaunchArgsFlag);
            if (!string.IsNullOrEmpty(relaunchArgs))
            {
                try
                {
                    o.RelaunchArgs = Encoding.UTF8.GetString(Convert.FromBase64String(relaunchArgs));
                }
                catch (FormatException)
                {
                    throw new ArgumentException("Flag " + RelaunchArgsFlag + " is not valid base64.");
                }
            }

            var relaunchDir = Lookup(values, RelaunchDirFlag);
            if (!string.IsNullOrEmpty(relaunchDir)) o.RelaunchDir = relaunchDir;

            return o;
        }

        /// <summary>
        /// Writes the install outcome as key=value lines (UTF-8, no BOM), replacing any earlier file. The message is
        /// flattened to one line (each CR and LF becomes a space). Throws on I/O problems; the caller decides what to do.
        /// </summary>
        public static void WriteResult(string path, bool success, int build, int previousBuild, string message)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("A result path is required.", nameof(path));

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var text = "success=" + (success ? "True" : "False") + "\n" +
                       "build=" + build.ToString(CultureInfo.InvariantCulture) + "\n" +
                       "previousBuild=" + previousBuild.ToString(CultureInfo.InvariantCulture) + "\n" +
                       "message=" + (message ?? "").Replace('\r', ' ').Replace('\n', ' ') + "\n";

            // Write beside the target and move into place so a reader never sees half a file.
            var temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        /// <summary>
        /// Reads a file written by <see cref="WriteResult"/>. False (with default outputs) when the file is missing or
        /// unreadable, or has no valid success and build. Unknown keys are ignored; a missing previousBuild reads as 0
        /// and a missing message as an empty string.
        /// </summary>
        public static bool TryReadResult(string path, out bool success, out int build, out int previousBuild, out string message)
        {
            success = false;
            build = 0;
            previousBuild = 0;
            message = null;
            if (string.IsNullOrEmpty(path)) return false;

            string text;
            try
            {
                if (!File.Exists(path)) return false;
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }

            bool? foundSuccess = null;
            int? foundBuild = null;
            var foundPrevious = 0;
            var foundMessage = "";

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                var equals = line.IndexOf('=');
                if (equals <= 0) continue;

                var key = line.Substring(0, equals).Trim();
                var value = line.Substring(equals + 1);

                if (string.Equals(key, "success", StringComparison.OrdinalIgnoreCase))
                {
                    bool parsed;
                    if (!bool.TryParse(value.Trim(), out parsed)) return false;
                    foundSuccess = parsed;
                }
                else if (string.Equals(key, "build", StringComparison.OrdinalIgnoreCase))
                {
                    int parsed;
                    if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return false;
                    foundBuild = parsed;
                }
                else if (string.Equals(key, "previousBuild", StringComparison.OrdinalIgnoreCase))
                {
                    int parsed;
                    if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return false;
                    foundPrevious = parsed;
                }
                else if (string.Equals(key, "message", StringComparison.OrdinalIgnoreCase))
                {
                    foundMessage = value;
                }
            }

            if (foundSuccess == null || foundBuild == null) return false;

            success = foundSuccess.Value;
            build = foundBuild.Value;
            previousBuild = foundPrevious;
            message = foundMessage;
            return true;
        }

        private static void Append(StringBuilder sb, string flag, string value)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(flag).Append(' ').Append(QuoteArg(value));
        }

        private static string Lookup(Dictionary<string, string> values, string flag)
        {
            string value;
            return values.TryGetValue(flag, out value) ? value : null;
        }

        private static string Required(string flag, string value)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("Required flag " + flag + " is missing or empty.");
            return value;
        }

        private static int ParseInt(string flag, string value)
        {
            int parsed;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                throw new ArgumentException("Flag " + flag + " needs a whole number but got '" + value + "'.");
            return parsed;
        }
    }
}

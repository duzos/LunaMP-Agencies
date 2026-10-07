using LmpAgenciesUpdater;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LmpCommonTest
{
    [TestClass]
    public class HelperCommandLineTest
    {
        private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string GameData = @"D:\SteamLibrary\steamapps\common\Kerbal Space Program\GameData";
        private const string ServerArgs = "-d \"D:\\My Server\\\" --x \"a \\\"b\\\"\"";

        private static HelperOptions Full(HelperMode mode = HelperMode.Client) => new HelperOptions
        {
            Mode = mode,
            Zip = @"D:\KSP\LunaMultiplayer-update\3\client.zip",
            Sha256 = Sha,
            Target = GameData,
            Extract = @"D:\KSP\LunaMultiplayer-update\3\extract",
            Backup = @"D:\KSP\LunaMultiplayer-update\backup",
            Result = @"D:\KSP\LunaMultiplayer-update\last-result.txt",
            Pid = 4242,
            Build = 3,
            WaitTimeoutSeconds = 600,
            Relaunch = @"D:\SteamLibrary\steamapps\common\Kerbal Space Program\KSP_x64.exe",
            RelaunchArgs = "-popupwindow \"-force-glcore\"",
            RelaunchDir = @"D:\SteamLibrary\steamapps\common\Kerbal Space Program"
        };

        private static HelperOptions Minimal() => new HelperOptions
        {
            Mode = HelperMode.Server,
            Zip = "z.zip", Sha256 = Sha, Target = "t", Extract = "e", Backup = "b", Result = "r.txt", Pid = 7, Build = 5
        };

        private static void AssertSame(HelperOptions e, HelperOptions a)
        {
            Assert.AreEqual(e.Mode, a.Mode);
            Assert.AreEqual(e.Zip, a.Zip);
            Assert.AreEqual(e.Sha256, a.Sha256);
            Assert.AreEqual(e.Target, a.Target);
            Assert.AreEqual(e.Extract, a.Extract);
            Assert.AreEqual(e.Backup, a.Backup);
            Assert.AreEqual(e.Result, a.Result);
            Assert.AreEqual(e.Pid, a.Pid);
            Assert.AreEqual(e.Build, a.Build);
            Assert.AreEqual(e.WaitTimeoutSeconds, a.WaitTimeoutSeconds);
            Assert.AreEqual(e.Relaunch, a.Relaunch);
            Assert.AreEqual(e.RelaunchArgs, a.RelaunchArgs);
            Assert.AreEqual(e.RelaunchDir, a.RelaunchDir);
        }

        // ---- Reference command line splitting ----

        /// <summary>
        /// Splits a command line the way CommandLineToArgvW does for every argument after the program name:
        /// 2n backslashes + quote = n backslashes and a quote toggle, 2n+1 backslashes + quote = n backslashes and a
        /// literal quote, backslashes elsewhere are literal, and a quote pair inside a quoted run is one literal quote.
        /// </summary>
        private static List<string> Split(string commandLine)
        {
            var args = new List<string>();
            var i = 0;
            var n = commandLine.Length;
            while (true)
            {
                while (i < n && (commandLine[i] == ' ' || commandLine[i] == '\t')) i++;
                if (i >= n) break;

                var sb = new StringBuilder();
                var inQuotes = false;
                while (i < n)
                {
                    var c = commandLine[i];
                    if (c == '\\')
                    {
                        var slashes = 0;
                        while (i < n && commandLine[i] == '\\') { slashes++; i++; }
                        if (i < n && commandLine[i] == '"')
                        {
                            sb.Append('\\', slashes / 2);
                            if (slashes % 2 == 1) { sb.Append('"'); i++; }
                        }
                        else sb.Append('\\', slashes);
                    }
                    else if (c == '"')
                    {
                        if (inQuotes && i + 1 < n && commandLine[i + 1] == '"') { sb.Append('"'); i += 2; }
                        else { inQuotes = !inQuotes; i++; }
                    }
                    else if (!inQuotes && (c == ' ' || c == '\t')) break;
                    else { sb.Append(c); i++; }
                }
                args.Add(sb.ToString());
            }
            return args;
        }

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>The real thing, for checking the reference splitter. Null off Windows.</summary>
        private static List<string> SplitWithWindows(string commandLine)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
            var argv = CommandLineToArgvW("prog.exe " + commandLine, out var count);
            Assert.AreNotEqual(IntPtr.Zero, argv, "CommandLineToArgvW failed");
            try
            {
                var result = new List<string>();
                for (var i = 1; i < count; i++)
                    result.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)));
                return result;
            }
            finally { LocalFree(argv); }
        }

        private static readonly string[] Nasty =
        {
            "plain", "", " ", "a b", "tab\there", "a\"b", "\"", "\"\"", "\"quoted\"", "C:\\a b\\", "C:\\a b\\\\", "C:\\a\\", "C:\\a\\\\",
            "a\\b", "a\\\\b", "a\\\"b", "a\\\\\"b", "a\\\\\\\"b", "trailing\\", "\\", "\\\\", "\\\"", "\\\\\"", "-d \"D:\\My Server\\\" --x \"a \\\"b\\\"\"",
            "e\u00e9\u65e5\u672c\u8a9e", "a=b", "--flag", "a&b|c", "100%", "x\"", "\" x", "\\\" \\\\\" \\", "mixed \\\\\\ \" \\ \"\"\" end"
        };

        // ---- QuoteArg / JoinArgs ----

        [TestMethod]
        public void QuoteArgLeavesAPlainStringUnquoted()
        {
            Assert.AreEqual("plain", HelperCommandLine.QuoteArg("plain"));
            Assert.AreEqual(@"C:\a\b", HelperCommandLine.QuoteArg(@"C:\a\b"));
            Assert.AreEqual(@"C:\a\", HelperCommandLine.QuoteArg(@"C:\a\"), "backslashes only matter before a quote");
        }

        [TestMethod]
        public void QuoteArgQuotesAnEmptyString()
        {
            Assert.AreEqual("\"\"", HelperCommandLine.QuoteArg(""));
            Assert.AreEqual("\"\"", HelperCommandLine.QuoteArg(null));
        }

        [TestMethod]
        public void QuoteArgDoublesBackslashesBeforeTheClosingQuote()
        {
            Assert.AreEqual("\"C:\\a b\\\\\"", HelperCommandLine.QuoteArg("C:\\a b\\"));
            Assert.AreEqual("\"C:\\a b\\\\\\\\\"", HelperCommandLine.QuoteArg("C:\\a b\\\\"));
        }

        [TestMethod]
        public void QuoteArgEscapesEmbeddedQuotes()
        {
            Assert.AreEqual("\"a\\\"b\"", HelperCommandLine.QuoteArg("a\"b"));
            Assert.AreEqual("\"a\\\\\\\"b\"", HelperCommandLine.QuoteArg("a\\\"b"), "a backslash before a quote is doubled, then the quote escaped");
            Assert.AreEqual("\"\\\"\"", HelperCommandLine.QuoteArg("\""));
        }

        [TestMethod]
        public void QuoteArgQuotesSpacesAndTabs()
        {
            Assert.AreEqual("\"a b\"", HelperCommandLine.QuoteArg("a b"));
            Assert.AreEqual("\"a\tb\"", HelperCommandLine.QuoteArg("a\tb"));
        }

        [TestMethod]
        public void QuoteArgOutputParsesBackToTheInputUnderTheReferenceRules()
        {
            foreach (var arg in Nasty)
            {
                var split = Split(HelperCommandLine.QuoteArg(arg));
                Assert.AreEqual(1, split.Count, "one argument for <" + arg + ">");
                Assert.AreEqual(arg, split[0], "round trip of <" + arg + ">");
            }
        }

        [TestMethod]
        public void QuoteArgOutputParsesBackToTheInputUnderCommandLineToArgvW()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return; // the reference splitter tests above still run everywhere
            foreach (var arg in Nasty)
            {
                var quoted = HelperCommandLine.QuoteArg(arg);
                var split = SplitWithWindows(quoted);
                Assert.AreEqual(1, split.Count, "one argument for <" + arg + ">");
                Assert.AreEqual(arg, split[0], "round trip of <" + arg + ">");
            }
        }

        [TestMethod]
        public void ReferenceSplitterAgreesWithCommandLineToArgvW()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var samples = new List<string>
            {
                "a b c", "  a   b  ", "\"a b\" c", "\"\" x", "a\"b c\"d", "\\\\\\\"x", "\"C:\\a b\\\\\" next", "a \"\" b",
                "\"a \\\"quoted\\\" word\" z", "\"abc\"\"def\"", "one\ttwo", "\"unterminated", "back\\\\ slash"
            };
            foreach (var s in Nasty) samples.Add(HelperCommandLine.QuoteArg(s) + " " + HelperCommandLine.QuoteArg(s));
            foreach (var s in samples)
                CollectionAssert.AreEqual(SplitWithWindows(s), Split(s), "<" + s + ">");
        }

        [TestMethod]
        public void JoinArgsQuotesEachAndSeparatesWithASpace()
        {
            Assert.AreEqual("a \"b c\" \"\" \"d\\\"e\"", HelperCommandLine.JoinArgs(new[] { "a", "b c", "", "d\"e" }));
            Assert.AreEqual("", HelperCommandLine.JoinArgs(new string[0]));
            Assert.AreEqual("", HelperCommandLine.JoinArgs(null));
        }

        [TestMethod]
        public void JoinArgsRoundTripsThroughTheReferenceSplitter()
        {
            var joined = HelperCommandLine.JoinArgs(Nasty);
            CollectionAssert.AreEqual(Nasty, Split(joined));
            var real = SplitWithWindows(joined);
            if (real != null) CollectionAssert.AreEqual(Nasty, real);
        }

        // ---- Build / Parse ----

        [TestMethod]
        public void BuildThenParseRoundTripsEveryField()
        {
            foreach (var mode in new[] { HelperMode.Client, HelperMode.Server })
            {
                var o = Full(mode);
                var line = HelperCommandLine.Build(o);
                AssertSame(o, HelperCommandLine.Parse(Split(line).ToArray()));
            }
        }

        [TestMethod]
        public void BuildThenParseRoundTripsUnderCommandLineToArgvW()
        {
            var real = SplitWithWindows(HelperCommandLine.Build(Full()));
            if (real == null) return;
            AssertSame(Full(), HelperCommandLine.Parse(real.ToArray()));
        }

        [TestMethod]
        public void BuildWritesTheDocumentedFlags()
        {
            var args = Split(HelperCommandLine.Build(Full()));
            Assert.AreEqual("--mode", args[0]);
            Assert.AreEqual("client", args[1]);
            foreach (var flag in new[] { "--zip", "--sha256", "--target", "--extract", "--backup", "--result", "--pid", "--build", "--wait-timeout-seconds", "--relaunch", "--relaunch-args", "--relaunch-dir" })
                Assert.IsTrue(args.Contains(flag), flag);
            Assert.AreEqual("server", Split(HelperCommandLine.Build(Full(HelperMode.Server)))[1]);
            Assert.AreEqual("4242", args[args.IndexOf("--pid") + 1]);
            Assert.AreEqual("3", args[args.IndexOf("--build") + 1]);
            Assert.AreEqual("600", args[args.IndexOf("--wait-timeout-seconds") + 1]);
        }

        [TestMethod]
        public void TargetWithSpacesAndRelaunchArgsWithQuotesAndTrailingBackslashSurvive()
        {
            var o = Full();
            o.Target = GameData;
            o.RelaunchArgs = ServerArgs;
            var args = Split(HelperCommandLine.Build(o));

            Assert.AreEqual(GameData, args[args.IndexOf("--target") + 1]);

            var encoded = args[args.IndexOf("--relaunch-args") + 1];
            Assert.AreEqual(ServerArgs, Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), "the flag carries base64 of the raw string");

            var parsed = HelperCommandLine.Parse(args.ToArray());
            Assert.AreEqual(GameData, parsed.Target);
            Assert.AreEqual(ServerArgs, parsed.RelaunchArgs);
        }

        [TestMethod]
        public void RelaunchArgsBuiltWithJoinArgsSplitBackIntoTheOriginalArguments()
        {
            var original = new[] { "-d", "D:\\My Server\\", "--x", "a \"b\"" };
            var o = Full();
            o.RelaunchArgs = HelperCommandLine.JoinArgs(original);
            var parsed = HelperCommandLine.Parse(Split(HelperCommandLine.Build(o)).ToArray());
            CollectionAssert.AreEqual(original, Split(parsed.RelaunchArgs));
            var real = SplitWithWindows(parsed.RelaunchArgs);
            if (real != null) CollectionAssert.AreEqual(original, real);
        }

        [TestMethod]
        public void RelaunchArgsAreEncodedAsUtf8()
        {
            var o = Full();
            o.RelaunchArgs = "-name \"\u00e9\u65e5\u672c\u8a9e\"";
            var args = Split(HelperCommandLine.Build(o));
            var encoded = args[args.IndexOf("--relaunch-args") + 1];
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(o.RelaunchArgs), Convert.FromBase64String(encoded));
            Assert.AreEqual(o.RelaunchArgs, HelperCommandLine.Parse(args.ToArray()).RelaunchArgs);
        }

        [TestMethod]
        public void OptionalFlagsAreOmittedWhenEmptyAndParseToDefaults()
        {
            var line = HelperCommandLine.Build(Minimal());
            foreach (var flag in new[] { "--relaunch", "--relaunch-args", "--relaunch-dir", "--wait-timeout-seconds" })
                Assert.IsFalse(Split(line).Contains(flag), flag);

            var o = Minimal();
            o.Relaunch = ""; o.RelaunchArgs = ""; o.RelaunchDir = null; o.WaitTimeoutSeconds = 0;
            Assert.AreEqual(line, HelperCommandLine.Build(o), "empty and null are the same as absent");

            var parsed = HelperCommandLine.Parse(Split(line).ToArray());
            AssertSame(Minimal(), parsed);
            Assert.IsNull(parsed.Relaunch);
            Assert.IsNull(parsed.RelaunchArgs);
            Assert.IsNull(parsed.RelaunchDir);
            Assert.AreEqual(0, parsed.WaitTimeoutSeconds);
        }

        [TestMethod]
        public void BuildRefusesMissingRequiredValues()
        {
            Assert.ThrowsException<ArgumentNullException>(() => HelperCommandLine.Build(null));
            foreach (var clear in new Action<HelperOptions>[]
            {
                o => o.Zip = null, o => o.Sha256 = "", o => o.Target = null, o => o.Extract = "", o => o.Backup = null, o => o.Result = ""
            })
            {
                var o = Minimal();
                clear(o);
                Assert.ThrowsException<ArgumentException>(() => HelperCommandLine.Build(o));
            }
        }

        [DataTestMethod]
        [DataRow("--mode")]
        [DataRow("--zip")]
        [DataRow("--sha256")]
        [DataRow("--target")]
        [DataRow("--extract")]
        [DataRow("--backup")]
        [DataRow("--result")]
        [DataRow("--pid")]
        [DataRow("--build")]
        public void ParseThrowsWhenARequiredFlagIsMissing(string flag)
        {
            var args = Split(HelperCommandLine.Build(Minimal()));
            var at = args.IndexOf(flag);
            Assert.IsTrue(at >= 0, flag);
            args.RemoveRange(at, 2);
            Assert.ThrowsException<ArgumentException>(() => HelperCommandLine.Parse(args.ToArray()));
        }

        [TestMethod]
        public void ParseThrowsOnMalformedInput()
        {
            Assert.ThrowsException<ArgumentNullException>(() => HelperCommandLine.Parse(null));
            Assert.ThrowsException<ArgumentException>(() => HelperCommandLine.Parse(new string[0]));

            var good = Split(HelperCommandLine.Build(Minimal()));

            var dangling = new List<string>(good) { "--future-flag" };
            Assert.ThrowsException<ArgumentException>(() => HelperCommandLine.Parse(dangling.ToArray()), "a flag without a value");

            var positional = new List<string>(good) { "stray" };
            positional.Add("value");
            Assert.ThrowsException<ArgumentException>(() => HelperCommandLine.Parse(positional.ToArray()), "a token in flag position that is not a flag");

            var badMode = new List<string>(good);
            badMode[badMode.IndexOf("--mode") + 1] = "both";
            Assert.ThrowsException<ArgumentException>(() => HelperCommandLine.Parse(badMode.ToArray()));

            foreach (var flag in new[] { "--pid", "--build" })
            {
                var badInt = new List<string>(good);
                badInt[badInt.IndexOf(flag) + 1] = "twelve";
                Assert.ThrowsException<ArgumentException>(() => HelperCommandLine.Parse(badInt.ToArray()), flag);
            }

            var badTimeout = new List<string>(good) { "--wait-timeout-seconds", "-5" };
            Assert.ThrowsException<ArgumentException>(() => HelperCommandLine.Parse(badTimeout.ToArray()), "a negative timeout");

            var badBase64 = new List<string>(good) { "--relaunch-args", "not base64!" };
            Assert.ThrowsException<ArgumentException>(() => HelperCommandLine.Parse(badBase64.ToArray()));

            var emptyRequired = new List<string>(good);
            emptyRequired[emptyRequired.IndexOf("--zip") + 1] = "";
            Assert.ThrowsException<ArgumentException>(() => HelperCommandLine.Parse(emptyRequired.ToArray()), "an empty required value counts as missing");
        }

        [TestMethod]
        public void ParseIsCaseInsensitiveForTheMode()
        {
            var args = Split(HelperCommandLine.Build(Minimal()));
            args[args.IndexOf("--mode") + 1] = "SERVER";
            Assert.AreEqual(HelperMode.Server, HelperCommandLine.Parse(args.ToArray()).Mode);
        }

        [TestMethod]
        public void ParseSkipsAnUnknownFlagAndItsValue()
        {
            var good = Split(HelperCommandLine.Build(Full()));

            var atEnd = new List<string>(good) { "--future-flag", "x" };
            AssertSame(Full(), HelperCommandLine.Parse(atEnd.ToArray()));

            var atStart = new List<string> { "--future-flag", "x" };
            atStart.AddRange(good);
            AssertSame(Full(), HelperCommandLine.Parse(atStart.ToArray()));

            var inMiddle = new List<string>(good);
            inMiddle.InsertRange(4, new[] { "--future-flag", "x" });
            AssertSame(Full(), HelperCommandLine.Parse(inMiddle.ToArray()));
        }

        [TestMethod]
        public void AnUnknownFlagConsumesExactlyOneValueEvenWhenItLooksLikeAFlag()
        {
            var good = Split(HelperCommandLine.Build(Full()));
            var args = new List<string> { "--future-flag", "--pid", "--another", "--build" };
            args.AddRange(good);
            AssertSame(Full(), HelperCommandLine.Parse(args.ToArray()));

            // a known flag whose value looks like a flag still takes it as its value
            var o = Full();
            o.Relaunch = "--pid";
            AssertSame(o, HelperCommandLine.Parse(Split(HelperCommandLine.Build(o)).ToArray()));
        }

        [TestMethod]
        public void TheLastOccurrenceOfAFlagWins()
        {
            var args = Split(HelperCommandLine.Build(Minimal()));
            args.AddRange(new[] { "--build", "9" });
            Assert.AreEqual(9, HelperCommandLine.Parse(args.ToArray()).Build);
        }

        [TestMethod]
        public void TheFrozenRequiredFlagSetIsAllThatAMinimalLineNeeds()
        {
            // helper N+1 must accept the line that build N wrote: exactly these eight flags, nothing else.
            var line = "--mode client --zip z.zip --sha256 " + Sha + " --target t --extract e --backup b --result r.txt --pid 12 --build 4";
            var o = HelperCommandLine.Parse(Split(line).ToArray());
            Assert.AreEqual(HelperMode.Client, o.Mode);
            Assert.AreEqual("z.zip", o.Zip);
            Assert.AreEqual(Sha, o.Sha256);
            Assert.AreEqual("t", o.Target);
            Assert.AreEqual("e", o.Extract);
            Assert.AreEqual("b", o.Backup);
            Assert.AreEqual("r.txt", o.Result);
            Assert.AreEqual(12, o.Pid);
            Assert.AreEqual(4, o.Build);
            Assert.AreEqual(0, o.WaitTimeoutSeconds);
            Assert.IsNull(o.Relaunch);
        }

        // ---- result file ----

        private static string TempFile()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lmp-helper-" + Guid.NewGuid().ToString("N"));
            return Path.Combine(dir, "last-result.txt");
        }

        private static void Cleanup(string file)
        {
            try { Directory.Delete(Path.GetDirectoryName(file), true); } catch (IOException) { }
        }

        [TestMethod]
        public void WriteResultThenTryReadResultRoundTrips()
        {
            var file = TempFile();
            try
            {
                HelperCommandLine.WriteResult(file, true, 3, 2, "Updated agencies.2 -> agencies.3 (kept installed Harmony)");
                Assert.IsTrue(HelperCommandLine.TryReadResult(file, out var success, out var build, out var previous, out var message));
                Assert.IsTrue(success);
                Assert.AreEqual(3, build);
                Assert.AreEqual(2, previous);
                Assert.AreEqual("Updated agencies.2 -> agencies.3 (kept installed Harmony)", message);

                HelperCommandLine.WriteResult(file, false, 4, 3, "Timed out waiting for exit.");
                Assert.IsTrue(HelperCommandLine.TryReadResult(file, out success, out build, out previous, out message));
                Assert.IsFalse(success);
                Assert.AreEqual(4, build);
                Assert.AreEqual(3, previous);
                Assert.AreEqual("Timed out waiting for exit.", message);
            }
            finally { Cleanup(file); }
        }

        [TestMethod]
        public void WriteResultSaysSuccessTrue()
        {
            var file = TempFile();
            try
            {
                HelperCommandLine.WriteResult(file, true, 3, 2, "ok");
                StringAssert.Contains(File.ReadAllText(file), "success=True");
            }
            finally { Cleanup(file); }
        }

        [TestMethod]
        public void WriteResultFlattensAMultiLineMessage()
        {
            var file = TempFile();
            try
            {
                HelperCommandLine.WriteResult(file, false, 3, 2, "first\r\nsecond\nthird\rfourth");
                var lines = File.ReadAllLines(file);
                Assert.AreEqual(1, lines.Count(l => l.StartsWith("message=", StringComparison.Ordinal)));
                Assert.IsTrue(lines.All(l => l.IndexOf('=') > 0), "every line is a key=value pair");

                Assert.IsTrue(HelperCommandLine.TryReadResult(file, out _, out _, out _, out var message));
                Assert.IsFalse(message.Contains('\r') || message.Contains('\n'));
                CollectionAssert.AreEqual(new[] { "first", "second", "third", "fourth" }, message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            }
            finally { Cleanup(file); }
        }

        [TestMethod]
        public void ResultMessagesMayContainEqualsSignsAndUnicode()
        {
            var file = TempFile();
            try
            {
                HelperCommandLine.WriteResult(file, false, 3, 2, "a=b; caf\u00e9 \u65e5\u672c");
                Assert.IsTrue(HelperCommandLine.TryReadResult(file, out _, out _, out _, out var message));
                Assert.AreEqual("a=b; caf\u00e9 \u65e5\u672c", message);
            }
            finally { Cleanup(file); }
        }

        [TestMethod]
        public void ANullMessageReadsBackEmpty()
        {
            var file = TempFile();
            try
            {
                HelperCommandLine.WriteResult(file, true, 3, 2, null);
                Assert.IsTrue(HelperCommandLine.TryReadResult(file, out _, out _, out _, out var message));
                Assert.AreEqual("", message);
            }
            finally { Cleanup(file); }
        }

        [TestMethod]
        public void TryReadResultIgnoresUnknownKeys()
        {
            var file = TempFile();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, "future=1\r\nsuccess=True\r\nbuild=7\r\n# a comment\r\npreviousBuild=6\r\nmessage=hello\r\nanother=thing=with=equals\r\n");
                Assert.IsTrue(HelperCommandLine.TryReadResult(file, out var success, out var build, out var previous, out var message));
                Assert.IsTrue(success);
                Assert.AreEqual(7, build);
                Assert.AreEqual(6, previous);
                Assert.AreEqual("hello", message);
            }
            finally { Cleanup(file); }
        }

        [TestMethod]
        public void TryReadResultFailsOnAMissingOrUnusableFile()
        {
            var file = TempFile();
            try
            {
                Assert.IsFalse(HelperCommandLine.TryReadResult(file, out var success, out var build, out var previous, out var message), "missing file");
                Assert.IsFalse(success);
                Assert.AreEqual(0, build);
                Assert.AreEqual(0, previous);
                Assert.IsNull(message);

                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, "");
                Assert.IsFalse(HelperCommandLine.TryReadResult(file, out _, out _, out _, out _), "empty file");
                File.WriteAllText(file, "message=only a message\r\n");
                Assert.IsFalse(HelperCommandLine.TryReadResult(file, out _, out _, out _, out _), "no success key");
                File.WriteAllText(file, "success=maybe\r\nbuild=3\r\n");
                Assert.IsFalse(HelperCommandLine.TryReadResult(file, out _, out _, out _, out _), "unparsable success");
                File.WriteAllText(file, "success=True\r\nbuild=three\r\n");
                Assert.IsFalse(HelperCommandLine.TryReadResult(file, out _, out _, out _, out _), "unparsable build");
                Assert.IsFalse(HelperCommandLine.TryReadResult(null, out _, out _, out _, out _), "null path");
            }
            finally { Cleanup(file); }
        }

        [TestMethod]
        public void WriteResultOverwritesAnEarlierResult()
        {
            var file = TempFile();
            try
            {
                HelperCommandLine.WriteResult(file, false, 3, 2, "a much longer first message that must not survive the second write");
                HelperCommandLine.WriteResult(file, true, 4, 3, "short");
                Assert.IsTrue(HelperCommandLine.TryReadResult(file, out var success, out var build, out var previous, out var message));
                Assert.IsTrue(success);
                Assert.AreEqual(4, build);
                Assert.AreEqual(3, previous);
                Assert.AreEqual("short", message);
                Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(file)).Length, "no temp file is left behind");
            }
            finally { Cleanup(file); }
        }
    }
}

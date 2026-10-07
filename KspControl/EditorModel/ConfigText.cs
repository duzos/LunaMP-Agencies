using System;
using System.Collections.Generic;
using System.Text;
namespace KspControl.EditorModel
{
    /// <summary>One line inside a node: either a key/value pair or a child node.</summary>
    public sealed class ConfigEntry
    {
        public string Key { get; }
        public string Value { get; }
        public ConfigNode Child { get; }
        public bool IsValue => Child == null;
        public ConfigEntry(string key, string value) { Key = key; Value = value ?? ""; }
        public ConfigEntry(ConfigNode child) { Child = child; }
    }
    /// <summary>Pure KSP ConfigNode tree. Entry order (values and child nodes interleaved) is preserved.</summary>
    public sealed class ConfigNode
    {
        public string Name { get; }
        public List<ConfigEntry> Entries { get; } = new List<ConfigEntry>();
        public ConfigNode(string name) { Name = name ?? ""; }
        public ConfigNode AddValue(string key, string value) { Entries.Add(new ConfigEntry(key, value)); return this; }
        public ConfigNode AddNode(ConfigNode child) { Entries.Add(new ConfigEntry(child)); return child; }
        public ConfigNode AddNode(string name) { return AddNode(new ConfigNode(name)); }
        public IEnumerable<KeyValuePair<string, string>> Values()
        {
            foreach (var e in Entries) if (e.IsValue) yield return new KeyValuePair<string, string>(e.Key, e.Value);
        }
        public IEnumerable<string> Values(string key)
        {
            foreach (var e in Entries) if (e.IsValue && string.Equals(e.Key, key, StringComparison.Ordinal)) yield return e.Value;
        }
        public string First(string key)
        {
            foreach (var e in Entries) if (e.IsValue && string.Equals(e.Key, key, StringComparison.Ordinal)) return e.Value;
            return null;
        }
        public IEnumerable<ConfigNode> Children()
        {
            foreach (var e in Entries) if (!e.IsValue) yield return e.Child;
        }
        public IEnumerable<ConfigNode> Children(string name)
        {
            foreach (var e in Entries) if (!e.IsValue && string.Equals(e.Child.Name, name, StringComparison.Ordinal)) yield return e.Child;
        }
    }
    public sealed class ConfigParseException : Exception
    {
        public string Code { get; }
        public int Line { get; }
        public ConfigParseException(string code, int line) : base(code + " at line " + line) { Code = code; Line = line; }
    }
    /// <summary>
    /// Parser and printer for KSP ConfigNode text: "key = value" lines, "NAME" followed by "{" ... "}" nodes, // comments.
    /// Comments are dropped. Printing uses tab indentation and "key = value" (an empty value prints "key = ", as KSP does).
    /// </summary>
    public static class ConfigText
    {
        public const int MaxDepth = 32;
        public const int MaxChars = 4 * 1024 * 1024;
        public static ConfigNode Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            if (text.Length > MaxChars) throw new ConfigParseException("config_too_large", 0);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            var root = new ConfigNode("");
            var stack = new List<ConfigNode> { root };
            string pendingName = null; int pendingLine = 0;
            int lineNo = 0, pos = 0;
            while (pos < text.Length)
            {
                int end = pos;
                while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;
                var raw = text.Substring(pos, end - pos);
                if (end < text.Length && text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n') end++;
                pos = end + 1; lineNo++;
                var line = StripComment(raw).Trim();
                if (line.Length == 0) continue;
                if (pendingName != null)
                {
                    if (line == "{") { Open(stack, pendingName, lineNo); pendingName = null; continue; }
                    throw new ConfigParseException("expected_open_brace", lineNo);
                }
                if (line == "{") throw new ConfigParseException("unexpected_open_brace", lineNo);
                if (line == "}")
                {
                    if (stack.Count <= 1) throw new ConfigParseException("unbalanced_close_brace", lineNo);
                    stack.RemoveAt(stack.Count - 1); continue;
                }
                int eq = line.IndexOf('=');
                if (eq >= 0)
                {
                    var key = line.Substring(0, eq).Trim();
                    if (key.Length == 0) throw new ConfigParseException("empty_key", lineNo);
                    if (key.IndexOf('{') >= 0 || key.IndexOf('}') >= 0) throw new ConfigParseException("inline_braces", lineNo);
                    stack[stack.Count - 1].AddValue(key, line.Substring(eq + 1).Trim());
                    continue;
                }
                if (line.EndsWith("{", StringComparison.Ordinal))
                {
                    var name = line.Substring(0, line.Length - 1).Trim();
                    if (name.Length == 0) throw new ConfigParseException("empty_node_name", lineNo);
                    Open(stack, name, lineNo); continue;
                }
                pendingName = line; pendingLine = lineNo;
            }
            if (pendingName != null) throw new ConfigParseException("expected_open_brace", pendingLine);
            if (stack.Count != 1) throw new ConfigParseException("unbalanced_open_brace", lineNo);
            return root;
        }
        private static void Open(List<ConfigNode> stack, string name, int line)
        {
            if (stack.Count > MaxDepth) throw new ConfigParseException("config_too_deep", line);
            var child = stack[stack.Count - 1].AddNode(name);
            stack.Add(child);
        }
        private static string StripComment(string line)
        {
            // A comment starts at "//" at the line start or after whitespace, so values such as "http://x" survive.
            int from = 0;
            while (true)
            {
                int i = line.IndexOf("//", from, StringComparison.Ordinal);
                if (i < 0) return line;
                if (i == 0 || char.IsWhiteSpace(line[i - 1])) return line.Substring(0, i);
                from = i + 2;
            }
        }
        public static string Print(ConfigNode root, string newLine = "\r\n")
        {
            if (root == null) throw new ArgumentNullException("root");
            var sb = new StringBuilder();
            PrintEntries(root, 0, sb, newLine);
            return sb.ToString();
        }
        private static void PrintEntries(ConfigNode node, int depth, StringBuilder sb, string nl)
        {
            if (depth > MaxDepth) throw new ConfigParseException("config_too_deep", 0);
            foreach (var e in node.Entries)
            {
                if (e.IsValue) { Indent(sb, depth); sb.Append(e.Key).Append(" = ").Append(e.Value).Append(nl); }
                else
                {
                    Indent(sb, depth); sb.Append(e.Child.Name).Append(nl);
                    Indent(sb, depth); sb.Append('{').Append(nl);
                    PrintEntries(e.Child, depth + 1, sb, nl);
                    Indent(sb, depth); sb.Append('}').Append(nl);
                }
                if (sb.Length > MaxChars) throw new ConfigParseException("config_too_large", 0);
            }
        }
        private static void Indent(StringBuilder sb, int depth) { for (int i = 0; i < depth; i++) sb.Append('\t'); }
    }
}

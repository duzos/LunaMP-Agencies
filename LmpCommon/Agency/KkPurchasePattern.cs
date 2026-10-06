using System;
using System.Collections.Generic;

namespace LmpCommon.Agency
{
    /// <summary>Projected IL matching, independent of Unity and Harmony. Refuses partial matches.</summary>
    public static class KkPurchasePattern
    {
        public enum Kind { Other, Label, Button, OtherButton, FalseBranch, ControlFlow }
        public sealed class Instruction
        {
            public Kind Type { get; }
            public string Text { get; }
            public Instruction(Kind type, string text = null) { Type = type; Text = text; }
        }

        public static int[] Match(IReadOnlyList<Instruction> instructions, params string[] requiredLabels)
        {
            var matches = new List<int>();
            foreach (var label in requiredLabels)
            {
                var found = -1;
                for (var i = 0; i < instructions.Count; i++)
                {
                    if (instructions[i].Type != Kind.Label || instructions[i].Text != label) continue;
                    if (found >= 0) throw new InvalidOperationException("Duplicate KK purchase label");
                    for (var j = i + 1; j < instructions.Count && j <= i + 32; j++)
                    {
                        var kind = instructions[j].Type;
                        if (kind == Kind.ControlFlow || kind == Kind.FalseBranch || kind == Kind.OtherButton)
                            throw new InvalidOperationException("Unsupported KK purchase expression");
                        if (kind != Kind.Button) continue;
                        if (j + 1 >= instructions.Count || instructions[j + 1].Type != Kind.FalseBranch)
                            throw new InvalidOperationException("KK purchase button does not guard a block");
                        found = j;
                        break;
                    }
                    if (found < 0) throw new InvalidOperationException("KK purchase button missing");
                }
                if (found < 0 || matches.Contains(found)) throw new InvalidOperationException("KK purchase label missing or shared");
                matches.Add(found);
            }
            return matches.ToArray();
        }
    }

    public static class KkCareerSaveScope
    {
        [ThreadStatic] private static int depth;
        public static bool Active => depth > 0;
        public static IDisposable Enter() { depth++; return new Scope(); }
        private sealed class Scope : IDisposable
        {
            private bool disposed;
            public void Dispose() { if (disposed) return; disposed = true; depth--; }
        }
    }
}

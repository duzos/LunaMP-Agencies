using System;
using System.IO;
using System.Text;
using KspControl.Contracts;
using UnityEngine;

namespace KspControl.Bridge
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class ControlAddon : MonoBehaviour
    {
        private readonly ObservationQueue queue = new ObservationQueue();
        private readonly Observations observations = new Observations();
        private LoopbackServer server;
        private ExecutionAuthority authority;
        private GrantWatcher watcher;
        private ControlPump pump;
        private KeyCode stopKey = KeyCode.None;
        private string panelLine = "";
        private float panelRefreshed = -1f;
        public void Awake()
        {
            DontDestroyOnLoad(this);
            try
            {
                var path = Environment.GetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE");
                if (string.IsNullOrEmpty(path)) return; // Installation alone never exposes a listener.
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 512) return;
                var token = File.ReadAllText(path).Trim();
                if (token.Length < 32 || token.Length > 256) return;
                var portText = Environment.GetEnvironmentVariable("KSP_CONTROL_PORT");
                int port;
                if (!int.TryParse(portText, out port)) port = Contracts.BridgeFrames.DefaultPort;
                if (port < 1024 || port > 65535) return;
                var store = new FileSuspensionStore(Path.Combine(KSPUtil.ApplicationRootPath, "KspControlData", "control", "suspensions.json"));
                authority = new ExecutionAuthority(() => MonotonicClock.Milliseconds, GrantMapping.KnownEffects, ControlLimits.WatchdogMilliseconds, store);
                var source = new KspContextSource(observations);
                // These paths reach only KSP. The MCP host is never given the key path.
                var grantFile = Environment.GetEnvironmentVariable("KSP_CONTROL_GRANT_FILE");
                var keyFile = Environment.GetEnvironmentVariable("KSP_CONTROL_GRANT_KEY_FILE");
                if (!string.IsNullOrEmpty(grantFile) && !string.IsNullOrEmpty(keyFile)) watcher = new GrantWatcher(authority, grantFile, keyFile, source.CurrentBinding);
                pump = new ControlPump(authority, watcher, source);
                ReadStopKey();
                server = new LoopbackServer(queue, token, port, new ControlDispatcher(authority).Handle);
                Debug.Log("[KspControl] Bridge ready (read-only observations; control leases inline).");
            }
            catch { Debug.LogWarning("[KspControl] Bridge unavailable; no control enabled."); }
        }
        private void ReadStopKey()
        {
            var text = Environment.GetEnvironmentVariable("KSP_CONTROL_STOP_KEY");
            if (string.IsNullOrEmpty(text)) return;
            try { stopKey = (KeyCode)Enum.Parse(typeof(KeyCode), text, true); }
            catch { Debug.LogWarning("[KspControl] KSP_CONTROL_STOP_KEY is not a valid key name; hotkey disabled."); }
        }
        public void Update()
        {
            if (server == null) return;
            try { observations.RefreshContext(); }
            catch { return; } // Scene teardown can invalidate game objects; pending requests expire without disclosure.
            try { pump.Update(); } catch { /* the trust layer must never break the frame */ }
            if (stopKey != KeyCode.None && Input.GetKeyDown(stopKey)) authority.Stop();
            queue.Drain(observations.Execute);
        }
        public void OnGUI()
        {
            if (watcher == null || authority == null) return;
            if (Time.unscaledTime - panelRefreshed > 0.25f) { panelRefreshed = Time.unscaledTime; panelLine = Describe(authority.Status()); }
            GUI.Label(new Rect(8f, 4f, 640f, 22f), panelLine);
            if (GUI.Button(new Rect(8f, 26f, 72f, 22f), "Stop")) authority.Stop();
        }
        private static string Describe(ControlStatusInfo status)
        {
            var text = new StringBuilder("KspControl: grant ").Append(status.Grant.State);
            if (status.Grant.Generation.HasValue) text.Append(" g").Append(status.Grant.Generation.Value);
            if (status.Lease.Held) text.Append(" | lease '").Append(status.Lease.Purpose).Append("' ").Append(status.Lease.ExpiresInSeconds).Append('s');
            if (status.CooldownSeconds > 0) text.Append(" | cooldown ").Append(status.CooldownSeconds).Append('s');
            return text.ToString();
        }
        public void OnDestroy() { server?.Dispose(); queue.Stop(); }
    }
}

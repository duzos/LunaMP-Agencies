using System;
using System.IO;
using UnityEngine;

namespace KspControl.Bridge
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class ControlAddon : MonoBehaviour
    {
        private readonly ObservationQueue queue = new ObservationQueue();
        private readonly Observations observations = new Observations();
        private LoopbackServer server;
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
                server = new LoopbackServer(queue, token, port);
                Debug.Log("[KspControl] Read-only bridge ready.");
            }
            catch { Debug.LogWarning("[KspControl] Bridge unavailable; no control enabled."); }
        }
        public void Update() { observations.RefreshContext(); queue.Drain(observations.Execute, DateTime.UtcNow); }
        public void OnDestroy() { server?.Dispose(); queue.Stop(); }
    }
}

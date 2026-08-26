// The official TopStats Analytics plugin for Rust (Oxide/uMod and Carbon).
// Drop this file into oxide/plugins. Player connects, disconnects with
// session length, and a server stats heartbeat land in your TopStats
// workspace; other plugins can post their own events through the Track API.
//
// Oxide compiles plugins from source inside a sandbox that cannot load
// external assemblies, so unlike most TopStats integrations this one does
// not embed the C# SDK: it carries a compact port of the same semantics -
// buffered capture, batch splitting at 500 events and 2 MiB, oversized
// events dropped, jittered backoff retries on 429, 5xx, and network
// failures only, and a bounded drop-oldest queue. One deviation: Oxide's
// webrequest callback exposes no response headers, so Retry-After cannot be
// honoured and backoff alone paces retries.

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("TopStats", "TopStats", "0.1.0")]
    [Description("Sends player and server events to TopStats Analytics.")]
    public class TopStats : CovalencePlugin
    {
        private const string DefaultHost = "https://topstats.gg";
        private const int MaxBatchSize = 500;
        private const int MaxEventBytes = 65536;
        private const int MaxBodyBytes = 2097152;
        // {"events":[ plus ]} around the comma-joined events.
        private const int WrapperBytes = 13;
        private const int MaxNameLength = 128;
        private const int MaxQueueSize = 10000;
        private const int MaxRetries = 3;
        private const float InitialRetryDelaySeconds = 0.5f;
        private const float MaxRetryDelaySeconds = 30f;

        private PluginConfig config;
        private readonly List<string> queue = new List<string>();
        private readonly Dictionary<string, double> joinedAt =
            new Dictionary<string, double>();
        private readonly Random jitter = new Random();
        private string eventsUrl;
        private double startedAt;
        private bool shutDown;

        private sealed class PluginConfig
        {
            public string ApiKey = "";
            public string Host = "";
            public string Source = "rust";
            public bool TrackSessions = true;
            public int HeartbeatSeconds = 60;
            public int FlushAt = 20;
            public int FlushSeconds = 5;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        private void Init()
        {
            config = Config.ReadObject<PluginConfig>() ?? new PluginConfig();

            if (string.IsNullOrEmpty(config.ApiKey) || config.ApiKey.Trim().Length == 0)
            {
                LogWarning(
                    "no ApiKey set in oxide/config/TopStats.json - TopStats is disabled"
                    + " until you add one. Create a key in your workspace under"
                    + " Settings -> API keys.");
                return;
            }

            var host = DefaultHost;

            if (!string.IsNullOrEmpty(config.Host) && config.Host.Trim().Length > 0)
            {
                host = config.Host.Trim().TrimEnd('/');
            }

            eventsUrl = host + "/v1/events";
            startedAt = Now();

            if (config.FlushSeconds > 0)
            {
                timer.Every(Math.Max(1, config.FlushSeconds), Flush);
            }

            if (config.HeartbeatSeconds > 0)
            {
                timer.Every(Math.Max(1, config.HeartbeatSeconds), CaptureServerStats);
            }
        }

        private void Unload()
        {
            Flush();
            shutDown = true;
        }

        // -- Hooks ----------------------------------------------------------

        private void OnUserConnected(IPlayer player)
        {
            if (!config.TrackSessions || player == null)
            {
                return;
            }

            joinedAt[player.Id] = Now();
            Track("player_connected", null, player.Id, player.Name);
        }

        private void OnUserDisconnected(IPlayer player, string reason)
        {
            if (!config.TrackSessions || player == null)
            {
                return;
            }

            var properties = new Dictionary<string, object>
            {
                ["reason"] = string.IsNullOrEmpty(reason) ? "unknown" : reason,
            };

            double joined;

            if (joinedAt.TryGetValue(player.Id, out joined))
            {
                properties["session_seconds"] = Math.Max(0, (long)(Now() - joined));
                joinedAt.Remove(player.Id);
            }

            Track("player_dropped", properties, player.Id, player.Name);
        }

        private void CaptureServerStats()
        {
            var online = 0;

            foreach (var _ in players.Connected)
            {
                online += 1;
            }

            Track("server_stats", new Dictionary<string, object>
            {
                ["players_online"] = online,
                ["max_players"] = server.MaxPlayers,
                ["uptime_seconds"] = Math.Max(0, (long)(Now() - startedAt)),
            }, null, null);
        }

        // -- The Track API for other plugins --------------------------------
        //   [PluginReference] private Plugin TopStats;
        //   TopStats?.Call("Track", "quest_completed",
        //       new Dictionary<string, object> { ["reward"] = 100 },
        //       player.Id, player.Name);

        private void Track(
            string name,
            Dictionary<string, object> properties,
            string actor,
            string actorLabel)
        {
            if (shutDown || config == null || string.IsNullOrEmpty(eventsUrl))
            {
                return;
            }

            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            {
                LogWarning("event name must be 1 to " + MaxNameLength + " characters; dropped");
                return;
            }

            var wire = new Dictionary<string, object> { ["name"] = name };

            if (properties != null && properties.Count > 0)
            {
                wire["properties"] = properties;
            }

            if (!string.IsNullOrEmpty(config.Source))
            {
                wire["_source"] = config.Source;
            }

            if (!string.IsNullOrEmpty(actor))
            {
                wire["_actor"] = actor;
            }

            if (!string.IsNullOrEmpty(actorLabel))
            {
                wire["_actorLabel"] = actorLabel;
            }

            // Stamped at capture time, not send time, so a buffered event
            // keeps the moment it actually happened.
            wire["_timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

            string encoded;

            try
            {
                encoded = JsonConvert.SerializeObject(wire);
            }
            catch (Exception)
            {
                LogWarning("event does not serialise to JSON; dropped");
                return;
            }

            if (Encoding.UTF8.GetByteCount(encoded) > MaxEventBytes)
            {
                LogWarning("event \"" + name + "\" is over the " + MaxEventBytes
                    + " byte limit; dropped");
                return;
            }

            while (queue.Count >= MaxQueueSize)
            {
                queue.RemoveAt(0);
                LogWarning("queue full; dropped the oldest buffered event");
            }

            queue.Add(encoded);

            if (queue.Count >= Math.Max(1, config.FlushAt))
            {
                Flush();
            }
        }

        private void Flush()
        {
            if (queue.Count == 0)
            {
                return;
            }

            var batches = BuildBatches(queue);
            queue.Clear();

            foreach (var body in batches)
            {
                Send(body, 0);
            }
        }

        /// Splits into request bodies at the batch-size cap and the body byte
        /// limit, whichever hits first, using real serialised sizes.
        private static List<string> BuildBatches(List<string> encodedEvents)
        {
            var batches = new List<string>();
            var current = new List<string>();
            var currentBytes = WrapperBytes;

            foreach (var encoded in encodedEvents)
            {
                var separator = current.Count == 0 ? 0 : 1;
                var eventBytes = Encoding.UTF8.GetByteCount(encoded);
                var projected = currentBytes + separator + eventBytes;

                if (current.Count > 0
                    && (current.Count >= MaxBatchSize || projected > MaxBodyBytes))
                {
                    batches.Add("{\"events\":[" + string.Join(",", current) + "]}");
                    current.Clear();
                    currentBytes = WrapperBytes;
                }

                currentBytes += (current.Count == 0 ? 0 : 1) + eventBytes;
                current.Add(encoded);
            }

            if (current.Count > 0)
            {
                batches.Add("{\"events\":[" + string.Join(",", current) + "]}");
            }

            return batches;
        }

        /// 429, 5xx, and network failures (code 0 or negative) retry with
        /// jittered exponential backoff; 400, 401, 402, and 413 are permanent.
        private static bool IsRetryable(int code)
        {
            return code <= 0 || code == 429 || code >= 500;
        }

        private float RetryDelaySeconds(int attempt)
        {
            var ceiling = Math.Min(
                InitialRetryDelaySeconds * (float)Math.Pow(2, Math.Min(attempt, 16)),
                MaxRetryDelaySeconds);

            var half = ceiling / 2f;
            return half + ((float)jitter.NextDouble() * half);
        }

        private void Send(string body, int attempt)
        {
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + config.ApiKey,
                ["Content-Type"] = "application/json",
            };

            webrequest.Enqueue(eventsUrl, body, (code, response) =>
            {
                if (code >= 200 && code < 300)
                {
                    return;
                }

                if (IsRetryable(code) && attempt < MaxRetries)
                {
                    timer.Once(RetryDelaySeconds(attempt), () => Send(body, attempt + 1));
                    return;
                }

                // The message never includes the key or the body, so nothing
                // sensitive can reach the console.
                LogWarning("send failed with status " + code + "; giving up on this batch");
            }, this, RequestMethod.POST, headers, 10000f);
        }

        private static double Now()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .TotalSeconds;
        }
    }
}

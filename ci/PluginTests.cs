// Drives the exact deployable TopStats.cs through the Oxide stubs. Hooks and
// internals are reached by reflection because Oxide itself calls them the
// same way; nothing here touches the network.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core.Libraries.Covalence;
using Xunit;

public sealed class PluginTests
{
    private const string ApiKey = "ts_test_fake_key_for_unit_tests_only";

    private static Oxide.Plugins.TopStats Plugin(Action<object> mutateConfig = null)
    {
        var plugin = new Oxide.Plugins.TopStats();

        Invoke(plugin, "LoadDefaultConfig");
        var stored = plugin.Config.Stored;
        SetField(stored, "ApiKey", ApiKey);
        mutateConfig?.Invoke(stored);

        Invoke(plugin, "Init");
        return plugin;
    }

    private static void Invoke(object target, string name, params object[] args)
    {
        var method = target.GetType().GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);
        method.Invoke(target, args);
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static void Track(
        Oxide.Plugins.TopStats plugin,
        string name,
        Dictionary<string, object> properties = null,
        string actor = null,
        string label = null)
    {
        Invoke(plugin, "Track", name, properties, actor, label);
    }

    private static JArray Events(Oxide.Core.Libraries.RecordedRequest request)
    {
        // DateParseHandling.None keeps the wire timestamp a string; Json.NET
        // would otherwise re-parse it into a DateTime and break assertions.
        var parsed = JsonConvert.DeserializeObject<JObject>(
            request.Body,
            new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });

        return (JArray)parsed["events"];
    }

    [Fact]
    public void TracksTheBatchShapeWithAuthHeadersAndContext()
    {
        var plugin = Plugin();

        Track(plugin, "quest_completed",
            new Dictionary<string, object> { ["reward"] = 100 },
            "76561198000000001", "Ada");
        Invoke(plugin, "Flush");

        var request = Assert.Single(plugin.webrequest.Requests);
        Assert.Equal("https://topstats.gg/v1/events", request.Url);
        Assert.Equal("Bearer " + ApiKey, request.Headers["Authorization"]);

        var events = Events(request);
        var single = Assert.Single(events);
        Assert.Equal("quest_completed", (string)single["name"]);
        Assert.Equal(100, (int)single["properties"]["reward"]);
        Assert.Equal("76561198000000001", (string)single["_actor"]);
        Assert.Equal("Ada", (string)single["_actorLabel"]);
        Assert.Equal("rust", (string)single["_source"]);
        Assert.EndsWith("Z", (string)single["_timestamp"]);
    }

    [Fact]
    public void SplitsBatchesAtTheEventCountCap()
    {
        var plugin = Plugin(config => SetField(config, "FlushAt", 10000));

        for (var index = 0; index < 501; index += 1)
        {
            Track(plugin, "tick");
        }

        Invoke(plugin, "Flush");

        Assert.Equal(2, plugin.webrequest.Requests.Count);
        Assert.Equal(500, Events(plugin.webrequest.Requests[0]).Count);
        Assert.Single(Events(plugin.webrequest.Requests[1]));
    }

    [Fact]
    public void DropsOversizedEventsAndTheOldestOnOverflow()
    {
        var plugin = Plugin(config => SetField(config, "FlushAt", 10000));

        Track(plugin, "huge",
            new Dictionary<string, object> { ["blob"] = new string('x', 70000) });
        Invoke(plugin, "Flush");

        Assert.Empty(plugin.webrequest.Requests);
        Assert.Contains(plugin.Warnings, warning => warning.Contains("huge"));
    }

    [Fact]
    public void RetriesTransientFailuresAndNeverPermanentOnes()
    {
        var plugin = Plugin(config => SetField(config, "FlushAt", 10000));

        Track(plugin, "event");
        Invoke(plugin, "Flush");

        var first = Assert.Single(plugin.webrequest.Requests);
        first.Callback(429, "");

        var retry = plugin.timer.Scheduled.Last(call => !call.Repeating);
        retry.Callback();
        Assert.Equal(2, plugin.webrequest.Requests.Count);

        plugin.webrequest.Requests[1].Callback(202, "");
        Assert.DoesNotContain(plugin.Warnings, warning => warning.Contains("giving up"));

        // Permanent statuses never schedule a retry.
        var permanent = Plugin(config => SetField(config, "FlushAt", 10000));
        Track(permanent, "event");
        Invoke(permanent, "Flush");
        var scheduledBefore = permanent.timer.Scheduled.Count;
        permanent.webrequest.Requests[0].Callback(401, "");

        Assert.Equal(scheduledBefore, permanent.timer.Scheduled.Count);
        Assert.Contains(permanent.Warnings, warning => warning.Contains("401"));
    }

    [Fact]
    public void GivesUpAfterMaxRetries()
    {
        var plugin = Plugin(config => SetField(config, "FlushAt", 10000));

        Track(plugin, "event");
        Invoke(plugin, "Flush");

        for (var attempt = 0; attempt < 4; attempt += 1)
        {
            plugin.webrequest.Requests[attempt].Callback(503, "");

            var retries = plugin.timer.Scheduled.Where(call => !call.Repeating).ToList();

            if (attempt < 3)
            {
                retries[attempt].Callback();
            }
        }

        Assert.Equal(4, plugin.webrequest.Requests.Count);
        Assert.Contains(plugin.Warnings, warning => warning.Contains("giving up"));
        Assert.DoesNotContain(plugin.Warnings, warning => warning.Contains(ApiKey));
    }

    [Fact]
    public void SessionsProduceConnectAndDropWithDuration()
    {
        var plugin = Plugin(config => SetField(config, "FlushAt", 10000));
        var player = new FakePlayer { Id = "76561198000000001", Name = "Ada" };

        Invoke(plugin, "OnUserConnected", player);
        Invoke(plugin, "OnUserDisconnected", player, "disconnect");
        Invoke(plugin, "Flush");

        var events = Events(Assert.Single(plugin.webrequest.Requests));
        Assert.Equal(2, events.Count);
        Assert.Equal("player_connected", (string)events[0]["name"]);
        Assert.Equal("player_dropped", (string)events[1]["name"]);
        Assert.Equal("disconnect", (string)events[1]["properties"]["reason"]);
        Assert.NotNull(events[1]["properties"]["session_seconds"]);
    }

    [Fact]
    public void HeartbeatCapturesServerStats()
    {
        var plugin = Plugin(config => SetField(config, "FlushAt", 10000));
        plugin.players.Players.Add(new FakePlayer { Id = "1", Name = "a" });
        plugin.players.Players.Add(new FakePlayer { Id = "2", Name = "b" });
        plugin.server.MaxPlayers = 150;

        Invoke(plugin, "CaptureServerStats");
        Invoke(plugin, "Flush");

        var stats = Assert.Single(Events(Assert.Single(plugin.webrequest.Requests)));
        Assert.Equal("server_stats", (string)stats["name"]);
        Assert.Equal(2, (int)stats["properties"]["players_online"]);
        Assert.Equal(150, (int)stats["properties"]["max_players"]);
    }

    [Fact]
    public void StaysDormantWithoutAnApiKey()
    {
        var plugin = new Oxide.Plugins.TopStats();
        Invoke(plugin, "LoadDefaultConfig");
        Invoke(plugin, "Init");

        Track(plugin, "event");
        Invoke(plugin, "Flush");

        Assert.Empty(plugin.webrequest.Requests);
        Assert.Contains(plugin.Warnings, warning => warning.Contains("ApiKey"));
    }
}

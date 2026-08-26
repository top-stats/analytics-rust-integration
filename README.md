# TopStats for Rust

The official TopStats Analytics plugin for Rust servers running Oxide/uMod
or Carbon. Player connects, disconnects with session length and reason, and
a server stats heartbeat land in your TopStats workspace as events,
attributed per player - plus a Track API so any plugin can post its own
events through the same buffered client.

Server-side only. No chat, no positions, no content of any kind.

## Install

1. Drop `TopStats.cs` into `oxide/plugins/` (Carbon: `carbon/plugins/`).

2. The first load writes `oxide/config/TopStats.json`. Create an API key in
   your TopStats workspace (Settings -> API keys) and set it:

   ```json
   {
     "ApiKey": "ts_live_your_key_here"
   }
   ```

3. Reload the plugin (`oxide.reload TopStats`). Connects, disconnects, and
   server stats appear in your workspace within seconds.

## What gets sent

| Event | Properties | When |
| --- | --- | --- |
| `player_connected` | | A player connects. |
| `player_dropped` | `reason`, `session_seconds` | A player disconnects. |
| `server_stats` | `players_online`, `max_players`, `uptime_seconds` | Every `HeartbeatSeconds` (default 60). |

Players are identified by their Steam ID, so the same person stays one actor
across sessions and name changes; the current name rides along as the
display label.

## Track your own events

Any plugin can post through the same buffered client:

```csharp
[PluginReference] private Plugin TopStats;

TopStats?.Call("Track", "quest_completed",
    new Dictionary<string, object> { ["reward"] = 100 },
    player.Id, player.Name);
```

## Configuration

`oxide/config/TopStats.json`:

| Key | Default | What it does |
| --- | --- | --- |
| `ApiKey` | required | Your workspace API key. |
| `Host` | `https://topstats.gg` | API origin override. |
| `Source` | `rust` | The `_source` label on every event. |
| `TrackSessions` | `true` | Player connect and disconnect events. |
| `HeartbeatSeconds` | `60` | Seconds between `server_stats`. 0 disables. |
| `FlushAt` | `20` | Buffered events that trigger a send. |
| `FlushSeconds` | `5` | Timer flush period. |

## How it works, and one honest deviation

Oxide compiles plugins from source inside a sandbox that cannot load
external assemblies, so this plugin does not embed the TopStats C# SDK: it
carries a compact port of the same behaviour - buffered capture, batch
splitting at 500 events and 2 MiB, oversized events dropped before sending,
jittered backoff retries on 429, 5xx, and network failures only, and a
bounded drop-oldest queue. CI compiles this exact file against a stub of
the Oxide surface and unit-tests that behaviour.

The one deviation from the other TopStats clients: Oxide's webrequest
callback exposes no response headers, so `Retry-After` cannot be honoured
and backoff alone paces retries.

Full product documentation: <https://docs.topstats.gg/docs/analytics>

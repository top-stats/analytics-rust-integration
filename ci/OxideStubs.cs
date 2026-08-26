// A minimal recreation of the Oxide API surface TopStats.cs touches, so CI
// can compile the exact deployable file and unit-test its behaviour without
// an Oxide installation. Everything records into public fields the tests
// read; nothing here touches the network or a timer wheel.

using System;
using System.Collections.Generic;

namespace Oxide.Core.Libraries
{
    public enum RequestMethod
    {
        GET,
        POST,
    }

    public sealed class RecordedRequest
    {
        public string Url;
        public string Body;
        public Dictionary<string, string> Headers;
        public Action<int, string> Callback;
    }

    public sealed class WebRequests
    {
        public readonly List<RecordedRequest> Requests = new List<RecordedRequest>();

        public void Enqueue(
            string url,
            string body,
            Action<int, string> callback,
            object owner,
            RequestMethod method,
            Dictionary<string, string> headers,
            float timeout)
        {
            Requests.Add(new RecordedRequest
            {
                Url = url,
                Body = body,
                Headers = headers,
                Callback = callback,
            });
        }
    }

    public sealed class ScheduledCall
    {
        public float Seconds;
        public Action Callback;
        public bool Repeating;
    }

    public sealed class Timers
    {
        public readonly List<ScheduledCall> Scheduled = new List<ScheduledCall>();

        public void Every(float seconds, Action callback)
        {
            Scheduled.Add(new ScheduledCall
            {
                Seconds = seconds,
                Callback = callback,
                Repeating = true,
            });
        }

        public void Once(float seconds, Action callback)
        {
            Scheduled.Add(new ScheduledCall
            {
                Seconds = seconds,
                Callback = callback,
                Repeating = false,
            });
        }
    }
}

namespace Oxide.Core.Libraries.Covalence
{
    public interface IPlayer
    {
        string Id { get; }
        string Name { get; }
    }

    public sealed class FakePlayer : IPlayer
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public interface IPlayerManager
    {
        IEnumerable<IPlayer> Connected { get; }
    }

    public sealed class FakePlayerManager : IPlayerManager
    {
        public List<IPlayer> Players = new List<IPlayer>();

        public IEnumerable<IPlayer> Connected
        {
            get { return Players; }
        }
    }

    public interface IServer
    {
        int MaxPlayers { get; }
    }

    public sealed class FakeServer : IServer
    {
        public int MaxPlayers { get; set; } = 100;
    }
}

namespace Oxide.Plugins
{
    using Oxide.Core.Libraries;
    using Oxide.Core.Libraries.Covalence;

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class InfoAttribute : Attribute
    {
        public InfoAttribute(string title, string author, string version)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DescriptionAttribute : Attribute
    {
        public DescriptionAttribute(string description)
        {
        }
    }

    public sealed class FakeConfigFile
    {
        public object Stored;

        public void WriteObject(object value, bool sync = false)
        {
            Stored = value;
        }

        public T ReadObject<T>() where T : new()
        {
            if (Stored is T typed)
            {
                return typed;
            }

            return new T();
        }
    }

    public abstract class CovalencePlugin
    {
        public readonly WebRequests webrequest = new WebRequests();
        public readonly Timers timer = new Timers();
        public FakePlayerManager players = new FakePlayerManager();
        public FakeServer server = new FakeServer();
        public readonly FakeConfigFile Config = new FakeConfigFile();
        public readonly List<string> Warnings = new List<string>();

        protected void LogWarning(string message)
        {
            Warnings.Add(message);
        }

        protected virtual void LoadDefaultConfig()
        {
        }
    }
}

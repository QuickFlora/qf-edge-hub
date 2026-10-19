using System;
using System.Collections.Generic;

namespace QfEdgeHub
{
    /// <summary>Who is connected right now. In memory only; a recycle empties it and the PCs reconnect.</summary>
    public static class Registry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Terminal> byConnection = new Dictionary<string, Terminal>();
        private static readonly Dictionary<string, int> connectionsPerKey = new Dictionary<string, int>();

        public static void Add(Terminal t)
        {
            lock (Gate)
            {
                byConnection[t.ConnectionId] = t;
                int n; connectionsPerKey.TryGetValue(t.Key, out n); connectionsPerKey[t.Key] = n + 1;
            }
        }

        public static Terminal Remove(string connectionId)
        {
            lock (Gate)
            {
                Terminal t;
                if (!byConnection.TryGetValue(connectionId, out t)) return null;
                byConnection.Remove(connectionId);
                int n; connectionsPerKey.TryGetValue(t.Key, out n);
                if (n <= 1) connectionsPerKey.Remove(t.Key); else connectionsPerKey[t.Key] = n - 1;
                return t;
            }
        }

        public static Terminal Get(string connectionId)
        {
            lock (Gate) { Terminal t; byConnection.TryGetValue(connectionId, out t); return t; }
        }

        /// <summary>Distinct terminals with at least one live connection (one entry per key).</summary>
        public static List<Terminal> ConnectedTerminals()
        {
            lock (Gate)
            {
                Dictionary<string, Terminal> seen = new Dictionary<string, Terminal>();
                foreach (Terminal t in byConnection.Values) if (!seen.ContainsKey(t.Key)) seen[t.Key] = t;
                return new List<Terminal>(seen.Values);
            }
        }

        public static int ConnectionCount { get { lock (Gate) { return byConnection.Count; } } }

        /// <summary>Live connections for one terminal key (0 = that PC is not on the hub right now).</summary>
        public static int ConnectionsFor(string key)
        {
            lock (Gate) { int n; connectionsPerKey.TryGetValue(key, out n); return n; }
        }

        public static List<Terminal> All()
        {
            lock (Gate) { return new List<Terminal>(byConnection.Values); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Text;
using System.Threading;
using Microsoft.AspNet.SignalR;

namespace QfEdgeHub
{
    /// <summary>
    /// The one loop that replaces every shop PC's polling. Once a second, for ALL connected terminals
    /// in two batched statements (no per-terminal query):
    ///   1. POSPrintProcess rows with [Read]=1 AND Active=1 (last 24 h) -> push Job to the terminal's group
    ///   2. any Active=1 print request (POSPrintRequest, POSPaidOut, POSShift, POSReturnOrder, Trip, RO)
    ///      -> call the existing PingPOSForPrinting once for that terminal (it builds the jobs), push Nudge
    /// A job is pushed once; the PC claims it with UpdateReadPOSForPrinting ([Read]=0) exactly as
    /// CheckPOSForPrinting does today, so an unclaimed job (PC vanished mid-push) is simply pushed again
    /// after RepushSeconds or picked up by the PC's long-poll fallback. No database write happens here.
    /// Zero database work while no terminal is connected.
    /// Everything catches its own errors: an unhandled exception on a timer thread stops the worker process.
    /// </summary>
    public static class Dispatcher
    {
        public const int TickMilliseconds = 1000;
        public const int RepushSeconds = 60;      // a pushed job nobody claimed is offered again after this
        public const int PingEverySeconds = 3;    // at most one PingPOSForPrinting per terminal per 3 s
        private const int KeysPerQuery = 400;     // 4 parameters each, well under SQL Server's 2100

        private static readonly object Gate = new object();
        private static Timer ticker;
        private static int busy;
        private static readonly Dictionary<long, DateTime> pushed = new Dictionary<long, DateTime>();
        private static readonly Dictionary<string, DateTime> lastPing = new Dictionary<string, DateTime>();

        // counters for health.ashx
        public static DateTime StartedAt = DateTime.MinValue;
        public static DateTime LastTick = DateTime.MinValue;
        public static long Ticks, JobsPushed, Nudges, Pings, DbErrors;
        public static string LastError = "";
        public static double LastTickMs;

        public static void EnsureRunning()
        {
            lock (Gate)
            {
                if (ticker != null) return;
                StartedAt = DateTime.Now;
                ticker = new Timer(Tick, null, TickMilliseconds, TickMilliseconds);
                Log.Write("DISPATCHER started, tick " + TickMilliseconds + " ms");
            }
        }

        public static void NoteClaimed(long slno)
        {
            lock (Gate) { pushed[slno] = DateTime.UtcNow.AddYears(1); }   // never re-push a claimed job
        }

        private static void Tick(object state)
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;   // previous tick still running
            DateTime t0 = DateTime.UtcNow;
            try
            {
                Ticks++;
                LastTick = DateTime.Now;
                List<Terminal> terminals = Registry.ConnectedTerminals();
                if (terminals.Count == 0) return;   // nobody connected: no database work at all

                Dictionary<string, Terminal> byKey = new Dictionary<string, Terminal>();
                foreach (Terminal t in terminals) byKey[t.Key] = t;

                IHubContext hub = GlobalHost.ConnectionManager.GetHubContext<EdgeHub>();

                // 1. jobs ready
                List<KeyValuePair<string, JobMessage>> jobs;
                try { jobs = JobsFor(terminals); }
                catch (Exception ex) { DbErrors++; LastError = DateTime.Now.ToString("HH:mm:ss") + " jobs: " + ex.Message; Log.Write("DB ERROR (jobs): " + ex.Message); return; }
                DateTime now = DateTime.UtcNow;
                foreach (KeyValuePair<string, JobMessage> kv in jobs)
                {
                    bool skip;
                    lock (Gate)
                    {
                        DateTime at;
                        skip = pushed.TryGetValue(kv.Value.Slno, out at) && (now - at).TotalSeconds < RepushSeconds;
                        if (!skip) pushed[kv.Value.Slno] = now;
                    }
                    if (skip) continue;
                    Terminal t = byKey[kv.Key];
                    hub.Clients.Group(kv.Key).Job(kv.Value);
                    JobsPushed++;
                    Log.Write("PUSH job slno=" + kv.Value.Slno + " " + kv.Value.PrintText + " file=" + kv.Value.FileName + " printer=" + kv.Value.PrintText2
                        + " -> " + t + " (queued " + kv.Value.QueuedAt.ToString("HH:mm:ss") + ")");
                }

                // 2. print requests waiting to become jobs
                HashSet<string> withRequests;
                try { withRequests = TerminalsWithRequests(terminals); }
                catch (Exception ex) { DbErrors++; LastError = DateTime.Now.ToString("HH:mm:ss") + " requests: " + ex.Message; Log.Write("DB ERROR (requests): " + ex.Message); return; }
                foreach (string key in withRequests)
                {
                    Terminal t = byKey[key];
                    bool skip;
                    lock (Gate)
                    {
                        DateTime at;
                        skip = lastPing.TryGetValue(key, out at) && (now - at).TotalSeconds < PingEverySeconds;
                        if (!skip) lastPing[key] = now;
                    }
                    if (skip) continue;
                    Nudges++;
                    hub.Clients.Group(key).Nudge("print request waiting - building the job");
                    Terminal target = t;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            Pings++;
                            string err;
                            bool hasJobs = PrintServiceClient.PingPOSForPrinting(target, out err);
                            if (err != null) { LastError = DateTime.Now.ToString("HH:mm:ss") + " ping: " + err; Log.Write("PING FAILED " + target + ": " + err); }
                            else Log.Write("PING " + target + " -> jobs " + (hasJobs ? "ready" : "none yet"));
                        }
                        catch (Exception ex) { Log.Write("PING ERROR " + target + ": " + ex.Message); }
                    });
                }

                // housekeeping
                if (Ticks % 300 == 0)
                {
                    lock (Gate)
                    {
                        List<long> old = new List<long>();
                        foreach (KeyValuePair<long, DateTime> kv in pushed) if ((now - kv.Value).TotalHours > 25 && kv.Value < now.AddMonths(6)) old.Add(kv.Key);
                        foreach (long s in old) pushed.Remove(s);
                        if (pushed.Count > 50000) pushed.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = DateTime.Now.ToString("HH:mm:ss") + " tick: " + ex.Message;
                Log.Write("TICK ERROR " + ex);
            }
            finally
            {
                LastTickMs = (DateTime.UtcNow - t0).TotalMilliseconds;
                Interlocked.Exchange(ref busy, 0);
            }
        }

        private static string ConnString { get { return ConfigurationManager.AppSettings["ConnectionString"]; } }

        private static void AddKeys(SqlCommand cmd, List<Terminal> terminals, int start, int n, StringBuilder values)
        {
            for (int i = 0; i < n; i++)
            {
                Terminal t = terminals[start + i];
                if (i > 0) values.Append(",");
                values.Append("(@c" + i + ",@d" + i + ",@p" + i + ",@t" + i + ")");
                cmd.Parameters.Add("@c" + i, SqlDbType.NVarChar, 100).Value = t.CompanyID;
                cmd.Parameters.Add("@d" + i, SqlDbType.NVarChar, 100).Value = t.DivisionID;
                cmd.Parameters.Add("@p" + i, SqlDbType.NVarChar, 100).Value = t.DepartmentID;
                cmd.Parameters.Add("@t" + i, SqlDbType.NVarChar, 200).Value = t.TerminalName;
            }
        }

        /// <summary>POSPrintProcess rows ready for the connected terminals. Same filter as CheckPOSForPrinting.</summary>
        public static List<KeyValuePair<string, JobMessage>> JobsFor(List<Terminal> terminals)
        {
            List<KeyValuePair<string, JobMessage>> list = new List<KeyValuePair<string, JobMessage>>();
            for (int start = 0; start < terminals.Count; start += KeysPerQuery)
            {
                int n = Math.Min(KeysPerQuery, terminals.Count - start);
                StringBuilder values = new StringBuilder();
                using (SqlConnection con = new SqlConnection(ConnString))
                using (SqlCommand cmd = con.CreateCommand())
                {
                    cmd.CommandTimeout = 10;
                    AddKeys(cmd, terminals, start, n, values);
                    cmd.CommandText =
                        "SELECT s.slno, s.CompanyID, s.DivisionID, s.DepartmentID, s.TerminalName, s.PrintText, s.PrintText1, s.PrintText2, s.[FileName], s.OrderNumber, s.[TimeStamp] " +
                        "FROM (VALUES " + values + ") w(c,d,p,t) " +
                        "JOIN [POSPrintProcess] s WITH (NOLOCK) ON s.CompanyID=w.c AND s.DivisionID=w.d AND s.DepartmentID=w.p AND s.TerminalName=w.t " +
                        "WHERE s.[TimeStamp] > GETDATE()-1 AND s.[Read]=1 AND s.Active=1 ORDER BY s.slno";
                    con.Open();
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            JobMessage j = new JobMessage();
                            j.Slno = Convert.ToInt64(r[0]);
                            string key = Terminal.MakeKey(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));
                            j.PrintText = r.IsDBNull(5) ? "" : r.GetString(5);
                            j.PrintText1 = r.IsDBNull(6) ? "" : r.GetString(6);
                            j.PrintText2 = r.IsDBNull(7) ? "" : r.GetString(7);
                            j.FileName = r.IsDBNull(8) ? "" : r.GetString(8);
                            j.OrderNumber = r.IsDBNull(9) ? "" : r.GetString(9);
                            j.QueuedAt = r.IsDBNull(10) ? DateTime.MinValue : r.GetDateTime(10);
                            list.Add(new KeyValuePair<string, JobMessage>(key, j));
                        }
                    }
                }
            }
            return list;
        }

        /// <summary>Terminals with an Active=1 print request in any of the six request tables (what Ping would act on).</summary>
        public static HashSet<string> TerminalsWithRequests(List<Terminal> terminals)
        {
            HashSet<string> hits = new HashSet<string>();
            for (int start = 0; start < terminals.Count; start += KeysPerQuery)
            {
                int n = Math.Min(KeysPerQuery, terminals.Count - start);
                StringBuilder values = new StringBuilder();
                using (SqlConnection con = new SqlConnection(ConnString))
                using (SqlCommand cmd = con.CreateCommand())
                {
                    cmd.CommandTimeout = 10;
                    AddKeys(cmd, terminals, start, n, values);
                    cmd.CommandText =
                        "SELECT w.c, w.d, w.p, w.t FROM (VALUES " + values + ") w(c,d,p,t) WHERE " +
                        "EXISTS (SELECT 1 FROM [POSPrintRequest] r WITH (NOLOCK) WHERE r.CompanyID=w.c AND r.DivisionID=w.d AND r.DepartmentID=w.p AND r.TerminalName=w.t AND r.[TimeStamp] > GETDATE()-1 AND r.Active=1) " +
                        "OR EXISTS (SELECT 1 FROM [POSPaidOutPrintRequest] r WITH (NOLOCK) WHERE r.CompanyID=w.c AND r.DivisionID=w.d AND r.DepartmentID=w.p AND r.TerminalName=w.t AND r.Active=1) " +
                        "OR EXISTS (SELECT 1 FROM [POSShiftPrintRequest] r WITH (NOLOCK) WHERE r.CompanyID=w.c AND r.DivisionID=w.d AND r.DepartmentID=w.p AND r.TerminalName=w.t AND r.printdate > GETDATE()-1 AND r.Active=1) " +
                        "OR EXISTS (SELECT 1 FROM [POSReturnOrderPrintRequest] r WITH (NOLOCK) WHERE r.CompanyID=w.c AND r.DivisionID=w.d AND r.DepartmentID=w.p AND r.TerminalName=w.t AND r.Active=1) " +
                        "OR EXISTS (SELECT 1 FROM [TripPrintRequest] r WITH (NOLOCK) WHERE r.CompanyID=w.c AND r.DivisionID=w.d AND r.DepartmentID=w.p AND r.TerminalName=w.t AND r.Active=1) " +
                        "OR EXISTS (SELECT 1 FROM [ROPrintRequest] r WITH (NOLOCK) WHERE r.CompanyID=w.c AND r.DivisionID=w.d AND r.DepartmentID=w.p AND r.TerminalName=w.t AND r.Active=1)";
                    con.Open();
                    using (SqlDataReader r = cmd.ExecuteReader())
                        while (r.Read()) hits.Add(Terminal.MakeKey(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
                }
            }
            return hits;
        }
    }
}

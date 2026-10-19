using System;
using System.Configuration;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Hubs;

namespace QfEdgeHub
{
    /// <summary>
    /// AB#3958 (epic AB#3961): one persistent connection per shop PC. The print app connects with
    /// ?c=CompanyID&d=DivisionID&p=DepartmentID&t=TerminalName[&k=HubKey] and joins the SignalR group
    /// for that terminal. The Dispatcher pushes to the group:
    ///   Job(JobMessage)   a POSPrintProcess row ready to print (Read=1, Active=1)
    ///   Nudge(reason)     a print request exists and the hub asked the print web service to turn it into jobs
    ///   Alert(text)       a message for the screen (support use)
    ///   Warm(reason)      someone started an order on this terminal (print web service -> signal.ashx): wake the printers
    /// The client calls Hello(appVersion) after connecting and Claimed(slno) after it stored a job.
    /// Nothing here writes to the database.
    /// </summary>
    [HubName("EdgeHub")]
    public class EdgeHub : Hub
    {
        public override Task OnConnected()
        {
            Terminal t = TerminalFromRequest();
            if (t == null)
            {
                Log.Write("REJECT " + Context.ConnectionId + " from " + RemoteIp() + " - missing ids or bad key");
                Clients.Caller.Reject("missing CompanyID/DivisionID/DepartmentID/TerminalName or bad key");
                return base.OnConnected();
            }
            t.ConnectionId = Context.ConnectionId;
            t.RemoteIp = RemoteIp();
            Registry.Add(t);
            Groups.Add(Context.ConnectionId, t.Key);
            Log.Write("CONNECT " + t + " conn=" + Context.ConnectionId + " ip=" + t.RemoteIp + " transport=" + Context.QueryString["transport"]);
            Dispatcher.EnsureRunning();
            return base.OnConnected();
        }

        public override Task OnReconnected()
        {
            Terminal t = Registry.Get(Context.ConnectionId);
            if (t == null)
            {
                t = TerminalFromRequest();
                if (t != null) { t.ConnectionId = Context.ConnectionId; t.RemoteIp = RemoteIp(); Registry.Add(t); Groups.Add(Context.ConnectionId, t.Key); }
            }
            Log.Write("RECONNECT " + (t == null ? "?" : t.ToString()) + " conn=" + Context.ConnectionId);
            return base.OnReconnected();
        }

        public override Task OnDisconnected(bool stopCalled)
        {
            Terminal t = Registry.Remove(Context.ConnectionId);
            Log.Write("DISCONNECT " + (t == null ? "?" : t.ToString()) + " conn=" + Context.ConnectionId + (stopCalled ? " (client stopped)" : " (timed out)"));
            return base.OnDisconnected(stopCalled);
        }

        /// <summary>Print app says who it is. Logged; shown on health.ashx.</summary>
        public void Hello(string appVersion, string machineName)
        {
            Terminal t = Registry.Get(Context.ConnectionId);
            if (t != null) t.AppVersion = (appVersion ?? "") + (string.IsNullOrEmpty(machineName) ? "" : " on " + machineName);
            Log.Write("HELLO " + (t == null ? Context.ConnectionId : t.ToString()) + " " + appVersion + " pc=" + machineName);
        }

        /// <summary>Print app stored the job in its edge queue and claimed it (UpdateReadPOSForPrinting).</summary>
        public void Claimed(long slno)
        {
            Terminal t = Registry.Get(Context.ConnectionId);
            Dispatcher.NoteClaimed(slno);
            Log.Write("CLAIMED slno=" + slno + " by " + (t == null ? Context.ConnectionId : t.ToString()));
        }

        private Terminal TerminalFromRequest()
        {
            string c = Context.QueryString["c"], d = Context.QueryString["d"], p = Context.QueryString["p"], t = Context.QueryString["t"];
            if (string.IsNullOrEmpty(c) || string.IsNullOrEmpty(d) || string.IsNullOrEmpty(p) || string.IsNullOrEmpty(t)) return null;
            if (c.Length > 100 || d.Length > 100 || p.Length > 100 || t.Length > 200) return null;
            string key = ConfigurationManager.AppSettings["HubKey"];
            if (!string.IsNullOrEmpty(key) && !string.Equals(Context.QueryString["k"], key)) return null;
            return new Terminal(c, d, p, t);
        }

        private string RemoteIp()
        {
            try
            {
                object ip;
                if (Context.Request.Environment.TryGetValue("server.RemoteIpAddress", out ip) && ip != null) return ip.ToString();
            }
            catch { }
            return "";
        }
    }
}

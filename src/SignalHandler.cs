using System;
using System.Configuration;
using System.Threading;
using System.Web;
using Microsoft.AspNet.SignalR;

namespace QfEdgeHub
{
    /// <summary>
    /// signal.ashx?type=warm&c=CompanyID&d=DivisionID&p=DepartmentID&t=TerminalName[&k=HubKey]
    /// Server-side entry point for the print web service (AB#3796 warm-ups, AB#3958 push mode):
    /// PrintDispatcher.SignalWarm keeps its warm-up signals in memory and hands them to a PC's next
    /// WaitForPrintJobOrWarm; a PC in push mode never calls that, so the print web service also calls
    /// this handler, and the hub pushes "Warm" to the terminal's group at once. The print app answers by
    /// running its existing StartWarmUp (wake Edge, the print queue, the printer). Nothing prints.
    /// Reply (JSON): { "type": "warm", "connections": N, "pushed": true|false }. connections = 0 means the PC
    /// is not on the hub (it is long-polling) and the print web service's in-memory signal still serves it.
    /// Only "warm" exists today; the rate limit (one per terminal per minute) lives in the caller.
    /// No database work.
    /// </summary>
    public class SignalHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }

        public static long Warms, WarmsPushed;

        public void ProcessRequest(HttpContext ctx)
        {
            ctx.Response.ContentType = "application/json";
            ctx.Response.Cache.SetCacheability(HttpCacheability.NoCache);
            string key = ConfigurationManager.AppSettings["HubKey"];
            if (!string.IsNullOrEmpty(key) && ctx.Request["k"] != key) { ctx.Response.StatusCode = 403; ctx.Response.Write("{\"error\":\"bad key\"}"); return; }
            string type = (ctx.Request["type"] ?? "").ToLowerInvariant();
            string c = ctx.Request["c"] ?? "", d = ctx.Request["d"] ?? "", p = ctx.Request["p"] ?? "", t = ctx.Request["t"] ?? "";
            if (c.Length == 0 || d.Length == 0 || p.Length == 0 || t.Length == 0) { ctx.Response.StatusCode = 400; ctx.Response.Write("{\"error\":\"c, d, p and t are required\"}"); return; }
            if (c.Length > 100 || d.Length > 100 || p.Length > 100 || t.Length > 200) { ctx.Response.StatusCode = 400; ctx.Response.Write("{\"error\":\"id too long\"}"); return; }
            if (type != "warm") { ctx.Response.StatusCode = 404; ctx.Response.Write("{\"error\":\"unknown type\"}"); return; }
            try
            {
                Terminal term = new Terminal(c, d, p, t);
                int connections = Registry.ConnectionsFor(term.Key);
                Interlocked.Increment(ref Warms);
                bool pushed = false;
                if (connections > 0)
                {
                    IHubContext hub = GlobalHost.ConnectionManager.GetHubContext<EdgeHub>();
                    hub.Clients.Group(term.Key).Warm("an order was started on " + t);
                    Interlocked.Increment(ref WarmsPushed);
                    pushed = true;
                }
                Log.Write("WARM " + term + " connections=" + connections + (pushed ? " -> pushed" : " -> nobody on the hub (long-poll serves it)"));
                ctx.Response.Write("{\"type\":\"warm\",\"connections\":" + connections + ",\"pushed\":" + (pushed ? "true" : "false") + "}");
            }
            catch (Exception ex)
            {
                Log.Write("WARM ERROR " + c + "/" + t + ": " + ex.Message);
                ctx.Response.StatusCode = 500;
                ctx.Response.Write("{\"error\":\"server error\"}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using System.Web;

namespace QfEdgeHub
{
    /// <summary>health.ashx - plain text status for a support person. No secrets, no job content.</summary>
    public class HealthHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }

        public void ProcessRequest(HttpContext ctx)
        {
            ctx.Response.ContentType = "text/plain";
            ctx.Response.Cache.SetCacheability(HttpCacheability.NoCache);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("qf-edge-hub " + typeof(HealthHandler).Assembly.GetName().Version + "  server time " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("dispatcher: " + (Dispatcher.StartedAt == DateTime.MinValue ? "idle (no terminal has connected yet)" :
                "started " + Dispatcher.StartedAt.ToString("HH:mm:ss") + ", last tick " + Dispatcher.LastTick.ToString("HH:mm:ss") + " (" + Dispatcher.LastTickMs.ToString("0") + " ms)"));
            sb.AppendLine("ticks " + Dispatcher.Ticks + " | jobs pushed " + Dispatcher.JobsPushed + " | nudges " + Dispatcher.Nudges + " | pings " + Dispatcher.Pings + " | db errors " + Dispatcher.DbErrors);
            if (Dispatcher.LastError.Length > 0) sb.AppendLine("last error: " + Dispatcher.LastError);
            List<Terminal> all = Registry.All();
            sb.AppendLine("connections " + all.Count);
            foreach (Terminal t in all)
                sb.AppendLine("  " + t + "  app=" + t.AppVersion + "  since " + t.ConnectedAt.ToLocalTime().ToString("HH:mm:ss") + "  ip=" + t.RemoteIp);
            ctx.Response.Write(sb.ToString());
        }
    }
}

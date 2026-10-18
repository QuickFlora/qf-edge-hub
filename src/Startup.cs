using System;
using Microsoft.AspNet.SignalR;
using Microsoft.Owin;
using Owin;

[assembly: OwinStartup(typeof(QfEdgeHub.Startup))]

namespace QfEdgeHub
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            // Hub lives at <app>/signalr (the SignalR default; the print app connects to <app> and the
            // client library appends /signalr). Detailed errors stay off: nothing internal reaches a shop PC.
            HubConfiguration cfg = new HubConfiguration();
            cfg.EnableDetailedErrors = false;
            cfg.EnableJavaScriptProxies = false;
            // A PC that stops answering is dropped after 30 s; the client then falls back to its long-poll.
            GlobalHost.Configuration.DisconnectTimeout = TimeSpan.FromSeconds(30);
            GlobalHost.Configuration.KeepAlive = TimeSpan.FromSeconds(10);
            app.MapSignalR("/signalr", cfg);
            Log.Write("STARTUP qf-edge-hub " + typeof(Startup).Assembly.GetName().Version + " signalr mapped");
        }
    }
}

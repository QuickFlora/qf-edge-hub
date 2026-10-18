using System;
using System.IO;
using System.Web.Hosting;

namespace QfEdgeHub
{
    /// <summary>One rolling text log per day under App_Data\Logs. Never throws.</summary>
    public static class Log
    {
        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                string dir = Path.Combine(HostingEnvironment.ApplicationPhysicalPath ?? AppDomain.CurrentDomain.BaseDirectory, "App_Data\\Logs");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "hub_" + DateTime.Now.ToString("yyyy_MM_dd") + ".txt");
                lock (Gate)
                {
                    File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + "\r\n");
                }
                // 14 days kept
                if (DateTime.Now.Second == 0)
                    foreach (string f in Directory.GetFiles(dir, "hub_*.txt"))
                        if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
            }
            catch { }
        }
    }
}

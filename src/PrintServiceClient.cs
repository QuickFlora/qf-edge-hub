using System;
using System.Configuration;
using System.IO;
using System.Net;
using System.Text;

namespace QfEdgeHub
{
    /// <summary>
    /// Calls the EXISTING print web service (NewQFPrintWebService/QFPrintService.asmx) on this server.
    /// PingPOSForPrinting is what turns POSPrintRequest / POSPaidOut / POSShift / POSReturnOrder / Trip /
    /// ROPrintRequest rows into POSPrintProcess jobs (clsAppPrinting.PingStartPOSForPrinting). Until now
    /// every shop PC called it; with the hub, the hub calls it once per request on the PC's behalf, so the
    /// job-building code is untouched and the PC makes no call at all.
    /// Settings: PrintServiceUrl (full .asmx URL), optional PrintServiceHost (Host header when the URL
    /// points at 127.0.0.1 because the site only answers its own host name).
    /// </summary>
    public static class PrintServiceClient
    {
        public static bool PingPOSForPrinting(Terminal t, out string error)
        {
            error = null;
            string url = ConfigurationManager.AppSettings["PrintServiceUrl"];
            if (string.IsNullOrEmpty(url)) { error = "PrintServiceUrl not set"; return false; }
            string host = ConfigurationManager.AppSettings["PrintServiceHost"];
            string body =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<soap:Envelope xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                "<soap:Body><PingPOSForPrinting xmlns=\"https://secure.quickflora.com/\">" +
                "<CompanyID>" + Xml(t.CompanyID) + "</CompanyID><DivisionID>" + Xml(t.DivisionID) + "</DivisionID>" +
                "<DepartmentID>" + Xml(t.DepartmentID) + "</DepartmentID><TerminalName>" + Xml(t.TerminalName) + "</TerminalName>" +
                "</PingPOSForPrinting></soap:Body></soap:Envelope>";
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "text/xml; charset=utf-8";
                req.Headers["SOAPAction"] = "\"https://secure.quickflora.com/PingPOSForPrinting\"";
                req.Timeout = 20000;
                req.ServicePoint.Expect100Continue = false;
                if (!string.IsNullOrEmpty(host)) req.Host = host;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader r = new StreamReader(resp.GetResponseStream()))
                {
                    string xml = r.ReadToEnd();
                    return xml.IndexOf("<PingPOSForPrintingResult>true</PingPOSForPrintingResult>", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (WebException ex)
            {
                string detail = "";
                try { if (ex.Response != null) using (StreamReader r = new StreamReader(ex.Response.GetResponseStream())) detail = r.ReadToEnd(); } catch { }
                if (detail.Length > 300) detail = detail.Substring(0, 300);
                error = ex.Message + (detail.Length > 0 ? " | " + detail : "");
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static string Xml(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}

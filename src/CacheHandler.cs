using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Web;
using Newtonsoft.Json;

namespace QfEdgeHub
{
    /// <summary>
    /// cache.ashx?table=Companies&c=CompanyID&d=DivisionID&p=DepartmentID&since=N[&k=HubKey]
    /// Pull endpoint for the print app's EdgeSync (ICacheSource). Returns
    ///   { "table": "...", "version": N, "rows": [ { "key": "...", "version": n, "data": {...} } ] }
    /// "version" is a change fingerprint, not a rowversion: Companies and InventoryItems have no
    /// rowversion column and adding one is a schema change (Ved, R4), so the hub computes
    /// BINARY_CHECKSUM over the columns it serves, per row, and CHECKSUM_AGG over the set. When the
    /// caller's "since" equals the current set fingerprint the reply carries no rows (nothing changed).
    /// Only shop-facing columns are served - no keys, passwords, bank or tax IDs (epic AB#3961 rule).
    /// </summary>
    public class CacheHandler : IHttpHandler
    {
        public bool IsReusable { get { return true; } }

        private static readonly string[] CompanyColumns = new string[] {
            "CompanyID", "DivisionID", "DepartmentID", "CompanyName", "CompanyAddress1", "CompanyAddress2", "CompanyCity", "CompanyState",
            "CompanyZip", "CompanyCountry", "CompanyPhone", "CompanyFax", "CompanyEmail", "CompanyWebAddress", "CompanyLogoUrl", "CompanyLogoUrl2",
            "CurrencyID", "CurrencySymbol", "GMTOffset", "OffsetHours", "IsActive", "NewPOS", "ManageLiveInventory", "isPackPrint", "PackPrinter",
            "PackItemCount", "IsEnvelope", "TollFreeNumber", "companytype", "IsWholeSaler" };

        public void ProcessRequest(HttpContext ctx)
        {
            ctx.Response.ContentType = "application/json";
            ctx.Response.Cache.SetCacheability(HttpCacheability.NoCache);
            string key = ConfigurationManager.AppSettings["HubKey"];
            if (!string.IsNullOrEmpty(key) && ctx.Request["k"] != key) { ctx.Response.StatusCode = 403; ctx.Response.Write("{\"error\":\"bad key\"}"); return; }
            string table = ctx.Request["table"] ?? "";
            string c = ctx.Request["c"] ?? "", d = ctx.Request["d"] ?? "", p = ctx.Request["p"] ?? "";
            long since = 0; long.TryParse(ctx.Request["since"] ?? "0", out since);
            if (c.Length == 0 || d.Length == 0 || p.Length == 0) { ctx.Response.StatusCode = 400; ctx.Response.Write("{\"error\":\"c, d and p are required\"}"); return; }
            try
            {
                if (string.Equals(table, "Companies", StringComparison.OrdinalIgnoreCase)) ctx.Response.Write(Companies(c, d, p, since));
                else { ctx.Response.StatusCode = 404; ctx.Response.Write("{\"error\":\"unknown table\"}"); }
            }
            catch (Exception ex)
            {
                Log.Write("CACHE ERROR " + table + " " + c + ": " + ex.Message);
                ctx.Response.StatusCode = 500;
                ctx.Response.Write("{\"error\":\"server error\"}");
            }
        }

        private static string Companies(string c, string d, string p, long since)
        {
            string cols = string.Join(",", CompanyColumns);
            using (SqlConnection con = new SqlConnection(ConfigurationManager.AppSettings["ConnectionString"]))
            using (SqlCommand cmd = con.CreateCommand())
            {
                cmd.CommandTimeout = 10;
                cmd.Parameters.Add("@c", SqlDbType.NVarChar, 72).Value = c;
                cmd.Parameters.Add("@d", SqlDbType.NVarChar, 72).Value = d;
                cmd.Parameters.Add("@p", SqlDbType.NVarChar, 72).Value = p;
                cmd.CommandText =
                    "SELECT ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(" + cols + ")),0) FROM [Companies] WITH (NOLOCK) WHERE CompanyID=@c AND DivisionID=@d AND DepartmentID=@p";
                con.Open();
                long version = Convert.ToInt64(cmd.ExecuteScalar());
                Dictionary<string, object> reply = new Dictionary<string, object>();
                reply["table"] = "Companies";
                reply["version"] = version;
                List<object> rows = new List<object>();
                reply["rows"] = rows;
                if (version != since || since == 0)
                {
                    cmd.CommandText = "SELECT BINARY_CHECKSUM(" + cols + ") AS _v, " + cols + " FROM [Companies] WITH (NOLOCK) WHERE CompanyID=@c AND DivisionID=@d AND DepartmentID=@p";
                    using (SqlDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            Dictionary<string, object> data = new Dictionary<string, object>();
                            for (int i = 1; i < r.FieldCount; i++) data[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                            Dictionary<string, object> row = new Dictionary<string, object>();
                            row["key"] = c + "|" + d + "|" + p;
                            row["version"] = Convert.ToInt64(r[0]);
                            row["data"] = data;
                            rows.Add(row);
                        }
                    }
                }
                return JsonConvert.SerializeObject(reply);
            }
        }
    }
}

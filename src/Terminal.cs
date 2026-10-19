using System;

namespace QfEdgeHub
{
    /// <summary>One shop PC: the four IDs the print app has always identified itself with.</summary>
    public sealed class Terminal
    {
        public readonly string CompanyID, DivisionID, DepartmentID, TerminalName;
        public readonly string Key;        // SignalR group name
        public string ConnectionId;
        public string AppVersion = "";
        public string RemoteIp = "";
        public DateTime ConnectedAt = DateTime.UtcNow;

        public Terminal(string companyId, string divisionId, string departmentId, string terminalName)
        {
            CompanyID = companyId ?? ""; DivisionID = divisionId ?? ""; DepartmentID = departmentId ?? ""; TerminalName = terminalName ?? "";
            Key = MakeKey(CompanyID, DivisionID, DepartmentID, TerminalName);
        }

        public static string MakeKey(string c, string d, string p, string t)
        {
            // Same shape as PrintDispatcher.Key in the print web service (ChrW(1) separators).
            return c + "\u0001" + d + "\u0001" + p + "\u0001" + t;
        }

        public string[] Parts { get { return new string[] { CompanyID, DivisionID, DepartmentID, TerminalName }; } }

        public override string ToString() { return CompanyID + "/" + DivisionID + "/" + DepartmentID + "/" + TerminalName; }
    }

    /// <summary>What the hub pushes for one POSPrintProcess row. Same five fields the app reads from CheckPOSForPrinting.</summary>
    public sealed class JobMessage
    {
        public long Slno;
        public string PrintText;    // form / type: Text, PDF, HTML ...
        public string PrintText1;   // where the file is (URL)
        public string PrintText2;   // printer name
        public string FileName;
        public string OrderNumber;
        public DateTime QueuedAt;   // POSPrintProcess.TimeStamp (server time)
    }
}

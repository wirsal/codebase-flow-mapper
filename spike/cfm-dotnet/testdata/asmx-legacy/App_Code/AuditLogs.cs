public interface IAuditLog
{
    void Write(string entry);
}

public static class AuditLogs
{
    // The caller only sees IAuditLog: the concrete type is decided here.
    public static IAuditLog Create()
    {
        return new DbAuditLog();
    }
}

public class DbAuditLog : IAuditLog
{
    public void Write(string entry)
    {
        System.Data.SqlClient.SqlCommand cmd = new System.Data.SqlClient.SqlCommand("insert_audit");
        cmd.ExecuteNonQuery();
    }
}

// Decoy: implements IAuditLog but is never created anywhere.
public class FileAuditLog : IAuditLog
{
    public void Write(string entry)
    {
        System.IO.File.AppendAllText("audit.log", entry);
    }
}

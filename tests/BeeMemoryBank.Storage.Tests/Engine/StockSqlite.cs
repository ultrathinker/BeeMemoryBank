using System.Runtime.InteropServices;

namespace BeeMemoryBank.Storage.Tests.Engine;

/// <summary>
/// The stock SQLite that nodes of 2.5.1 and older ship (the e_sqlite3 native build), driven through the few raw calls a
/// compatibility test needs. The product itself runs on SQLite3 Multiple Ciphers from 2.5.3; this project alone keeps the stock
/// native library next to it (SQLitePCLRaw.lib.e_sqlite3 - the native file only, no SQLitePCLRaw provider, so there is one
/// provider in the process), so a database file written by one engine can be opened by the other in the same test run.
/// </summary>
internal sealed class StockSqlite : IDisposable
{
    private const int OpenReadWrite = 0x2;
    private const int Row = 100;
    private const int Done = 101;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OpenV2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CloseDb(IntPtr db);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PrepareV2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr stmt, IntPtr tail);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Step(IntPtr stmt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ColumnCount(IntPtr stmt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ColumnText(IntPtr stmt, int column);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FinalizeStmt(IntPtr stmt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ErrMsg(IntPtr db);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr LibVersion();

    private static readonly Lazy<IntPtr> Library = new(LoadLibrary);

    private readonly IntPtr _db;

    private StockSqlite(IntPtr db) => _db = db;

    /// <summary>Opens (read-write, an existing file) with the stock engine.</summary>
    public static StockSqlite Open(string path)
    {
        var rc = Fn<OpenV2>("sqlite3_open_v2")(path, out var db, OpenReadWrite, IntPtr.Zero);
        if (rc != 0)
        {
            if (db != IntPtr.Zero) Fn<CloseDb>("sqlite3_close")(db);
            throw new InvalidOperationException($"stock sqlite3_open_v2 failed with {rc}");
        }
        return new StockSqlite(db);
    }

    /// <summary>The version of the stock native library, e.g. "3.53.3".</summary>
    public static string LibraryVersion => Marshal.PtrToStringUTF8(Fn<LibVersion>("sqlite3_libversion")())!;

    /// <summary>Runs one statement and returns every row as text columns.</summary>
    public List<string?[]> Query(string sql)
    {
        var rc = Fn<PrepareV2>("sqlite3_prepare_v2")(_db, sql, -1, out var stmt, IntPtr.Zero);
        if (rc != 0) throw Failed(sql, rc);
        var rows = new List<string?[]>();
        try
        {
            var columns = Fn<ColumnCount>("sqlite3_column_count")(stmt);
            while (true)
            {
                rc = Fn<Step>("sqlite3_step")(stmt);
                if (rc == Done) return rows;
                if (rc != Row) throw Failed(sql, rc);
                var row = new string?[columns];
                for (var i = 0; i < columns; i++)
                    row[i] = Marshal.PtrToStringUTF8(Fn<ColumnText>("sqlite3_column_text")(stmt, i));
                rows.Add(row);
            }
        }
        finally
        {
            Fn<FinalizeStmt>("sqlite3_finalize")(stmt);
        }
    }

    /// <summary>Runs one statement for its effect.</summary>
    public void Execute(string sql) => Query(sql);

    /// <summary>The first column of the first row.</summary>
    public string? Scalar(string sql) => Query(sql).FirstOrDefault()?[0];

    public void Dispose() => Fn<CloseDb>("sqlite3_close")(_db);

    private InvalidOperationException Failed(string sql, int rc) =>
        new($"stock SQLite failed ({rc}: {Marshal.PtrToStringUTF8(Fn<ErrMsg>("sqlite3_errmsg")(_db))}) on: {sql}");

    private static T Fn<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library.Value, name));

    private static IntPtr LoadLibrary()
    {
        var (rid, file) =
            OperatingSystem.IsWindows() ? ("win", "e_sqlite3.dll") :
            OperatingSystem.IsMacOS() ? ("osx", "libe_sqlite3.dylib") :
            ("linux", "libe_sqlite3.so");
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", $"{rid}-{arch}", "native", file);
        return NativeLibrary.Load(path);
    }
}

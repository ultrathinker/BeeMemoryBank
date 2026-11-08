using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BeeMemoryBank.Infrastructure.Network;

/// <summary>The category Windows gives a network (NLM_NETWORK_CATEGORY).</summary>
public enum WindowsNetworkCategory
{
    Public = 0,
    Private = 1,
    DomainAuthenticated = 2,
}

/// <summary>Windows' category of each connected network, by the adapter it is on; an interface so the tests can say what Windows would answer.</summary>
public interface IWindowsNetworkCategories
{
    /// <summary>Adapter id (GUID, "D" form, lower case) to category. Throws when Windows cannot be asked (the caller then falls back to the fingerprints).</summary>
    IReadOnlyDictionary<string, WindowsNetworkCategory> ByAdapterId();
}

/// <summary>
/// Reads the categories from the Network List Manager (<c>INetworkListManager</c>, netprofm), the same source as Settings &gt; Network &gt; "Network
/// profile type". Plain COM interop: the Node project targets plain <c>net10.0</c>, and the built-in COM support of the runtime on Windows is all this needs.
/// A connection whose network or adapter cannot be read is left out (its addresses then fall back to the person's answer).
/// </summary>
public sealed class WindowsNetworkCategories : IWindowsNetworkCategories
{
    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    public IReadOnlyDictionary<string, WindowsNetworkCategory> ByAdapterId()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Network categories exist only on Windows.");
        return Read();
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, WindowsNetworkCategory> Read()
    {
        var result = new Dictionary<string, WindowsNetworkCategory>(StringComparer.OrdinalIgnoreCase);
        var type = Type.GetTypeFromCLSID(NetworkListManagerClsid, throwOnError: true)!;
        var manager = (INetworkListManager)Activator.CreateInstance(type)!;
        try
        {
            var connections = manager.GetNetworkConnections();
            try
            {
                while (connections.Next(1, out var connection, out var fetched) == 0 && fetched == 1 && connection is not null)
                {
                    try
                    {
                        var adapter = connection.GetAdapterId();
                        var network = connection.GetNetwork();
                        try
                        {
                            var category = (WindowsNetworkCategory)network.GetCategory();
                            var key = adapter.ToString("D");
                            // One adapter, two networks (rare): the less trusting answer wins.
                            if (!result.TryGetValue(key, out var seen) || category < seen) result[key] = category;
                        }
                        finally { Marshal.ReleaseComObject(network); }
                    }
                    catch (COMException) { }
                    finally { Marshal.ReleaseComObject(connection); }
                }
            }
            finally { Marshal.ReleaseComObject(connections); }
        }
        finally { Marshal.ReleaseComObject(manager); }
        return result;
    }

    // ── netlistmgr.idl, in vtable order (dual interfaces; the IDispatch methods come first and are supplied by the runtime) ──

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNetwork(Guid networkId);
        IEnumNetworkConnections GetNetworkConnections();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNetworkConnection(Guid connectionId);
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        object NewEnum { [return: MarshalAs(UnmanagedType.IUnknown)] get; }
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out INetworkConnection? connection, out uint fetched);
        void Skip(uint celt);
        void Reset();
        IEnumNetworkConnections Clone();
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
        int GetDomainType();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        IEnumNetworkConnections GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint lowCreated, out uint highCreated, out uint lowConnected, out uint highConnected);
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        int GetCategory();
        void SetCategory(int category);
    }
}

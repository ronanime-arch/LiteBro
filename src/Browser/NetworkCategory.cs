using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace LiteBro;

/// <summary>
/// Whether Windows calls the network of an address public. The firewall rules for the phone (SSH, finding the computer)
/// are for private networks only, so on a public network the phone finds nothing: the dialog says so.
/// Network List Manager, no process started.
/// </summary>
static class NetworkCategory
{
    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface INetworkListManager
    {
        object GetNetworks(int flags);
        object GetNetwork(Guid id);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IEnumNetworkConnections
    {
        [DispId(-4)] object NewEnum { get; }
        [PreserveSig] int Next(int celt, [MarshalAs(UnmanagedType.Interface)] out INetworkConnection connection, out int fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface INetworkConnection
    {
        INetwork GetNetwork();
        bool IsConnectedToInternet { get; }
        bool IsConnected { get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface INetwork
    {
        string GetName();
        void SetName(string name);
        string GetDescription();
        void SetDescription(string description);
        Guid GetNetworkId();
        int GetDomainType();
        object GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
        bool IsConnectedToInternet { get; }
        bool IsConnected { get; }
        int GetConnectivity();
        int GetCategory(); // 0 public, 1 private, 2 domain
    }

    /// <summary>The name of the network the address is in when Windows treats it as public; null when private, or unknown.</summary>
    public static string? PublicNetworkOf(string? ip)
    {
        if (ip == null) return null;
        object? manager = null;
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == ip));
            if (adapter == null || !Guid.TryParse(adapter.Id, out var adapterId)) return null;
            manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!);
            var connections = ((INetworkListManager)manager!).GetNetworkConnections();
            try
            {
                while (connections.Next(1, out var connection, out var fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        if (connection.GetAdapterId() != adapterId) continue;
                        var network = connection.GetNetwork();
                        try { return network.GetCategory() == 0 ? network.GetName() : null; }
                        finally { Marshal.ReleaseComObject(network); }
                    }
                    finally { Marshal.ReleaseComObject(connection); }
                }
            }
            finally { Marshal.ReleaseComObject(connections); }
        }
        catch (Exception e) when (e is COMException || e is InvalidCastException || e is ArgumentException || e is NetworkInformationException) { }
        finally { if (manager != null) Marshal.ReleaseComObject(manager); }
        return null;
    }
}

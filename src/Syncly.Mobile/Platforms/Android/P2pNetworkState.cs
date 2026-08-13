using Android.Content;
using Android.Net;
using Log = Android.Util.Log;

namespace Syncly.Mobile;

/// <summary>
/// Android 10+ will not send TCP over a Wi-Fi Direct interface unless the process is bound to
/// that network. Without this, same-LAN sync works (STA) and cross-network P2P looks dead.
/// </summary>
internal static class P2pNetworkState
{
    public const string Tag = "SynclyP2p";

    public static Network? Network { get; set; }

    public static string? InterfaceName { get; set; }

    public static string? GroupOwnerAddress { get; set; }

    public static IDisposable BindProcessFor(string address)
    {
        if (!ShouldBind(address))
            return Nop.Instance;

        var network = Network ?? FindP2pNetwork();
        if (network is null)
        {
            Log.Warn(Tag, $"No P2P network to bind for {address}.");
            return Nop.Instance;
        }

        var cm = Connectivity();
        if (cm is null)
            return Nop.Instance;

        cm.BindProcessToNetwork(network);
        Log.Info(Tag, $"Bound process to P2P network for {address}.");
        return new Unbind(cm);
    }

    public static Network? FindP2pNetwork()
    {
        var cm = Connectivity();
        if (cm?.GetAllNetworks() is not { } networks)
            return null;

        foreach (var network in networks)
        {
            var link = cm.GetLinkProperties(network);
            var name = link?.InterfaceName ?? "";
            if (name.Contains("p2p", StringComparison.OrdinalIgnoreCase))
                return network;

            if (link?.LinkAddresses is null)
                continue;

            foreach (var addr in link.LinkAddresses)
            {
                var host = addr.Address?.HostAddress;
                if (host is not null && host.Contains("192.168.49.", StringComparison.Ordinal))
                    return network;
            }
        }

        return null;
    }

    private static bool ShouldBind(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        if (string.Equals(address, GroupOwnerAddress, StringComparison.Ordinal))
            return true;

        return address.StartsWith("192.168.49.", StringComparison.Ordinal);
    }

    private static ConnectivityManager? Connectivity() =>
        Android.App.Application.Context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;

    private sealed class Unbind(ConnectivityManager cm) : IDisposable
    {
        public void Dispose()
        {
            try { cm.BindProcessToNetwork(null); }
            catch { /* restoring the default network is best-effort */ }
        }
    }

    private sealed class Nop : IDisposable
    {
        public static readonly Nop Instance = new();
        public void Dispose() { }
    }
}

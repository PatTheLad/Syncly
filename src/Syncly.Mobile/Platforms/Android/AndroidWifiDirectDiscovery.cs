using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.Net.Wifi.P2p;
using Android.Net.Wifi.P2p.Nsd;
using Android.OS;
using Syncly.Model;
using Syncly.Sync;
using Manifest = Android.Manifest;
using Log = Android.Util.Log;

namespace Syncly.Mobile;

/// <summary>
/// Wi-Fi Direct discovery that does not need a shared router.
///
/// DNS-SD identity is encoded in the service instance name so we still see peers when TXT
/// records never arrive. After a group forms, TCP is bound onto the P2P network (Android 10+
/// otherwise keeps using the other Wi-Fi and the connect goes nowhere).
/// </summary>
public sealed class AndroidWifiDirectDiscovery : IPeerDiscovery
{
    public const string ServiceType = "_syncly._tcp";
    private const string Tag = P2pNetworkState.Tag;
    private static readonly Regex InstanceName = new(
        @"^s-([0-9a-f]{32})-(\d+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, DiscoveredPeer> _peers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _macToId = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _lifetime;
    private DeviceDescriptor? _self;
    private int _port;
    private WifiP2pManager? _manager;
    private WifiP2pManager.Channel? _channel;
    private P2pReceiver? _receiver;
    private bool _advertised;
    private string _status = "Starting nearby…";

    public string Name => "Wi-Fi Direct (Android)";

    public bool IsAvailable => _channel is not null;

    public string Status => _status;

    public IReadOnlyList<DiscoveredPeer> Peers => _peers.Values.ToList();

    public event Action<DiscoveredPeer>? PeerAppeared;

    public event Action<string>? PeerDisappeared;

    public Task StartAsync(DeviceDescriptor self, int listenPort, CancellationToken ct = default)
    {
        if (_lifetime is not null)
            return Task.CompletedTask;

        _self = self;
        _port = listenPort > 0 ? listenPort : 45_654;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task RequestAccessAsync(CancellationToken ct = default)
    {
        await WaitForActivityAsync(ct);
        await RequestPermissionsAsync();

        if (!HasPermissions())
        {
            SetStatus("Allow nearby devices (and Location) to find phones off this Wi-Fi.");
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (_channel is null)
                InitializeOnUi();
            else
                DiscoverOnUi();
        });
    }

    public async Task InviteAsync(DiscoveredPeer peer, CancellationToken ct = default)
    {
        if (PeerEndpoints.IsRoutable(peer.Address) || _manager is null || _channel is null)
            return;

        var mac = MacOf(peer);
        Log.Info(Tag, $"Inviting {peer.DisplayName} at {mac}");
        SetStatus("Accept the Wi-Fi Direct popup on the other device.");

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var config = new WifiP2pConfig { DeviceAddress = mac };
            config.Wps ??= new WpsInfo();
            config.Wps.Setup = WpsInfo.Pbc;
            _manager.Connect(_channel, config, new P2pAction(
                () => Log.Info(Tag, "Connect issued."),
                reason =>
                {
                    Log.Warn(Tag, $"Connect failed: {reason}");
                    SetStatus($"Wi-Fi Direct connect failed ({reason}). Try again.");
                }));
        });

        for (var i = 0; i < 20 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(500, ct);
            await MainThread.InvokeOnMainThreadAsync(OnConnectionChanged);
            if (_peers.TryGetValue(peer.DeviceId, out var updated) && PeerEndpoints.IsRoutable(updated.Address))
                return;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_lifetime is not null)
            await _lifetime.CancelAsync();

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (_manager is { } manager && _channel is { } channel)
                {
                    manager.StopPeerDiscovery(channel, new P2pAction());
                    manager.ClearLocalServices(channel, new P2pAction());
                    manager.ClearServiceRequests(channel, new P2pAction());
                    manager.RemoveGroup(channel, new P2pAction());
                }

                if (_receiver is not null)
                {
                    try
                    {
                        Android.App.Application.Context.UnregisterReceiver(_receiver);
                    }
                    catch (ArgumentException) { /* already unregistered */ }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "Shutdown: " + ex.Message);
        }

        foreach (var id in _peers.Keys)
            PeerDisappeared?.Invoke(id);

        _receiver = null;
        _manager = null;
        _channel = null;
        _advertised = false;
        _lifetime?.Dispose();
        _lifetime = null;
        _peers.Clear();
        _macToId.Clear();
        P2pNetworkState.Network = null;
        P2pNetworkState.InterfaceName = null;
        P2pNetworkState.GroupOwnerAddress = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await WaitForActivityAsync(ct);
        }
        catch (System.OperationCanceledException)
        {
            return;
        }

        if (HasPermissions())
        {
            try
            {
                await MainThread.InvokeOnMainThreadAsync(InitializeOnUi);
            }
            catch (Exception ex)
            {
                Log.Warn(Tag, "Init failed: " + ex);
                SetStatus("Wi-Fi Direct could not start.");
            }
        }
        else
        {
            SetStatus("Open Devices and allow nearby access.");
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_channel is not null)
                    await MainThread.InvokeOnMainThreadAsync(DiscoverOnUi);

                await Task.Delay(TimeSpan.FromSeconds(8), ct);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn(Tag, "Rediscover: " + ex.Message);
            }
        }
    }

    private static async Task WaitForActivityAsync(CancellationToken ct)
    {
        for (var i = 0; i < 40 && Platform.CurrentActivity is null; i++)
            await Task.Delay(250, ct);

        if (Platform.CurrentActivity is null)
            throw new InvalidOperationException("No Android activity yet.");
    }

    private static bool HasPermissions()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            return Permissions.CheckStatusAsync<NearbyWifiPermission>().GetAwaiter().GetResult()
                   == PermissionStatus.Granted;

        return Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>().GetAwaiter().GetResult()
               == PermissionStatus.Granted;
    }

    private static async Task RequestPermissionsAsync()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            await Permissions.RequestAsync<NearbyWifiPermission>();
            await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }
        else
        {
            await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }
    }

    private void InitializeOnUi()
    {
        if (_channel is not null)
            return;

        var context = Android.App.Application.Context;
        _manager = (WifiP2pManager?)context.GetSystemService(Context.WifiP2pService)
                   ?? throw new InvalidOperationException("Wi-Fi P2P is not available.");

        _channel = _manager.Initialize(context, Looper.MainLooper, null)
                   ?? throw new InvalidOperationException("Could not open a Wi-Fi P2P channel.");

        _manager.SetDnsSdResponseListeners(_channel, new ServiceListener(this), new TxtListener(this));

        _receiver = new P2pReceiver(this);
        var filter = new IntentFilter();
        filter.AddAction(WifiP2pManager.WifiP2pStateChangedAction);
        filter.AddAction(WifiP2pManager.WifiP2pPeersChangedAction);
        filter.AddAction(WifiP2pManager.WifiP2pConnectionChangedAction);
        filter.AddAction(WifiP2pManager.WifiP2pThisDeviceChangedAction);

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            context.RegisterReceiver(_receiver, filter, ReceiverFlags.Exported);
        else
            context.RegisterReceiver(_receiver, filter);

        AdvertiseOnUi();
        DiscoverOnUi();
        SetStatus("Looking for nearby devices…");
        Log.Info(Tag, "Wi-Fi Direct started.");
    }

    private void AdvertiseOnUi()
    {
        if (_advertised || _manager is null || _channel is null || _self is null)
            return;

        var record = new Dictionary<string, string>
        {
            ["id"] = _self.DeviceId,
            ["n"] = Truncate(_self.DisplayName, 32),
            ["p"] = _port.ToString(),
        };

        var info = WifiP2pDnsSdServiceInfo.NewInstance(InstanceLabel(_self.DeviceId, _port), ServiceType, record);
        _manager.AddLocalService(_channel, info, new P2pAction(
            () =>
            {
                _advertised = true;
                Log.Info(Tag, "Advertising " + ServiceType);
            },
            reason =>
            {
                Log.Warn(Tag, "Advertise failed: " + reason);
                SetStatus($"Could not advertise nearby ({reason}).");
            }));
    }

    private void DiscoverOnUi()
    {
        if (_manager is null || _channel is null)
            return;

        // Unfiltered request: type filters miss peers on several OEMs. Identity is in the instance name.
        var request = WifiP2pDnsSdServiceRequest.NewInstance();
        _manager.ClearServiceRequests(_channel, new P2pAction(() =>
            _manager.AddServiceRequest(_channel, request, new P2pAction(
                () => _manager.DiscoverPeers(_channel, new P2pAction(
                    () => _manager.DiscoverServices(_channel, new P2pAction(
                        () => Log.Debug(Tag, "DiscoverServices running."),
                        reason => Log.Warn(Tag, "DiscoverServices: " + reason))),
                    reason => Log.Warn(Tag, "DiscoverPeers: " + reason))),
                reason => Log.Warn(Tag, "AddServiceRequest: " + reason))),
            reason => Log.Warn(Tag, "ClearServiceRequests: " + reason)));
    }

    private void OnPeersChanged()
    {
        _manager?.RequestPeers(_channel, new PeerListListener(this));
    }

    private void OnService(string? instanceName, string? registrationType, WifiP2pDevice? device)
    {
        Log.Info(Tag, $"Service {instanceName} ({registrationType}) from {device?.DeviceAddress}");
        if (device?.DeviceAddress is not { Length: > 0 } mac || _self is null)
            return;

        if (instanceName is not null && InstanceName.Match(instanceName) is { Success: true } match)
        {
            var id = match.Groups[1].Value;
            if (id == _self.DeviceId)
                return;

            var port = int.TryParse(match.Groups[2].Value, out var parsed) ? parsed : _port;
            _macToId[mac] = id;
            Remember(new DiscoveredPeer(
                id,
                device.DeviceName ?? "Syncly device",
                mac,
                port,
                PeerTransport.WifiDirect,
                DateTimeOffset.UtcNow));
            return;
        }

        if (registrationType is not null
            && registrationType.Contains("syncly", StringComparison.OrdinalIgnoreCase))
        {
            var fallbackId = _macToId.GetValueOrDefault(mac) ?? $"p2p:{mac}";
            _macToId[mac] = fallbackId;
            Remember(new DiscoveredPeer(
                fallbackId,
                string.IsNullOrWhiteSpace(instanceName) ? (device.DeviceName ?? "Syncly device") : instanceName,
                mac,
                _port,
                PeerTransport.WifiDirect,
                DateTimeOffset.UtcNow));
        }
    }

    private void OnTxtRecord(string mac, IDictionary<string, string> record)
    {
        if (_self is null)
            return;

        Log.Info(Tag, $"TXT from {mac}: {string.Join(',', record.Select(kv => kv.Key + '=' + kv.Value))}");

        if (!record.TryGetValue("id", out var id) || id == _self.DeviceId)
            return;

        record.TryGetValue("n", out var name);
        var port = record.TryGetValue("p", out var portText) && int.TryParse(portText, out var parsed)
            ? parsed
            : _port;

        ReplaceId(mac, id);
        Remember(new DiscoveredPeer(
            id,
            string.IsNullOrWhiteSpace(name) ? "Syncly device" : name,
            mac,
            port,
            PeerTransport.WifiDirect,
            DateTimeOffset.UtcNow));
    }

    private void OnConnectionChanged()
    {
        if (_manager is null || _channel is null)
            return;

        _manager.RequestConnectionInfo(_channel, new ConnectionListener(this));
        _manager.RequestGroupInfo(_channel, new GroupListener(this));
    }

    private void OnGroup(WifiP2pGroup? group)
    {
        if (group is null)
            return;

        P2pNetworkState.InterfaceName = group.Interface;
        P2pNetworkState.Network = P2pNetworkState.FindP2pNetwork();
        Log.Info(Tag, $"Group iface={group.Interface} owner={group.Owner?.DeviceAddress} net={P2pNetworkState.Network}");

        if (group.Owner?.DeviceAddress is { Length: > 0 } mac)
            OnConnected(null, mac);
    }

    private void OnConnectionInfo(WifiP2pInfo? info)
    {
        if (info is not { GroupFormed: true })
            return;

        var ip = NormalizeIp(info.GroupOwnerAddress?.HostAddress);
        P2pNetworkState.GroupOwnerAddress = ip;
        P2pNetworkState.Network ??= P2pNetworkState.FindP2pNetwork();
        Log.Info(Tag, $"Group formed go={ip} isGO={info.IsGroupOwner}");

        if (info.IsGroupOwner)
        {
            SetStatus("Waiting for the other device to join…");
            return;
        }

        if (ip is not null)
            OnConnected(ip, null);
    }

    private void OnConnected(string? ownerIp, string? ownerMac)
    {
        if (ownerIp is not null)
            P2pNetworkState.GroupOwnerAddress = ownerIp;

        var ip = ownerIp ?? P2pNetworkState.GroupOwnerAddress;
        if (ip is null || !PeerEndpoints.IsRoutable(ip))
            return;

        DiscoveredPeer? found = null;
        if (ownerMac is not null && _macToId.TryGetValue(ownerMac, out var id))
            _peers.TryGetValue(id, out found);

        found ??= _peers.Values.OrderByDescending(p => p.SeenAt).FirstOrDefault();
        if (found is null)
            return;

        Remember(found with
        {
            Address = ip,
            Transport = PeerTransport.WifiDirect,
            SeenAt = DateTimeOffset.UtcNow,
        });
        SetStatus("Nearby link up.");
    }

    private void ReplaceId(string mac, string id)
    {
        if (_macToId.TryGetValue(mac, out var previous) && previous != id && _peers.TryRemove(previous, out _))
            PeerDisappeared?.Invoke(previous);

        _macToId[mac] = id;
    }

    private void Remember(DiscoveredPeer peer)
    {
        var isNew = !_peers.ContainsKey(peer.DeviceId);
        var previous = _peers.GetValueOrDefault(peer.DeviceId);
        _peers[peer.DeviceId] = peer;

        if (isNew || previous?.Address != peer.Address)
        {
            Log.Info(Tag, $"Nearby {peer.DisplayName} at {peer.Endpoint}");
            SetStatus($"Found {peer.DisplayName}.");
            PeerAppeared?.Invoke(peer);
        }
    }

    private void SetStatus(string status)
    {
        _status = status;
        PeerDisappeared?.Invoke("");
    }

    private string MacOf(DiscoveredPeer peer)
    {
        if (!PeerEndpoints.IsRoutable(peer.Address))
            return peer.Address;

        foreach (var (mac, id) in _macToId)
        {
            if (id == peer.DeviceId)
                return mac;
        }

        return peer.Address;
    }

    private static string InstanceLabel(string deviceId, int port) => $"s-{deviceId}-{port}";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string? NormalizeIp(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var pct = host.IndexOf('%');
        if (pct >= 0)
            host = host[..pct];

        const string mapped = "::ffff:";
        if (host.StartsWith(mapped, StringComparison.OrdinalIgnoreCase))
            host = host[mapped.Length..];

        return host;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private sealed class NearbyWifiPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        [
            (Manifest.Permission.NearbyWifiDevices, true),
        ];
    }

    private sealed class P2pAction(Action? ok = null, Action<string>? fail = null)
        : Java.Lang.Object, WifiP2pManager.IActionListener
    {
        public void OnSuccess() => ok?.Invoke();

        public void OnFailure(WifiP2pFailureReason reason) => fail?.Invoke(reason.ToString());
    }

    private sealed class TxtListener(AndroidWifiDirectDiscovery owner)
        : Java.Lang.Object, WifiP2pManager.IDnsSdTxtRecordListener
    {
        public void OnDnsSdTxtRecordAvailable(
            string? fullDomainName,
            IDictionary<string, string>? txtRecordMap,
            WifiP2pDevice? srcDevice)
        {
            if (srcDevice?.DeviceAddress is { Length: > 0 } mac && txtRecordMap is not null)
                owner.OnTxtRecord(mac, txtRecordMap);
        }
    }

    private sealed class ServiceListener(AndroidWifiDirectDiscovery owner)
        : Java.Lang.Object, WifiP2pManager.IDnsSdServiceResponseListener
    {
        public void OnDnsSdServiceAvailable(
            string? instanceName,
            string? registrationType,
            WifiP2pDevice? srcDevice) =>
            owner.OnService(instanceName, registrationType, srcDevice);
    }

    private sealed class ConnectionListener(AndroidWifiDirectDiscovery owner)
        : Java.Lang.Object, WifiP2pManager.IConnectionInfoListener
    {
        public void OnConnectionInfoAvailable(WifiP2pInfo? info) => owner.OnConnectionInfo(info);
    }

    private sealed class GroupListener(AndroidWifiDirectDiscovery owner)
        : Java.Lang.Object, WifiP2pManager.IGroupInfoListener
    {
        public void OnGroupInfoAvailable(WifiP2pGroup? group) => owner.OnGroup(group);
    }

    private sealed class PeerListListener(AndroidWifiDirectDiscovery owner)
        : Java.Lang.Object, WifiP2pManager.IPeerListListener
    {
        public void OnPeersAvailable(WifiP2pDeviceList? peers)
        {
            _ = owner;
            if (peers?.DeviceList is null)
                return;

            foreach (var device in peers.DeviceList)
                Log.Debug(Tag, $"P2P peer {device.DeviceName} {device.DeviceAddress} {device.Status}");
        }
    }

    private sealed class P2pReceiver(AndroidWifiDirectDiscovery owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            switch (intent?.Action)
            {
                case WifiP2pManager.WifiP2pConnectionChangedAction:
                    owner.OnConnectionChanged();
                    break;

                case WifiP2pManager.WifiP2pPeersChangedAction:
                    owner.OnPeersChanged();
                    break;
            }
        }
    }
}

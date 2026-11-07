using System.Net.NetworkInformation;

namespace RemoteControlServer.Services;

public sealed record AdapterInfo(string Name, string Mac, string? Ipv4, string Broadcast, bool Wireless);

public static class NetworkAdapters
{
    /// <summary>
    /// Whether Windows allows each physical adapter to wake the PC (MSPower_DeviceWakeEnable). Keyed by adapter name;
    /// null when Windows does not report it (e.g. not readable without admin rights on this machine).
    /// </summary>
    public static Dictionary<string, bool?> WakeOnLanEnabled()
    {
        var result = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var wake = new List<(string inst, bool en)>();
            using (var s = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, Enable FROM MSPower_DeviceWakeEnable"))
                foreach (System.Management.ManagementObject o in s.Get()) wake.Add(((string)o["InstanceName"], (bool)o["Enable"]));
            using var adapters = new System.Management.ManagementObjectSearcher("SELECT NetConnectionID, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter=TRUE AND NetConnectionID IS NOT NULL");
            foreach (System.Management.ManagementObject a in adapters.Get())
            {
                var name = (string)a["NetConnectionID"]; var pnp = (a["PNPDeviceID"] as string) ?? "";
                var m = wake.FirstOrDefault(w => pnp.Length > 0 && w.inst.StartsWith(pnp, StringComparison.OrdinalIgnoreCase));
                result[name] = m.inst == null ? null : m.en;
            }
        }
        catch (Exception ex) { AppLog.Warn("Wake-on-LAN status unavailable: " + ex.Message); }
        return result;
    }

    /// <summary>Physical (Ethernet / Wi-Fi) adapters that are up, with their MAC, IPv4 address and broadcast address.</summary>
    public static List<AdapterInfo> MacAddresses()
    {
        var list = new List<AdapterInfo>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            if (n.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)) continue;
            var mac = n.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length != 6) continue;
            var v4 = n.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            string broadcast = "255.255.255.255";
            if (v4 != null && v4.IPv4Mask != null)
            {
                var a = v4.Address.GetAddressBytes(); var m = v4.IPv4Mask.GetAddressBytes();
                broadcast = string.Join('.', a.Select((x, i) => (byte)(x | ~m[i])));
            }
            list.Add(new AdapterInfo(n.Name, string.Join(':', mac.Select(b => b.ToString("X2"))), v4?.Address.ToString(), broadcast,
                n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211));
        }
        return list;
    }
}

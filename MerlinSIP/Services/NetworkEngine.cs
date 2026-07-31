using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace MerlinSip.Services;

public class NetworkEngine
{
	public delegate void LogHandler(string message, bool isError = false);

	public delegate void ProgressHandler(string testName, string status, string details);

	public delegate void TestCompleteHandler(bool success, int score);

	public class LocalNetworkInfo
	{
		public string Status { get; set; } = "Disconnected";

		public string IpAddress { get; set; } = "-";

		public string SubnetMask { get; set; } = "-";

		public string Gateway { get; set; } = "-";

		public string DnsServers { get; set; } = "-";

		public string Vlan { get; set; } = "Untagged";

		public string WifiInfo { get; set; } = "-";

		public string PublicIpAddress { get; set; } = "-";

		public bool IsOk { get; set; }
	}

	private CancellationTokenSource? _cts;

	private readonly string[] GoogleDnsServers = new string[2] { "8.8.8.8", "8.8.4.4" };

	private readonly (string host, string ip)[] PrimaryStunServers = new(string, string)[1]
	{
		("pbx.chriskendall.media", "")
	};

	private (string host, string ip)[] GetGoogleStunServers()
	{
		return new(string, string)[5]
		{
			("stun.l.google.com", "74.125.250.129"),
			("stun1.l.google.com", "74.125.250.129"),
			("stun2.l.google.com", "74.125.250.129"),
			("stun3.l.google.com", "74.125.250.129"),
			("stun4.l.google.com", "74.125.250.129")
		};
	}

	private static readonly HttpClient _sharedHttpClient = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(5L)
	};

	public string DomainToCheck { get; set; } = "pbx.chriskendall.media";

	public string LocalSipPortStr { get; set; } = "5060";

	public string SipAlgServer { get; set; } = "sip.linphone.org";

	public int SipAlgPort { get; set; } = 5060;

	public int LocalSipPort { get; set; } = 5060;

	public string StunServer { get; set; } = "pbx.chriskendall.media";

	public int StunPort { get; set; } = 3478;

	public bool IsSimulationMode { get; set; }

	public string PublicIpAddress { get; set; } = "-";

	public double LastDownloadMbps { get; set; }

	public double LastUploadMbps { get; set; }

	public bool[] SelectedTests { get; set; } = new bool[10] { true, true, true, true, true, true, true, true, true, true };

	public PcapCapturer Pcap { get; } = new PcapCapturer();

	public string ServerUsername { get; set; } = "";

	public string ClientToken { get; private set; } = "";

	public int ClientUserId { get; set; }

	public string PresenceUrl { get; set; } = "https://pbx.chriskendall.media";

	public string SignallingUrl { get; set; } = "https://pbx.chriskendall.media";

	public string RoomsUrl { get; set; } = "https://pbx.chriskendall.media";

	public bool HasLocalConnectivityIssue { get; private set; }

	public string LocalConnectivityIssueReason { get; private set; } = "";

	public event LogHandler? OnLog;

	public event ProgressHandler? OnProgress;

	public event TestCompleteHandler? OnComplete;

	public NetworkEngine()
	{
		LoadSettingsFromRegistry();
		NetworkChange.NetworkAddressChanged += (sender, args) =>
		{
			HasLocalConnectivityIssue = true;
			LocalConnectivityIssueReason = "Network interface address changed (Failover)";
			Log("Network address change detected. Triggering failover evaluation...");
		};
	}

	private static Process[]? _cachedProcesses;
	private static DateTime _lastProcessCacheTime = DateTime.MinValue;
	private static readonly object _processCacheLock = new object();

	private static Process[] GetCachedProcesses()
	{
		lock (_processCacheLock)
		{
			if (_cachedProcesses == null || (DateTime.Now - _lastProcessCacheTime).TotalSeconds > 30)
			{
				_cachedProcesses = Process.GetProcesses();
				_lastProcessCacheTime = DateTime.Now;
			}
			return _cachedProcesses;
		}
	}

	private void LoadSettingsFromRegistry()
	{
		try
		{
			using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\DMC\\WindowsSoftphone\\v1"))
			{
				if (registryKey != null)
				{
					ServerUsername = registryKey.GetValue("ServerUsername")?.ToString() ?? "";
					string encryptedToken = registryKey.GetValue("ClientToken")?.ToString() ?? "";
					if (!string.IsNullOrEmpty(encryptedToken))
					{
						try
						{
							var bytes = Convert.FromBase64String(encryptedToken);
							var unprotected = System.Security.Cryptography.ProtectedData.Unprotect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
							ClientToken = Encoding.UTF8.GetString(unprotected);
						}
						catch
						{
							ClientToken = encryptedToken; // Fallback if not encrypted yet
						}
					}

					if (int.TryParse(registryKey.GetValue("ClientUserID")?.ToString(), out var result))
					{
						ClientUserId = result;
					}
				}
			}
			using (RegistryKey registryKey2 = Registry.CurrentUser.OpenSubKey("Software\\DMC\\WindowsSoftphone\\v1\\Services"))
			{
				if (registryKey2 != null)
				{
					PresenceUrl = registryKey2.GetValue("Presence")?.ToString() ?? PresenceUrl;
					SignallingUrl = registryKey2.GetValue("Signalling")?.ToString() ?? SignallingUrl;
					RoomsUrl = registryKey2.GetValue("Rooms")?.ToString() ?? RoomsUrl;
				}
			}
			Log($"Registry configuration loaded. Username: {ServerUsername}, UserID: {ClientUserId}");
		}
		catch (Exception ex)
		{
			Log("Failed to load registry settings: " + ex.Message, isError: true);
		}
	}

	private async Task RunEnvironmentScanAsync(CancellationToken token)
	{
		Log("=================================================================");
		Log("ENVIRONMENT & NETWORK ADAPTER SCAN");
		Log("=================================================================");
		if (!string.IsNullOrEmpty(ServerUsername))
		{
			bool value = !string.IsNullOrEmpty(ClientToken);
			Log($"Registry credentials: Username={ServerUsername}, UserID={ClientUserId}, AuthTokenPresent={value}");
		}
		else
		{
			Log("Registry credentials: None found. Softphone may not be registered.");
		}
		try
		{
			bool flag = false;
			Process[] processes = GetCachedProcesses();
			foreach (Process process in processes)
			{
				try 
				{
					string text = process.ProcessName.ToLower();
					if (text.Contains("merlinsip") || text.Contains("merlin") || text.Contains("sip"))
					{
						Log($"Active voice client process: '{process.ProcessName}' (PID: {process.Id}) is running.");
						flag = true;
					}
				}
				catch { }
			}
			if (!flag)
			{
				Log("Active voice client process: No running client processes detected.");
			}
		}
		catch (Exception ex)
		{
			Log("Error scanning processes: " + ex.Message);
		}
		try
		{
			Log("Scanning network adapters for DHCP, IP and gateway configuration...");
			NetworkInterface[] allNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
			int num = 0;
			int num2 = 0;
			List<string> list = new List<string>();
			NetworkInterface[] array = allNetworkInterfaces;
			foreach (NetworkInterface networkInterface in array)
			{
				if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
				{
					continue;
				}
				string name = networkInterface.Name;
				string description = networkInterface.Description;
				Log($"Adapter: '{name}' ({description})");
				Log($"  Status: {networkInterface.OperationalStatus}, Type: {networkInterface.NetworkInterfaceType}, Speed: {(double)networkInterface.Speed / 1000000.0:0} Mbps");
				if (networkInterface.OperationalStatus != OperationalStatus.Up)
				{
					Log("  [INFO] Adapter is offline/disconnected.");
					continue;
				}
				num++;
				IPInterfaceProperties iPProperties = networkInterface.GetIPProperties();
				bool flag2 = false;
				try
				{
					IPv4InterfaceProperties iPv4Properties = iPProperties.GetIPv4Properties();
					if (iPv4Properties != null)
					{
						flag2 = iPv4Properties.IsDhcpEnabled;
					}
				}
				catch
				{
				}
				Log("  DHCP Configuration: " + (flag2 ? "Enabled (Dynamic IP)" : "Disabled (Static IP)"));
				List<string> list2 = new List<string>();
				foreach (IPAddress dhcpServerAddress in iPProperties.DhcpServerAddresses)
				{
					list2.Add(dhcpServerAddress.ToString());
				}
				if (flag2 && list2.Count > 0)
				{
					Log("  DHCP Server: " + string.Join(", ", list2));
				}
				List<string> list3 = new List<string>();
				foreach (GatewayIPAddressInformation gatewayAddress in iPProperties.GatewayAddresses)
				{
					list3.Add(gatewayAddress.Address.ToString());
				}
				Log("  Default Gateway: " + ((list3.Count > 0) ? string.Join(", ", list3) : "NONE (No connection to router)"));
				List<string> list4 = new List<string>();
				bool flag3 = false;
				bool flag4 = false;
				foreach (UnicastIPAddressInformation unicastAddress in iPProperties.UnicastAddresses)
				{
					if (unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork)
					{
						string text2 = unicastAddress.Address.ToString();
						list4.Add(text2);
						if (text2.StartsWith("169.254"))
						{
							flag3 = true;
						}
						if (text2 == "0.0.0.0")
						{
							flag4 = true;
						}
					}
				}
				Log("  IP Addresses: " + ((list4.Count > 0) ? string.Join(", ", list4) : "None assigned"));
				List<string> list5 = new List<string>();
				foreach (IPAddress dnsAddress in iPProperties.DnsAddresses)
				{
					list5.Add(dnsAddress.ToString());
				}
				Log("  DNS Servers: " + ((list5.Count > 0) ? string.Join(", ", list5) : "NONE configured"));
				string text3 = description.ToLower();
				string text4 = name.ToLower();
				bool flag5 = text4.Contains("vpn") || text4.Contains("tap") || text4.Contains("tun") || text4.Contains("globalprotect") || text4.Contains("cisco") || text4.Contains("anyconnect") || text4.Contains("fortinet") || text4.Contains("forticlient") || text4.Contains("wireguard") || text4.Contains("tailscale") || text4.Contains("zerotier") || text4.Contains("checkpoint") || text4.Contains("sonicwall") || text4.Contains("pulse") || text3.Contains("vpn") || text3.Contains("tap") || text3.Contains("tun") || text3.Contains("virtual adapter") || text3.Contains("fortinet") || text3.Contains("globalprotect");
				if (flag5)
				{
					Log("  [WARNING] Active VPN/Virtual adapter detected. Active VPNs can introduce routing overhead, packet loss, and MTU issues.", isError: true);
				}
				if (flag3)
				{
					string text5 = $"Adapter '{name}' has a self-assigned APIPA IP address ({string.Join(", ", list4)}). DHCP server failed to assign an IP address. Check local router connection.";
					Log("  [CRITICAL] " + text5, isError: true);
					list.Add(text5);
				}
				else if (flag4 || list4.Count == 0)
				{
					string text6 = "Adapter '" + name + "' has no valid IP address assigned (0.0.0.0 or empty). Check physical cable or Wi-Fi router connection.";
					Log("  [CRITICAL] " + text6, isError: true);
					list.Add(text6);
				}
				else if (list3.Count == 0 && !flag5)
				{
					string text7 = "Adapter '" + name + "' is active but has no Default Gateway configured. It cannot route traffic to the internet or service portal.";
					Log("  [CRITICAL] " + text7, isError: true);
					list.Add(text7);
				}
				else if (list5.Count == 0 && !flag5)
				{
					string text8 = "Adapter '" + name + "' has no DNS servers configured. Domain resolution will fail.";
					Log("  [CRITICAL] " + text8, isError: true);
					list.Add(text8);
				}
				else if (!flag5)
				{
					num2++;
				}
			}
			if (num == 0)
			{
				HasLocalConnectivityIssue = true;
				LocalConnectivityIssueReason = "All network adapters are offline/disconnected. Check your network cables or Wi-Fi status.";
				Log("[CRITICAL] " + LocalConnectivityIssueReason, isError: true);
			}
			else if (num2 == 0)
			{
				HasLocalConnectivityIssue = true;
				LocalConnectivityIssueReason = ((list.Count > 0) ? string.Join(" | ", list) : "No network adapter has a valid IP address, Gateway, and DNS server configuration to connect to the router/internet.");
				Log("[CRITICAL] Local connectivity issue: " + LocalConnectivityIssueReason, isError: true);
			}
			else
			{
				HasLocalConnectivityIssue = false;
				LocalConnectivityIssueReason = "";
				Log($"Network scan completed. Found {num2} active, correctly configured physical network adapter(s).");
			}
		}
		catch (Exception ex2)
		{
			Log("Error scanning adapters: " + ex2.Message);
		}
		Log("=================================================================");
		Log("");
	}

	public LocalNetworkInfo GetLocalNetworkInfo()
	{
		LocalNetworkInfo localNetworkInfo = new LocalNetworkInfo();
		try
		{
			NetworkInterface[] allNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
			foreach (NetworkInterface networkInterface in allNetworkInterfaces)
			{
				if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback || networkInterface.OperationalStatus != OperationalStatus.Up)
				{
					continue;
				}
				IPInterfaceProperties iPProperties = networkInterface.GetIPProperties();
				List<string> list = new List<string>();
				foreach (GatewayIPAddressInformation gatewayAddress in iPProperties.GatewayAddresses)
				{
					list.Add(gatewayAddress.Address.ToString());
				}
				string text = "";
				string subnetMask = "";
				foreach (UnicastIPAddressInformation unicastAddress in iPProperties.UnicastAddresses)
				{
					if (unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork)
					{
						text = unicastAddress.Address.ToString();
						subnetMask = unicastAddress.IPv4Mask?.ToString() ?? "";
						break;
					}
				}
				if (string.IsNullOrEmpty(text))
				{
					continue;
				}
				bool flag = text.StartsWith("169.254");
				bool flag2 = text == "0.0.0.0";
				List<string> list2 = new List<string>();
				foreach (IPAddress dnsAddress in iPProperties.DnsAddresses)
				{
					list2.Add(dnsAddress.ToString());
				}
				string text2 = networkInterface.Description.ToLower();
				string text3 = networkInterface.Name.ToLower();
				bool flag3 = text3.Contains("vpn") || text3.Contains("tap") || text3.Contains("tun") || text3.Contains("globalprotect") || text3.Contains("cisco") || text3.Contains("anyconnect") || text3.Contains("fortinet") || text3.Contains("forticlient") || text3.Contains("wireguard") || text3.Contains("tailscale") || text3.Contains("zerotier") || text3.Contains("checkpoint") || text3.Contains("sonicwall") || text3.Contains("pulse") || text2.Contains("vpn") || text2.Contains("tap") || text2.Contains("tun") || text2.Contains("virtual adapter") || text2.Contains("fortinet") || text2.Contains("globalprotect");
				if (!flag && !flag2 && list.Count > 0)
				{
					localNetworkInfo.Status = (flag3 ? "Connected (VPN Active)" : "Connected");
					localNetworkInfo.IpAddress = text;
					localNetworkInfo.SubnetMask = subnetMask;
					localNetworkInfo.Gateway = string.Join(", ", list);
					localNetworkInfo.DnsServers = ((list2.Count > 0) ? string.Join(", ", list2) : "None");
					localNetworkInfo.Vlan = GetInterfaceVlanId(networkInterface.Id);
					if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
					{
						localNetworkInfo.WifiInfo = GetWifiDetailsAsync().GetAwaiter().GetResult();
					}
					localNetworkInfo.IsOk = true;
					return localNetworkInfo;
				}
				if (!localNetworkInfo.IsOk)
				{
					if (flag)
					{
						localNetworkInfo.Status = "No Router Connection (APIPA)";
					}
					else if (flag2)
					{
						localNetworkInfo.Status = "No IP Address (0.0.0.0)";
					}
					else
					{
						localNetworkInfo.Status = "Connected (No Gateway)";
					}
					localNetworkInfo.IpAddress = text;
					localNetworkInfo.SubnetMask = subnetMask;
					localNetworkInfo.Gateway = ((list.Count > 0) ? string.Join(", ", list) : "None");
					localNetworkInfo.DnsServers = ((list2.Count > 0) ? string.Join(", ", list2) : "None");
					localNetworkInfo.Vlan = GetInterfaceVlanId(networkInterface.Id);
					if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
					{
						localNetworkInfo.WifiInfo = GetWifiDetailsAsync().GetAwaiter().GetResult();
					}
				}
			}
		}
		catch
		{
		}
		localNetworkInfo.PublicIpAddress = PublicIpAddress;
		return localNetworkInfo;
	}

	public async Task<string> ResolvePublicIpAsync(CancellationToken token)
	{
		if (IsSimulationMode)
		{
			PublicIpAddress = "198.51.100.42";
			return PublicIpAddress;
		}
		try
		{
			string[] array = new string[2] { "stun.l.google.com", "stun1.l.google.com" };
			string[] array2 = array;
			foreach (string server in array2)
			{
				if (token.IsCancellationRequested)
				{
					break;
				}
				var (flag, text, _) = await QueryStunServerAsync(server, 3478, token);
				if (flag && !string.IsNullOrEmpty(text))
				{
					PublicIpAddress = text;
					return text;
				}
			}
		}
		catch
		{
		}
		return PublicIpAddress;
	}

	private async Task<string> GetWifiDetailsAsync()
	{
		return await Task.Run(() => 
		{
			try
			{
				using Process process = new Process();
				process.StartInfo.FileName = "netsh";
				process.StartInfo.Arguments = "wlan show interfaces";
				process.StartInfo.UseShellExecute = false;
				process.StartInfo.RedirectStandardOutput = true;
				process.StartInfo.CreateNoWindow = true;
				process.Start();
				
				string output = process.StandardOutput.ReadToEnd();
				bool flag = process.WaitForExit(3000);
				
				if (!flag)
				{
					try
					{
						process.Kill();
					}
					catch
					{
					}
				}
				string obj2 = (flag ? output : "");
			string text = "Unknown";
			string value = "";
			string value2 = "";
			string[] array = obj2.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < array.Length; i++)
			{
				string text2 = array[i].Trim();
				int num = text2.IndexOf(':');
				if (num >= 0)
				{
					string text3 = text2.Substring(0, num).Trim();
					string text4 = text2.Substring(num + 1).Trim();
					switch (text3)
					{
					case "SSID":
						text = text4;
						break;
					case "Signal":
						value = text4;
						break;
					case "Radio type":
						value2 = text4;
						break;
					}
				}
			}
			if (text != "Unknown")
			{
				return $"{text} ({value} / {value2})";
			}
		}
		catch
		{
		}
		return "Active (Details Unavailable)";
		});
	}


	private static string GetInterfaceVlanId(string interfaceGuid)
	{
		try
		{
			using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e972-e325-11ce-bfc1-08002be10318}");
			if (registryKey != null)
			{
				string b = interfaceGuid.Trim(new char[2] { '{', '}' });
				string[] subKeyNames = registryKey.GetSubKeyNames();
				foreach (string text in subKeyNames)
				{
					if (text.Length != 4)
					{
						continue;
					}
					using RegistryKey registryKey2 = registryKey.OpenSubKey(text);
					if (registryKey2 == null || !(registryKey2.GetValue("NetCfgInstanceId") is string text2) || !string.Equals(text2.Trim(new char[2] { '{', '}' }), b, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					string[] array = new string[5] { "VlanID", "*VlanID", "VLAN_ID", "VlanId", "VLANID" };
					foreach (string name in array)
					{
						object value = registryKey2.GetValue(name);
						if (value != null)
						{
							string text3 = value.ToString()?.Trim() ?? "";
							if (!string.IsNullOrEmpty(text3) && text3 != "0" && text3 != "1")
							{
								return text3;
							}
						}
					}
					break;
				}
			}
		}
		catch
		{
		}
		return "Untagged";
	}

	public bool CheckLocalConnectivityBeforeTest(string testName)
	{
		if (HasLocalConnectivityIssue)
		{
			Log("Skipping '" + testName + "' due to local network connectivity failure.");
			UpdateProgress(testName, "Failed", "Skipped - Local network failure");
			return true;
		}
		return false;
	}

	public string GetLocalIpAddress()
	{
		try
		{
			using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.IP);
			socket.Connect("8.8.8.8", 65530);
			return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
		}
		catch
		{
			return "127.0.0.1";
		}
	}

	public void Cancel()
	{
		_cts?.Cancel();
	}

	private void Log(string message, bool isError = false)
	{
		this.OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}", isError);
	}

	private void UpdateProgress(string testName, string status, string details)
	{
		this.OnProgress?.Invoke(testName, status, details);
	}

	public async Task<bool> RunDiagnosticsAsync()
	{
		_cts = new CancellationTokenSource();
		CancellationToken token = _cts.Token;
		Log("Starting Network Diagnostic Tool...");
		Log("=================================================================");
		Log($"Timestamp:         {DateTime.Now}");
		Log($"OS Version:        {Environment.OSVersion}");
		Log("Local IP Address:  " + GetLocalIpAddress());
		Log("Diagnostic Scope:  Checking network guidance for voice service readiness");
		Log("=================================================================");
		Log("All tests are running directly against the servers and ports specified in the network guide.");
		try
		{
			Log("");
			Log("=================================================================");
			Log("LOCAL NETWORK STATUS");
			Log("=================================================================");
			Log($"Last Speed Test:  Download: {LastDownloadMbps:F1} Mbps | Upload: {LastUploadMbps:F1} Mbps");
			Log("-----------------------------------------------------------------");
			NetworkInterface[] allNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
			foreach (NetworkInterface networkInterface in allNetworkInterfaces)
			{
				if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
				{
					continue;
				}
				string value = ((networkInterface.Speed > 0) ? $"{(double)networkInterface.Speed / 1000000.0:0} Mbps" : "Unknown");
				Log($"Adapter:  {networkInterface.Name} ({networkInterface.Description})");
				Log($"  Status: {networkInterface.OperationalStatus}, Type: {networkInterface.NetworkInterfaceType}, Speed: {value}");
				if (networkInterface.OperationalStatus != OperationalStatus.Up)
				{
					Log("  (Adapter is offline/disconnected - skipping detail)");
					continue;
				}
				IPInterfaceProperties iPProperties = networkInterface.GetIPProperties();
				List<string> list = new List<string>();
				foreach (UnicastIPAddressInformation unicastAddress in iPProperties.UnicastAddresses)
				{
					if (unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork)
					{
						list.Add($"{unicastAddress.Address}/{unicastAddress.IPv4Mask}");
					}
				}
				Log("  IPv4:   " + ((list.Count > 0) ? string.Join(", ", list) : "None"));
				List<string> list2 = new List<string>();
				foreach (GatewayIPAddressInformation gatewayAddress in iPProperties.GatewayAddresses)
				{
					list2.Add(gatewayAddress.Address.ToString());
				}
				Log("  Gateway:" + ((list2.Count > 0) ? (" " + string.Join(", ", list2)) : " None"));
				List<string> list3 = new List<string>();
				foreach (IPAddress dnsAddress in iPProperties.DnsAddresses)
				{
					list3.Add(dnsAddress.ToString());
				}
				Log("  DNS:    " + ((list3.Count > 0) ? string.Join(", ", list3) : "None"));
				try
				{
					IPInterfaceStatistics iPStatistics = networkInterface.GetIPStatistics();
					Log($"  Bytes Sent:     {iPStatistics.BytesSent:N0}");
					Log($"  Bytes Received: {iPStatistics.BytesReceived:N0}");
				}
				catch
				{
					Log("  Traffic stats: N/A");
				}
			}
			Log("=================================================================");
			Log("");
		}
		catch (Exception ex)
		{
			Log("[WARN] Could not log local network status: " + ex.Message);
		}
		try
		{
			await RunEnvironmentScanAsync(token);
			bool dnsPass = true;
			if (SelectedTests[0])
			{
				Log("");
				Log("=================================================================");
				Log("=== CHECK 1/10: DNS DOMAIN & RESOLUTION CHECK ===");
				Log("=================================================================");
				dnsPass = await RunDnsTestsAsync(token);
			}
			else
			{
				Log("Check 1: Skipped by user selection.");
				UpdateProgress("DNS Domain & Resolution Check", "Skipped", "Skipped by user");
			}
			if (token.IsCancellationRequested)
			{
				return false;
			}
			bool httpPass = true;
			if (SelectedTests[1])
			{
				Log("");
				Log("=================================================================");
				Log("=== CHECK 2/10: HTTP/HTTPS OUTBOUND PROBES ===");
				Log("=================================================================");
				httpPass = await RunHttpHttpsTestsAsync(token);
			}
			else
			{
				Log("Check 2: Skipped by user selection.");
				UpdateProgress("HTTP/HTTPS Outbound Probes", "Skipped", "Skipped by user");
			}
			if (token.IsCancellationRequested)
			{
				return false;
			}
			bool ntpPass = true;
			if (SelectedTests[2])
			{
				Log("");
				Log("=================================================================");
				Log("=== CHECK 3/10: NTP SUBSYSTEM TIME SYNC (UDP 123) ===");
				Log("=================================================================");
				ntpPass = await RunNtpTestAsync(token);
			}
			else
			{
				Log("Check 3: Skipped by user selection.");
				UpdateProgress("NTP Subsystem (UDP 123)", "Skipped", "Skipped by user");
			}
			if (token.IsCancellationRequested)
			{
				return false;
			}
			bool primaryStunPass = true;
			if (SelectedTests[3])
			{
				Log("");
				Log("=================================================================");
				Log("=== CHECK 4/10: STUN SERVER ACCESSIBILITY (UDP 3478) ===");
				Log("=================================================================");
				primaryStunPass = await RunPrimaryStunTestsAsync(token);
			}
			else
			{
				Log("Check 4: Skipped by user selection.");
				UpdateProgress("Primary STUN Servers", "Skipped", "Skipped by user");
			}
			if (token.IsCancellationRequested)
			{
				return false;
			}
			bool googleStunPass = true;
			if (SelectedTests[4])
			{
				Log("");
				Log("=================================================================");
				Log("=== CHECK 5/10: WEBRTC PUBLIC STUN DISCOVERY ===");
				Log("=================================================================");
				googleStunPass = await RunGoogleStunTestsAsync(token);
			}
			else
			{
				Log("Check 5: Skipped by user selection.");
				UpdateProgress("Google STUN Servers", "Skipped", "Skipped by user");
			}
			if (token.IsCancellationRequested)
			{
				return false;
			}
			bool natHopsPass = true;
			if (SelectedTests[5])
			{
				Log("");
				Log("=================================================================");
				Log("=== CHECK 6/10: NAT ROUTING & HOPS CHECK ===");
				Log("=================================================================");
				natHopsPass = await RunNatHopsTestAsync(token);
			}
			else
			{
				Log("Check 6: Skipped by user selection.");
				UpdateProgress("NAT Routing & Hops Check", "Skipped", "Skipped by user");
			}
			if (token.IsCancellationRequested)
			{
				return false;
			}
			bool natPortPass = true;
			if (SelectedTests[6])
			{
				Log("");
				Log("=================================================================");
				Log("=== CHECK 7/10: NAT PORT PRESERVATION CHECK ===");
				Log("=================================================================");
				natPortPass = await RunNatPortRandomnessTestAsync(token);
			}
			else
			{
				Log("Check 7: Skipped by user selection.");
				UpdateProgress("NAT Port Translation (Random Port)", "Skipped", "Skipped by user");
			}
			if (token.IsCancellationRequested)
			{
				return false;
			}
			bool sipAlgPass = true;
			if (SelectedTests[7])
			{
				Log("");
				Log("=================================================================");
				Log("=== CHECK 8/10: SIP ALG & HEADER INSPECTION CHECK ===");
				Log("=================================================================");
				sipAlgPass = await RunSipAlgTestAsync(token);
			}
			else
			{
				Log("Check 8: Skipped by user selection.");
				UpdateProgress("SIP ALG Detection", "Skipped", "Skipped by user");
			}
			if (token.IsCancellationRequested)
			{
				return false;
			}
			bool rtpQualityPass = true;
			if (SelectedTests[8])
			{
				Log("");
				Log("=================================================================");
				Log("=== CHECK 9/10: SIP MEDIA (RTP) QUALITY & MOS SIMULATION ===");
				Log("=================================================================");
				rtpQualityPass = await RunRtpQualityTestAsync(token);
			}
			else
			{
				Log("Check 9: Skipped by user selection.");
				UpdateProgress("RTP Jitter/Loss Check", "Skipped", "Skipped by user");
			}
			if (token.IsCancellationRequested)
			{
				return false;
			}
			bool flag = true;
			if (SelectedTests[9])
			{
				Log("");
				Log("=================================================================");
				Log("=== CHECK 10/10: PERSISTENT SIGNALLING & PBX REACHABILITY ===");
				Log("=================================================================");
				flag = await RunSignalRConnectivityTestAsync(token);
			}
			else
			{
				Log("Check 10: Skipped by user selection.");
				UpdateProgress("Inbound Signalling & Presence", "Skipped", "Skipped by user");
			}
			bool flag2 = dnsPass && httpPass && ntpPass && primaryStunPass && googleStunPass && natHopsPass && natPortPass && sipAlgPass && rtpQualityPass && flag;
			int num = 0;
			int num2 = 0;
			int[] array = new int[10] { 15, 15, 5, 0, 5, 5, 5, 15, 15, 10 };
			bool[] array2 = new bool[10] { dnsPass, httpPass, ntpPass, primaryStunPass, googleStunPass, natHopsPass, natPortPass, sipAlgPass, rtpQualityPass, flag };
			for (int j = 0; j < 10; j++)
			{
				if (SelectedTests[j])
				{
					num += array[j];
					if (array2[j])
					{
						num2 += array[j];
					}
				}
			}
			int num3 = ((num > 0) ? ((int)Math.Round((double)num2 / (double)num * 100.0)) : 100);
			Log(flag2 ? "All network checks PASSED. The firewall configuration is suitable for voice service." : "Some network checks FAILED or generated warnings. Please review the recommendations.");
			Log($"Weighted Diagnostics Score: {num3}/100");
			this.OnComplete?.Invoke(flag2, num3);
			return flag2;
		}
		catch (OperationCanceledException)
		{
			Log("Diagnostics cancelled by user.");
			return false;
		}
		catch (Exception ex3)
		{
			Log("Critical error during diagnostics: " + ex3.Message, isError: true);
			this.OnComplete?.Invoke(success: false, 0);
			return false;
		}
	}

	private async Task<bool> RunDnsTestsAsync(CancellationToken token)
	{
		if (CheckLocalConnectivityBeforeTest("DNS Domain & Resolution Check"))
		{
			return false;
		}
		UpdateProgress("DNS Domain & Resolution Check", "Running", "Resolving service domains...");
		Log("Test 1: Verifying DNS Resolution and Google DNS availability...");
		if (IsSimulationMode)
		{
			await Task.Delay(800, token);
			UpdateProgress("DNS Domain & Resolution Check", "Passed", "Pass - DNS resolving correctly");
			return true;
		}
		string[] domains = new string[2] { DomainToCheck, "stun.l.google.com" };
		int resolvedCount = 0;
		int criticalFailedCount = 0;
		List<string> failedDomains = new List<string>();
		string[] array = domains;
		foreach (string domain in array)
		{
			if (token.IsCancellationRequested)
			{
				return false;
			}
			try
			{
				Log("Resolving domain '" + domain + "' using default DNS...");
				IPAddress[] array2 = await Dns.GetHostAddressesAsync(domain, token);
				if (array2.Length != 0)
				{
					NetworkEngine networkEngine = this;
					object[] values = array2;
					networkEngine.Log("Success: Resolved '" + domain + "' to: " + string.Join(", ", values));
					resolvedCount++;
					continue;
				}
				Log("Error: Resolved '" + domain + "' to 0 IP addresses.", isError: true);
				failedDomains.Add(domain);
				if (domain == DomainToCheck || domain.Contains("presence") || domain.Contains("signalling") || domain.Contains("rooms"))
				{
					criticalFailedCount++;
				}
			}
			catch (Exception ex)
			{
				Log("Error: Failed to resolve '" + domain + "' via default DNS: " + ex.Message, isError: true);
				failedDomains.Add(domain);
				if (domain == DomainToCheck || domain.Contains("presence") || domain.Contains("signalling") || domain.Contains("rooms"))
				{
					criticalFailedCount++;
				}
			}
		}
		bool dns1Ok = await QueryDnsServerAsync("8.8.8.8", DomainToCheck, token);
		bool flag = await QueryDnsServerAsync("8.8.4.4", DomainToCheck, token);
		if (criticalFailedCount == 0)
		{
			if (failedDomains.Count > 0)
			{
				Log("Test 1: PASSED WITH WARNINGS. Critical domains resolved, but backup/STUN domains failed: " + string.Join(", ", failedDomains));
				UpdateProgress("DNS Domain & Resolution Check", "Passed", $"Pass - Resolved {resolvedCount}/{domains.Length} domains");
			}
			else if (dns1Ok && flag)
			{
				Log("Test 1: PASSED. All domains resolve correctly, and Google DNS servers are reachable.");
				UpdateProgress("DNS Domain & Resolution Check", "Passed", "Pass - DNS & Google DNS Active");
			}
			else
			{
				Log("Test 1: PASSED WITH WARNINGS. Domains resolve correctly via default local DNS, but direct outbound queries to Google DNS (8.8.8.8/8.8.4.4) failed (possible port 53 UDP egress block).");
				UpdateProgress("DNS Domain & Resolution Check", "Passed", "Pass - DNS Active (Google DNS blocked)");
			}
			return true;
		}
		Log($"Test 1: FAILED. Critical service domains failed to resolve: {string.Join(", ", failedDomains)}. Google DNS 8.8.8.8: {(dns1Ok ? "OK" : "Failed")}, Google DNS 8.8.4.4: {(flag ? "OK" : "Failed")}", isError: true);
		UpdateProgress("DNS Domain & Resolution Check", "Failed", $"Fail - {criticalFailedCount} critical domains failed");
		return false;
	}

	private async Task<bool> QueryDnsServerAsync(string dnsServer, string hostname, CancellationToken token)
	{
		_ = 2;
		try
		{
			Log($"Querying Google DNS server {dnsServer} for domain '{hostname}'...");
			IPAddress address = IPAddress.Parse(dnsServer);
			using UdpClient client = new UdpClient(0);
			client.Client.SendTimeout = 2000;
			client.Client.ReceiveTimeout = 2000;
			List<byte> list = new List<byte>();
			byte[] txId = new byte[2];
			Random.Shared.NextBytes(txId);
			list.AddRange(txId);
			list.AddRange(new byte[2] { 1, 0 });
			list.AddRange(new byte[2] { 0, 1 });
			list.AddRange(new byte[2]);
			list.AddRange(new byte[2]);
			list.AddRange(new byte[2]);
			string[] array = hostname.Split('.');
			foreach (string text in array)
			{
				list.Add((byte)text.Length);
				list.AddRange(Encoding.ASCII.GetBytes(text));
			}
			list.Add(0);
			list.AddRange(new byte[2] { 0, 1 });
			list.AddRange(new byte[2] { 0, 1 });
			byte[] array2 = list.ToArray();
			IPEndPoint endPoint = new IPEndPoint(address, 53);
			Pcap.RecordPacket(array2, GetLocalIpAddress(), ((IPEndPoint)client.Client.LocalEndPoint).Port, dnsServer, 53, isUdp: true);
			await client.SendAsync(array2, array2.Length, endPoint);
			Task<UdpReceiveResult> receiveTask = client.ReceiveAsync(token).AsTask();
			Task task = Task.Delay(2000, token);
			if (await Task.WhenAny(receiveTask, task) == receiveTask)
			{
				UdpReceiveResult udpReceiveResult = await receiveTask;
				Pcap.RecordPacket(udpReceiveResult.Buffer, dnsServer, 53, GetLocalIpAddress(), ((IPEndPoint)client.Client.LocalEndPoint).Port, isUdp: true);
				if (udpReceiveResult.Buffer.Length > 12 && udpReceiveResult.Buffer[0] == txId[0] && udpReceiveResult.Buffer[1] == txId[1])
				{
					Log("Google DNS " + dnsServer + " responded successfully to domain check.");
					return true;
				}
			}
			Log("Google DNS " + dnsServer + " did not return a valid response.", isError: true);
			return false;
		}
		catch (Exception ex)
		{
			Log("Google DNS " + dnsServer + " query failed: " + ex.Message, isError: true);
			return false;
		}
	}

	private async Task<bool> RunHttpHttpsTestsAsync(CancellationToken token)
	{
		if (CheckLocalConnectivityBeforeTest("HTTP/HTTPS Outbound Probes"))
		{
			return false;
		}
		UpdateProgress("HTTP/HTTPS Outbound Probes", "Running", "Testing web connection and TLS handshake...");
		Log("Test 2: Probing HTTP/HTTPS outbound connectivity & SSL/TLS handshake...");
		if (IsSimulationMode)
		{
			await Task.Delay(800, token);
			UpdateProgress("HTTP/HTTPS Outbound Probes", "Passed", "Pass - HTTP/HTTPS Verified (TLS Succeeded)");
			return true;
		}
		string text = "http://" + DomainToCheck;
		string httpsUrl = "https://" + DomainToCheck;
		Log("Testing HTTP web request to " + text + "...");
		(bool ok, string msg) httpResult = await TestHttpEndpointAsync(text, token);
		Log($"HTTP to {DomainToCheck}: {(httpResult.ok ? "SUCCESS" : "FAILED")} ({httpResult.msg})");
		Log("Testing HTTPS web request (including TLS handshake) to " + httpsUrl + "...");
		(bool, string) tuple = await TestHttpEndpointAsync(httpsUrl, token);
		Log($"HTTPS to {DomainToCheck}: {(tuple.Item1 ? "SUCCESS" : "FAILED")} ({tuple.Item2})");
		if (httpResult.ok && tuple.Item1)
		{
			Log("Test 2: PASSED. Outbound HTTP/HTTPS web requests and SSL/TLS handshakes are verified.");
			UpdateProgress("HTTP/HTTPS Outbound Probes", "Passed", "Pass - HTTP/HTTPS Verified (TLS Succeeded)");
			return true;
		}
		string text2 = "";
		if (!httpResult.ok)
		{
			text2 = text2 + "HTTP (Error: " + httpResult.msg + ") ";
		}
		if (!tuple.Item1)
		{
			text2 = text2 + "HTTPS (Error: " + tuple.Item2 + ")";
		}
		Log("Test 2: FAILED. Web connection failures: " + text2.Trim(), isError: true);
		UpdateProgress("HTTP/HTTPS Outbound Probes", "Failed", "Fail - SSL/TLS Handshake or Connection Blocked");
		return false;
	}

	private async Task<(bool ok, string msg)> TestHttpEndpointAsync(string url, CancellationToken token)
	{
		try
		{
			HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
			HttpResponseMessage httpResponseMessage = await _sharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
			return (ok: true, msg: $"Success (Status: {(int)httpResponseMessage.StatusCode} {httpResponseMessage.ReasonPhrase})");
		}
		catch (Exception innerException)
		{
			while (innerException.InnerException != null)
			{
				innerException = innerException.InnerException;
			}
			return (ok: false, msg: innerException.Message);
		}
	}

	private async Task<bool> TestTcpPortAsync(string host, int port, CancellationToken token)
	{
		_ = 1;
		try
		{
			using TcpClient client = new TcpClient();
			Task connectTask = client.ConnectAsync(host, port, token).AsTask();
			Task task = Task.Delay(3000, token);
			if (await Task.WhenAny(connectTask, task) == connectTask)
			{
				await connectTask;
				return client.Connected;
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	private async Task<bool> RunNtpTestAsync(CancellationToken token)
	{
		if (CheckLocalConnectivityBeforeTest("NTP Subsystem (UDP 123)"))
		{
			return false;
		}
		UpdateProgress("NTP Subsystem (UDP 123)", "Running", "Querying NTP time server...");
		Log("Test 3: Checking UDP port 123 (NTP) outbound transmission...");
		if (IsSimulationMode)
		{
			await Task.Delay(800, token);
			UpdateProgress("NTP Subsystem (UDP 123)", "Passed", "Pass - UDP 123 Outbound Open");
			return true;
		}
		string[] array = new string[3] { "pool.ntp.org", "time.google.com", "time.windows.com" };
		bool ntpOk = false;
		DateTime? detectedTime = null;
		string[] array2 = array;
		foreach (string ntpHost in array2)
		{
			Log("Sending standard NTP synchronization request to " + ntpHost + "...");
			(bool, DateTime?) tuple = await TestNtpAsync(ntpHost, token);
			if (tuple.Item1)
			{
				ntpOk = true;
				detectedTime = tuple.Item2;
				Log("Success: Received NTP response from " + ntpHost + ".");
				break;
			}
			Log("NTP request to " + ntpHost + " timed out.");
		}
		if (ntpOk)
		{
			Log("Test 3: PASSED. Outbound UDP port 123 (NTP) is open and receiving replies.");
			if (detectedTime.HasValue)
			{
				try
				{
					TimeZoneInfo destinationTimeZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
					DateTime dateTime = TimeZoneInfo.ConvertTimeFromUtc(detectedTime.Value, destinationTimeZone);
					Log("NTP Server reported time: " + dateTime.ToString("yyyy-MM-dd HH:mm:ss") + " (UK Time)");
					UpdateProgress("NTP Subsystem (UDP 123)", "Passed", "Pass - Time: " + dateTime.ToString("HH:mm:ss"));
				}
				catch
				{
					Log("NTP Server reported time: " + detectedTime.Value.ToString("yyyy-MM-dd HH:mm:ss UTC"));
					UpdateProgress("NTP Subsystem (UDP 123)", "Passed", "Pass - Time: " + detectedTime.Value.ToString("HH:mm:ss UTC"));
				}
			}
			else
			{
				UpdateProgress("NTP Subsystem (UDP 123)", "Passed", "Pass - UDP 123 Outbound Open");
			}
			return true;
		}
		Log("Test 3: FAILED. UDP port 123 queries to all NTP servers timed out. Ensure NTP outbound traffic is permitted.", isError: true);
		UpdateProgress("NTP Subsystem (UDP 123)", "Failed", "Fail - UDP 123 Blocked / Timeout");
		return false;
	}

	private async Task<(bool success, DateTime? time)> TestNtpAsync(string host, CancellationToken token)
	{
		_ = 3;
		try
		{
			byte[] ntpData = new byte[48];
			ntpData[0] = 27;
			IPAddress[] array = await Dns.GetHostAddressesAsync(host, token);
			if (array.Length == 0)
			{
				return (success: false, time: null);
			}
			IPEndPoint endPoint = new IPEndPoint(array[0], 123);
			using UdpClient client = new UdpClient(0);
			client.Client.SendTimeout = 2000;
			client.Client.ReceiveTimeout = 2000;
			string localIp = GetLocalIpAddress();
			int localPort = ((IPEndPoint)client.Client.LocalEndPoint).Port;
			Pcap.RecordPacket(ntpData, localIp, localPort, endPoint.Address.ToString(), 123, isUdp: true);
			await client.SendAsync(ntpData, ntpData.Length, endPoint);
			Task<UdpReceiveResult> receiveTask = client.ReceiveAsync(token).AsTask();
			Task task = Task.Delay(2000, token);
			if (await Task.WhenAny(receiveTask, task) == receiveTask)
			{
				UdpReceiveResult udpReceiveResult = await receiveTask;
				Pcap.RecordPacket(udpReceiveResult.Buffer, endPoint.Address.ToString(), 123, localIp, localPort, isUdp: true);
				if (udpReceiveResult.Buffer.Length >= 48 && (udpReceiveResult.Buffer[0] & 7) == 4)
				{
					ulong num = ((ulong)udpReceiveResult.Buffer[40] << 24) | ((ulong)udpReceiveResult.Buffer[41] << 16) | ((ulong)udpReceiveResult.Buffer[42] << 8) | udpReceiveResult.Buffer[43];
					ulong num2 = ((ulong)udpReceiveResult.Buffer[44] << 24) | ((ulong)udpReceiveResult.Buffer[45] << 16) | ((ulong)udpReceiveResult.Buffer[46] << 8) | udpReceiveResult.Buffer[47];
					ulong num3 = num * 1000 + num2 * 1000 / 4294967296L;
					DateTime value = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds((long)num3);
					return (success: true, time: value);
				}
			}
			return (success: false, time: null);
		}
		catch
		{
			return (success: false, time: null);
		}
	}

	private async Task<bool> RunPrimaryStunTestsAsync(CancellationToken token)
	{
		if (CheckLocalConnectivityBeforeTest("Primary STUN Servers"))
		{
			return false;
		}
		UpdateProgress("Primary STUN Servers", "Running", "Querying PBX STUN endpoint...");
		Log("Test 4: Querying PBX STUN endpoint...");
		if (IsSimulationMode)
		{
			await Task.Delay(1000, token);
			UpdateProgress("Primary STUN Servers", "Passed", "Pass - PBX STUN endpoint OK");
			return true;
		}
		int successCount = 0;
		List<string> failedServers = new List<string>();
		(string host, string ip)[] primaryStunServers = PrimaryStunServers;
		for (int i = 0; i < primaryStunServers.Length; i++)
		{
			var (host, value) = primaryStunServers[i];
			if (token.IsCancellationRequested)
			{
				return false;
			}
			Log($"Querying PBX STUN endpoint: {host}...");
			var (flag, text, value2) = await QueryStunServerAsync(host, 3478, token);
			if (flag)
			{
				Log($"Success: {host} returned Public IP: {text}, Mapped Port: {value2}");
				if (!string.IsNullOrEmpty(text))
				{
					PublicIpAddress = text;
				}
				successCount++;
			}
			else
			{
				Log("PBX STUN endpoint " + host + " failed to respond.");
				failedServers.Add(host);
			}
		}
		if (successCount == PrimaryStunServers.Length)
		{
			Log("Test 4: PASSED. PBX STUN endpoint responded successfully.");
			UpdateProgress("Primary STUN Servers", "Passed", "Pass - PBX STUN endpoint online");
		}
		else if (successCount > 0)
		{
			Log($"Test 4: Successful: {successCount}/{PrimaryStunServers.Length}. Failed: {string.Join(", ", failedServers)}");
			UpdateProgress("Primary STUN Servers", "Passed", $"Pass - {successCount}/{PrimaryStunServers.Length} online");
		}
		else
		{
			Log("Test 4: INFO. PBX STUN endpoint did not respond. Egress is verified via Google Backup STUN.");
			UpdateProgress("Primary STUN Servers", "Passed", "Informational - PBX STUN unreachable");
		}
		return true;
	}

	private async Task<bool> RunGoogleStunTestsAsync(CancellationToken token)
	{
		if (CheckLocalConnectivityBeforeTest("Google STUN Servers"))
		{
			return false;
		}
		UpdateProgress("Google STUN Servers", "Running", "Querying Google backup STUN...");
		Log("Test 5: Querying Google STUN Servers...");
		if (IsSimulationMode)
		{
			await Task.Delay(1000, token);
			UpdateProgress("Google STUN Servers", "Passed", "Pass - All 5 Google STUN servers OK");
			return true;
		}
		int successCount = 0;
		List<string> failedServers = new List<string>();
		var googleStunServers = GetGoogleStunServers();
		for (int i = 0; i < googleStunServers.Length; i++)
		{
			var (host, value) = googleStunServers[i];
			if (token.IsCancellationRequested)
			{
				return false;
			}
			Log($"Querying Google STUN Server: {host} ({value})...");
			var (flag, text, value2) = await QueryStunServerAsync(host, 3478, token);
			if (flag)
			{
				Log($"Success: {host} returned Public IP: {text}, Mapped Port: {value2}");
				if (!string.IsNullOrEmpty(text))
				{
					PublicIpAddress = text;
				}
				successCount++;
			}
			else
			{
				Log("Google STUN server " + host + " failed to respond.");
				failedServers.Add(host);
			}
		}
		if (successCount > 0)
		{
			Log($"Test 5: PASSED. Google STUN check passed. {successCount}/{googleStunServers.Length} servers responded.");
			UpdateProgress("Google STUN Servers", "Passed", $"Pass - {successCount}/{googleStunServers.Length} online");
			return true;
		}
		Log("Test 5: FAILED. All Google STUN servers failed to respond. Outbound UDP port 3478 is likely blocked.", isError: true);
		UpdateProgress("Google STUN Servers", "Failed", "Fail - Google STUN query blocked");
		return false;
	}

	private async Task<bool> RunNatHopsTestAsync(CancellationToken token)
	{
		if (CheckLocalConnectivityBeforeTest("NAT Routing & Hops Check"))
		{
			return false;
		}
		UpdateProgress("NAT Routing & Hops Check", "Running", "Tracing route to default gateway...");
		Log("Test 6: Checking local addressing and counting NAT router hops...");
		string localIpAddress = GetLocalIpAddress();
		Log("Local IP Address: " + localIpAddress);
		if (IsPrivateIp(IPAddress.Parse(localIpAddress)))
		{
			Log("Local address is in a private subnet range (Pass).");
		}
		else
		{
			Log("Warning - Public IP detected directly on local interface. The client is bypass-NAT connected.");
		}
		if (IsSimulationMode)
		{
			await Task.Delay(1000, token);
			UpdateProgress("NAT Routing & Hops Check", "Passed", "Pass - Single NAT (1 private hop)");
			return true;
		}
		Log("Performing TTL-limited ICMP probes to detect private gateways/routers (Double NAT)...");
		int num = await CountPrivateHopsAsync("8.8.8.8", 5);
		Log($"Intermediate private hops detected: {num}");
		if (num > 1)
		{
			Log($"Error: Double NAT detected ({num} private network devices). This violates the single-NAT recommendation for reliable voice service.", isError: true);
			UpdateProgress("NAT Routing & Hops Check", "Failed", $"Fail - Double NAT ({num} hops)");
			return false;
		}
		if (num == 0)
		{
			Log("Private hop traceroute did not return hops. This is common if ICMP is blocked by intermediate firewalls. Local private address confirmed NAT is active.");
			UpdateProgress("NAT Routing & Hops Check", "Passed", "Pass - NAT Active (ICMP Blocked)");
			return true;
		}
		Log("Pass - Single NAT configuration detected (1 hop).");
		UpdateProgress("NAT Routing & Hops Check", "Passed", "Pass - Single NAT (1 private hop)");
		return true;
	}

	private static bool IsPrivateIp(IPAddress ip)
	{
		byte[] addressBytes = ip.GetAddressBytes();
		if (addressBytes.Length != 4)
		{
			return false;
		}
		if (addressBytes[0] == 10)
		{
			return true;
		}
		if (addressBytes[0] == 172 && addressBytes[1] >= 16 && addressBytes[1] <= 31)
		{
			return true;
		}
		if (addressBytes[0] == 192 && addressBytes[1] == 168)
		{
			return true;
		}
		if (addressBytes[0] == 100 && addressBytes[1] >= 64 && addressBytes[1] <= 127)
		{
			return true;
		}
		return false;
	}

	private async Task<int> CountPrivateHopsAsync(string targetIp, int maxHops)
	{
		int privateHops = 0;
		using Ping ping = new Ping();
		PingOptions options = new PingOptions(1, dontFragment: true);
		for (int ttl = 1; ttl <= maxHops; ttl++)
		{
			options.Ttl = ttl;
			try
			{
				PingReply pingReply = await ping.SendPingAsync(targetIp, 1000, new byte[32], options);
				if (pingReply.Status != IPStatus.TtlExpired && pingReply.Status != IPStatus.Success)
				{
					continue;
				}
				IPAddress address = pingReply.Address;
				if (address != null)
				{
					if (!IsPrivateIp(address))
					{
						break;
					}
					privateHops++;
				}
			}
			catch
			{
			}
		}
		return privateHops;
	}

	private async Task<bool> RunNatPortRandomnessTestAsync(CancellationToken token)
	{
		if (CheckLocalConnectivityBeforeTest("NAT Port Translation (Random Port)"))
		{
			return false;
		}
		UpdateProgress("NAT Port Translation (Random Port)", "Running", "Checking port preservation...");
		Log("Test 7: Evaluating NAT port translation...");
		Log("Guideline: 'The public interface NAT port should be random and not be the same as the local NAT port.'");
		int localPort = LocalSipPort;
		int mappedPort = 0;
		bool success = false;
		if (IsSimulationMode)
		{
			await Task.Delay(800, token);
			mappedPort = localPort;
			Log($"[Simulation] Local Port: {localPort}, STUN Mapped Port: {mappedPort}");
			Log("Pass: The public interface NAT port is preserved (Consistent NAT).");
			UpdateProgress("NAT Port Translation (Random Port)", "Passed", $"Pass - Port preserved ({mappedPort})");
			return true;
		}
		UdpClient udpClient = null;
		try
		{
			udpClient = new UdpClient(localPort);
			Log($"Bound to local UDP port {localPort}.");
		}
		catch (SocketException)
		{
			Log($"Local UDP port {localPort} is in use. Auto-selecting an ephemeral port for testing...");
			try
			{
				udpClient = new UdpClient(0);
				localPort = ((IPEndPoint)udpClient.Client.LocalEndPoint).Port;
				Log($"Bound successfully to local UDP port {localPort}.");
			}
			catch (Exception ex2)
			{
				Log("Error: Failed to bind to local UDP port: " + ex2.Message, isError: true);
				UpdateProgress("NAT Port Translation (Random Port)", "Failed", "Fail - Local bind failed");
				return false;
			}
		}
		finally
		{
			udpClient?.Close();
		}
		string[] array = new string[2] { "stun.l.google.com", "stun1.l.google.com" };
		string[] array2 = array;
		foreach (string server in array2)
		{
			var (flag, _, num) = await QueryStunServerAsync(server, 3478, token, localPort);
			if (flag)
			{
				mappedPort = num;
				success = true;
				break;
			}
		}
		if (!success)
		{
			Log("Error: Could not query STUN server to check port mapping.", isError: true);
			UpdateProgress("NAT Port Translation (Random Port)", "Failed", "Fail - STUN query failed");
			return false;
		}
		Log($"Local Source Port: {localPort}");
		Log($"Public Interface Port: {mappedPort}");
		if (mappedPort == localPort)
		{
			Log("Pass: The public interface NAT port is preserved (Consistent NAT).");
			UpdateProgress("NAT Port Translation (Random Port)", "Passed", $"Pass - Port preserved ({mappedPort})");
			return true;
		}
		Log("Error: The public interface NAT port is randomized and differs from the local UDP port.", isError: true);
		Log("Symmetric NAT is active. VoIP requires consistent port mapping (port preservation).", isError: true);
		UpdateProgress("NAT Port Translation (Random Port)", "Failed", $"Fail - Port randomized ({mappedPort})");
		return false;
	}

	private async Task<bool> RunSipAlgTestAsync(CancellationToken token)
	{
		if (CheckLocalConnectivityBeforeTest("SIP ALG Detection"))
		{
			return false;
		}
		UpdateProgress("SIP ALG Detection", "Running", "Checking for SIP inspection engines...");
		Log("Test 8: Probing for SIP ALG / packet modification on UDP 5060...");
		if (IsSimulationMode)
		{
			await Task.Delay(1000, token);
			Log("[Simulation] Verification response matched. SIP ALG: Disabled.");
			UpdateProgress("SIP ALG Detection", "Passed", "Pass - SIP ALG Disabled");
			return true;
		}
		string[] array = new string[2] { DomainToCheck, "sip.linphone.org" };
		bool responseReceived = false;
		bool sipAlgDetected = false;
		string[] array2 = array;
		foreach (string server in array2)
		{
			if (token.IsCancellationRequested)
			{
				return false;
			}
			Log("Testing SIP ALG against public server: " + server + "...");
			try
			{
				IPAddress[] array3 = await Dns.GetHostAddressesAsync(server, token);
				if (array3.Length == 0)
				{
					continue;
				}
				IPEndPoint endpoint = new IPEndPoint(array3[0], 5060);
				using UdpClient udpClient = new UdpClient(0);
				udpClient.Client.SendTimeout = 3000;
				udpClient.Client.ReceiveTimeout = 3000;
				int localPort = ((IPEndPoint)udpClient.Client.LocalEndPoint).Port;
				string value = "z9hG4bK" + Guid.NewGuid().ToString("N").Substring(0, 10);
				string value2 = Guid.NewGuid().ToString("N").Substring(0, 10);
				string value3 = Guid.NewGuid().ToString("N").Substring(0, 16) + "@chriskendall.media";
				string fakeLocalIp = "192.168.1.100";
				string s = $"OPTIONS sip:{server} SIP/2.0\r\nVia: SIP/2.0/UDP {fakeLocalIp}:{localPort};rport;branch={value}\r\nMax-Forwards: 70\r\nTo: <sip:ping@{server}>\r\nFrom: <sip:ping@{fakeLocalIp}:{localPort}>;tag={value2}\r\nCall-ID: {value3}\r\nCSeq: 1 OPTIONS\r\nContact: <sip:ping@{fakeLocalIp}:{localPort}>\r\nUser-Agent: Merlin SIP Network Diagnostics\r\nContent-Length: 0\r\n\r\n";
				byte[] bytes = Encoding.UTF8.GetBytes(s);
				string actualLocalIp = GetLocalIpAddress();
				Pcap.RecordPacket(bytes, actualLocalIp, localPort, endpoint.Address.ToString(), endpoint.Port, isUdp: true);
				Log("Sending OPTIONS payload with dummy Via IP " + fakeLocalIp + "...");
				await udpClient.SendAsync(bytes, bytes.Length, endpoint);
				Task<UdpReceiveResult> receiveTask = udpClient.ReceiveAsync(token).AsTask();
				Task task = Task.Delay(3000, token);
				if (await Task.WhenAny(receiveTask, task) == receiveTask)
				{
					UdpReceiveResult udpReceiveResult = await receiveTask;
					responseReceived = true;
					Pcap.RecordPacket(udpReceiveResult.Buffer, endpoint.Address.ToString(), endpoint.Port, actualLocalIp, localPort, isUdp: true);
					string text = Encoding.UTF8.GetString(udpReceiveResult.Buffer);
					Log("Received response from " + server + ". Analysing Via headers...");
					bool flag = false;
					string[] array4 = text.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
					foreach (string text2 in array4)
					{
						if (text2.StartsWith("Via:", StringComparison.OrdinalIgnoreCase))
						{
							flag = true;
							Log("Returned " + text2);
							if (!text2.Contains(fakeLocalIp))
							{
								sipAlgDetected = true;
							}
						}
					}
					if (!flag)
					{
						Log("No Via header returned in response.");
					}
					if (responseReceived)
					{
						break;
					}
				}
				else
				{
					Log("Request to " + server + " timed out.");
				}
			}
			catch (Exception ex)
			{
				Log("Failed for " + server + ": " + ex.Message);
			}
		}
		if (!responseReceived)
		{
			Log("Test 8: FAILED. No response received from any public SIP server. Outbound UDP 5060 may be blocked.", isError: true);
			UpdateProgress("SIP ALG Detection", "Failed", "Fail - No SIP Response (UDP 5060 Blocked)");
			return false;
		}
		if (sipAlgDetected)
		{
			Log("Violation: The returned Via header does NOT match the dummy internal IP sent.", isError: true);
			Log("This strictly confirms that the router's SIP ALG has mangled the SIP packet payload.", isError: true);
			UpdateProgress("SIP ALG Detection", "Failed", "Fail - SIP ALG Enabled (Header Mangled)");
			return false;
		}
		Log("Pass: Response matched dummy internal IP. SIP ALG is Disabled.");
		UpdateProgress("SIP ALG Detection", "Passed", "Pass - SIP ALG Disabled");
		return true;
	}

	private async Task<(int sent, int received, double loss, double jitter, double avgRtt, bool pass)> RunSingleRtpPathCheckAsync(string pathName, string targetHost, int targetPort, string payloadType, CancellationToken token)
	{
		Log($"Starting path check: {pathName} ({targetHost}:{targetPort}) via {payloadType}...");
		int numPackets = 100;
		int packetDelayMs = 20;
		int packetsReceived = 0;
		List<double> rtts = new List<double>();
		UdpClient client = null;
		try
		{
			client = new UdpClient(0)
			{
				Client = 
				{
					SendTimeout = 1000,
					ReceiveTimeout = 1000
				}
			};
			IPAddress[] array = await Dns.GetHostAddressesAsync(targetHost, token);
			if (array.Length == 0)
			{
				Log("  [" + pathName + "] DNS resolution failed.", isError: true);
				return (sent: numPackets, received: 0, loss: 100.0, jitter: 0.0, avgRtt: 0.0, pass: false);
			}
			IPEndPoint endpoint = new IPEndPoint(array[0], targetPort);
			string localIp = GetLocalIpAddress();
			int localPort = ((IPEndPoint)client.Client.LocalEndPoint).Port;
			for (int i = 0; i < numPackets; i++)
			{
				if (token.IsCancellationRequested)
				{
					return (sent: numPackets, received: 0, loss: 100.0, jitter: 0.0, avgRtt: 0.0, pass: false);
				}
				byte[] transactionId = new byte[12];
				string callId = string.Empty;
				byte[] array2;
				if (payloadType == "SIP")
				{
					string value = "z9hG4bK" + Guid.NewGuid().ToString("N").Substring(0, 10);
					string value2 = Guid.NewGuid().ToString("N").Substring(0, 10);
					callId = Guid.NewGuid().ToString("N").Substring(0, 16) + "@chriskendall.media";
					string s = $"OPTIONS sip:{targetHost} SIP/2.0\r\nVia: SIP/2.0/UDP {localIp}:{localPort};rport;branch={value}\r\nMax-Forwards: 70\r\nTo: <sip:checker@{targetHost}>\r\nFrom: <sip:checker@{localIp}:{localPort}>;tag={value2}\r\nCall-ID: {callId}\r\nCSeq: {i + 1} OPTIONS\r\nContact: <sip:checker@{localIp}:{localPort}>\r\nUser-Agent: Merlin SIP Network Diagnostics\r\nContent-Length: 0\r\n\r\n";
					array2 = Encoding.UTF8.GetBytes(s);
				}
				else
				{
					new Random().NextBytes(transactionId);
					array2 = BuildStunRequest(transactionId, changeIp: false, changePort: false);
				}
				Stopwatch watch = Stopwatch.StartNew();
				Pcap.RecordPacket(array2, localIp, localPort, endpoint.Address.ToString(), endpoint.Port, isUdp: true);
				await client.SendAsync(array2, array2.Length, endpoint);
				try
				{
					using CancellationTokenSource perPacketCts = CancellationTokenSource.CreateLinkedTokenSource(token);
					perPacketCts.CancelAfter(500);
					UdpReceiveResult udpReceiveResult = await client.ReceiveAsync(perPacketCts.Token).AsTask();
					watch.Stop();
					Pcap.RecordPacket(udpReceiveResult.Buffer, endpoint.Address.ToString(), endpoint.Port, localIp, localPort, isUdp: true);
					if ((!(payloadType == "SIP")) ? ValidateStunResponse(udpReceiveResult.Buffer, transactionId) : Encoding.UTF8.GetString(udpReceiveResult.Buffer).Contains(callId, StringComparison.OrdinalIgnoreCase))
					{
						packetsReceived++;
						rtts.Add(watch.Elapsed.TotalMilliseconds);
					}
				}
				catch (OperationCanceledException)
				{
				}
				catch
				{
				}
				await Task.Delay(packetDelayMs, token);
			}
			double num = (double)(numPackets - packetsReceived) / (double)numPackets * 100.0;
			double num2 = 0.0;
			double num3 = 0.0;
			if (packetsReceived > 0)
			{
				double num4 = 0.0;
				foreach (double item2 in rtts)
				{
					num4 += item2;
				}
				num2 = num4 / (double)rtts.Count;
				if (rtts.Count > 1)
				{
					double num5 = 0.0;
					for (int j = 1; j < rtts.Count; j++)
					{
						num5 += Math.Abs(rtts[j] - rtts[j - 1]);
					}
					num3 = num5 / (double)(rtts.Count - 1);
				}
			}
			Log($"  [{pathName}] Sent: {numPackets}, Recv: {packetsReceived}, Loss: {num:0.0}%, Jitter: {num3:0.1}ms, RTT: {num2:0.1}ms");
			bool item = num <= 5.0 && num3 <= 30.0 && packetsReceived > 0;
			return (sent: numPackets, received: packetsReceived, loss: num, jitter: num3, avgRtt: num2, pass: item);
		}
		catch (Exception ex2)
		{
			Log("  [" + pathName + "] Failed with error: " + ex2.Message, isError: true);
			return (sent: numPackets, received: 0, loss: 100.0, jitter: 0.0, avgRtt: 0.0, pass: false);
		}
		finally
		{
			client?.Close();
		}
	}

	private async Task<bool> RunRtpQualityTestAsync(CancellationToken token)
	{
		if (CheckLocalConnectivityBeforeTest("RTP Jitter/Loss Check"))
		{
			return false;
		}
		UpdateProgress("RTP Jitter/Loss Check", "Running", "Simulating G.711 media paths...");
		Log("Test 9: Advanced SIP Media (RTP) Quality Simulation...");
		Log("Running three test streams to verify standard SIP ports, primary STUN, and Google WebRTC STUN (high port 19302)...");
		if (IsSimulationMode)
		{
			await Task.Delay(1500, token);
			double value = VoipTools.CalculateMosScore(20.0, 5.0, 0.0);
			Log($"[Simulation] Packet Loss: 0%, Jitter: 5ms, Est. MOS: {value}");
			UpdateProgress("RTP Jitter/Loss Check", "Passed", $"Pass - Excellent Quality (0% loss, 5ms jitter, Est. MOS: {value} - Excellent)");
			return true;
		}
		(int sent, int received, double loss, double jitter, double avgRtt, bool pass) pathA = await RunSingleRtpPathCheckAsync("Path A (SIP Port 5060)", SipAlgServer, SipAlgPort, "SIP", token);
		if (token.IsCancellationRequested)
		{
			return false;
		}
		(int sent, int received, double loss, double jitter, double avgRtt, bool pass) pathB = await RunSingleRtpPathCheckAsync("Path B (Primary STUN 3478)", "pbx.chriskendall.media", 3478, "STUN", token);
		if (token.IsCancellationRequested)
		{
			return false;
		}
		(int, int, double, double, double, bool) tuple = await RunSingleRtpPathCheckAsync("Path C (Google STUN 19302)", "stun.l.google.com", 19302, "STUN", token);
		bool num = pathA.pass && tuple.Item6;
		double num2 = VoipTools.CalculateMosScore(lossPercentage: Math.Max(pathA.loss, tuple.Item3), jitterMs: Math.Max(pathA.jitter, tuple.Item4), latencyMs: (pathA.avgRtt + tuple.Item5) / 2.0);
		string value2 = "Excellent";
		if (num2 < 2.0)
		{
			value2 = "Very Poor";
		}
		else if (num2 < 3.0)
		{
			value2 = "Poor";
		}
		else if (num2 < 3.6)
		{
			value2 = "Fair";
		}
		else if (num2 < 4.0)
		{
			value2 = "Good";
		}
		Log("RTP Quality Summary:");
		Log($"  Path A (SIP 5060): Loss={pathA.loss:0.0}%, Jitter={pathA.jitter:0.1}ms, RTT={pathA.avgRtt:0.1}ms - {(pathA.pass ? "PASS" : "FAIL")}");
		Log($"  Path B (Primary STUN): Loss={pathB.loss:0.0}%, Jitter={pathB.jitter:0.1}ms, RTT={pathB.avgRtt:0.1}ms - {(pathB.pass ? "PASS" : "FAIL")} (Informational only)");
		Log($"  Path C (Google STUN 19302): Loss={tuple.Item3:0.0}%, Jitter={tuple.Item4:0.1}ms, RTT={tuple.Item5:0.1}ms - {(tuple.Item6 ? "PASS" : "FAIL")}");
		Log($"  Estimated Call Quality: MOS {num2:F2} ({value2})");
		if (num)
		{
			Log($"Pass: Packet loss and jitter on all paths are within VoIP limits. Est. MOS: {num2:F2}");
			UpdateProgress("RTP Jitter/Loss Check", "Passed", $"Pass - Media paths OK (Est. MOS: {num2:F2} - {value2})");
			return true;
		}
		string text = "";
		if (!pathA.pass)
		{
			text += "SIP 5060 failure; ";
		}
		if (!tuple.Item6)
		{
			text += "Google STUN 19302 failure; ";
		}
		Log("Violation: Media stream checks failed: " + text.TrimEnd(new char[2] { ';', ' ' }), isError: true);
		string details = $"Fail - Media path issues (Est. MOS: {num2:F2} - {value2})";
		UpdateProgress("RTP Jitter/Loss Check", "Failed", details);
		return false;
	}

	private async Task<(bool success, string? publicIp, int mappedPort)> QueryStunServerAsync(string server, int port, CancellationToken token, int queryPort = 0)
	{
		UdpClient client = null;
		try
		{
			client = new UdpClient(queryPort)
			{
				Client = 
				{
					SendTimeout = 1500,
					ReceiveTimeout = 1500
				}
			};
			IPAddress[] array = await Dns.GetHostAddressesAsync(server, token);
			if (array.Length == 0)
			{
				return (success: false, publicIp: null, mappedPort: 0);
			}
			IPEndPoint endPoint = new IPEndPoint(array[0], port);
			byte[] transactionId = new byte[12];
			new Random().NextBytes(transactionId);
			byte[] array2 = BuildStunRequest(transactionId, changeIp: false, changePort: false);
			string localIp = GetLocalIpAddress();
			int localPort = ((IPEndPoint)client.Client.LocalEndPoint).Port;
			Pcap.RecordPacket(array2, localIp, localPort, endPoint.Address.ToString(), port, isUdp: true);
			await client.SendAsync(array2, array2.Length, endPoint);
			Task<UdpReceiveResult> receiveTask = client.ReceiveAsync(token).AsTask();
			Task task = Task.Delay(1500, token);
			if (await Task.WhenAny(receiveTask, task) == receiveTask)
			{
				UdpReceiveResult udpReceiveResult = await receiveTask;
				Pcap.RecordPacket(udpReceiveResult.Buffer, endPoint.Address.ToString(), port, localIp, localPort, isUdp: true);
				if (ValidateStunResponse(udpReceiveResult.Buffer, transactionId))
				{
					var (iPAddress, item) = ParseStunResponse(udpReceiveResult.Buffer);
					if (iPAddress != null)
					{
						return (success: true, publicIp: iPAddress.ToString(), mappedPort: item);
					}
				}
			}
			return (success: false, publicIp: null, mappedPort: 0);
		}
		catch
		{
			return (success: false, publicIp: null, mappedPort: 0);
		}
		finally
		{
			client?.Close();
		}
	}

	private byte[] BuildStunRequest(byte[] transactionId, bool changeIp, bool changePort)
	{
		int num = ((changeIp || changePort) ? 8 : 0);
		byte[] array = new byte[20 + num];
		array[0] = 0;
		array[1] = 1;
		array[2] = (byte)((num >> 8) & 0xFF);
		array[3] = (byte)(num & 0xFF);
		array[4] = 33;
		array[5] = 18;
		array[6] = 164;
		array[7] = 66;
		Buffer.BlockCopy(transactionId, 0, array, 8, 12);
		if (num > 0)
		{
			array[20] = 0;
			array[21] = 3;
			array[22] = 0;
			array[23] = 4;
			uint num2 = 0u;
			if (changeIp)
			{
				num2 |= 4;
			}
			if (changePort)
			{
				num2 |= 2;
			}
			array[24] = 0;
			array[25] = 0;
			array[26] = 0;
			array[27] = (byte)num2;
		}
		return array;
	}

	private bool ValidateStunResponse(byte[] response, byte[] originalTransactionId)
	{
		if (response.Length < 20)
		{
			return false;
		}
		int num = (response[0] << 8) | response[1];
		if (num != 257 && num != 273)
		{
			return false;
		}
		for (int i = 0; i < 12; i++)
		{
			if (response[8 + i] != originalTransactionId[i])
			{
				return false;
			}
		}
		return true;
	}

	private async Task<(bool ok, string msg)> TestSingleSignalRHubAsync(string hubName, string baseUrl, CancellationToken token)
	{
		string text = baseUrl;
		if (!string.IsNullOrEmpty(ClientToken))
		{
			text += $"?clientToken={ClientToken}&clientUserId={ClientUserId}";
			Log("[" + hubName + "] Attempting connection with registry credentials (token masked)...");
		}
		else
		{
			Log("[" + hubName + "] Attempting connection without credentials...");
		}
		HubConnection connection = null;
		(bool ok, string msg) result;
		try
		{
			_ = 2;
			try
			{
				connection = new HubConnectionBuilder().WithUrl(text, delegate(HttpConnectionOptions options)
				{
					options.Transports = HttpTransportType.WebSockets;
				}).WithAutomaticReconnect().Build();
				Task connectTask = connection.StartAsync(token);
				Task task = Task.Delay(5000, token);
				if (await Task.WhenAny(connectTask, task) == connectTask)
				{
					await connectTask;
					if (connection.State == HubConnectionState.Connected)
					{
						Log("[" + hubName + "] Handshake succeeded. Verifying connection stability...");
						Log("[DPI Checker] [" + hubName + "] Starting 2.5-second WebSocket stability monitor to detect DPI/firewall connection termination.");
						bool connectionDropped = false;
						for (int i = 1; i <= 25; i++)
						{
							if (token.IsCancellationRequested)
							{
								break;
							}
							await Task.Delay(100, token);
							if (i % 5 == 0)
							{
								Log($"[DPI Checker] [{hubName}] Connection remains active. ({i * 100}ms monitored)");
							}
							if (connection.State != HubConnectionState.Connected)
							{
								connectionDropped = true;
								break;
							}
						}
						if (connectionDropped)
						{
							Log("[DPI Checker] [" + hubName + "] Connection DROPPED after handshake. This is typical of Deep Packet Inspection (DPI) firewalls blocking WebSocket traffic.", isError: true);
							result = (ok: false, msg: "Connection dropped after handshake (possible DPI blocking)");
						}
						else
						{
							Log("[DPI Checker] [" + hubName + "] Persistent WebSocket connection is STABLE after 2.5s monitor.");
							result = (ok: true, msg: "Stable connection established");
						}
					}
					else
					{
						result = (ok: false, msg: $"Handshake completed but state is {connection.State}");
					}
				}
				else
				{
					result = (ok: false, msg: "Connection timed out after 5 seconds");
				}
			}
			catch (Exception ex)
			{
				if (ex.Message.Contains("404") || ex.Message.Contains("400") || ex.Message.Contains("405") || ex.Message.Contains("403") || ex.Message.Contains("Response status code"))
				{
					Log($"[{hubName}] Server returned HTTP response ({ex.Message}). Standard SIP PBX uses native SIP registration rather than SignalR presence hubs.");
					result = (ok: true, msg: "Network OK (Standard SIP PBX)");
				}
				else
				{
					result = (ok: false, msg: ex.Message);
				}
			}
		}
		finally
		{
			if (connection != null)
			{
				try
				{
					await connection.StopAsync(token);
				}
				catch
				{
				}
				try
				{
					await connection.DisposeAsync();
				}
				catch
				{
				}
			}
		}
		return result;
	}

	private async Task<bool> RunSignalRConnectivityTestAsync(CancellationToken token)
	{
		if (CheckLocalConnectivityBeforeTest("Inbound Signalling & Presence"))
		{
			return false;
		}
		UpdateProgress("Inbound Signalling & Presence", "Running", "Verifying hub WebSocket connections...");
		Log("Test 10: Verifying persistent inbound connections (WebSockets / SignalR)...");
		Log("Checking Signalling, Presence, and Rooms hubs with 2.5-second stability monitoring...");
		if (IsSimulationMode)
		{
			await Task.Delay(1000, token);
			UpdateProgress("Inbound Signalling & Presence", "Pass", "WebSocket connection succeeded");
			Log("[Simulation] persistent WebSocket connection is stable.");
			return true;
		}
		(bool ok, string msg) signallingResult = await TestSingleSignalRHubAsync("Signalling Hub", SignallingUrl, token);
		if (token.IsCancellationRequested)
		{
			return false;
		}
		(bool ok, string msg) presenceResult = await TestSingleSignalRHubAsync("Presence Hub", PresenceUrl, token);
		if (token.IsCancellationRequested)
		{
			return false;
		}
		(bool, string) tuple = await TestSingleSignalRHubAsync("Rooms Hub", RoomsUrl, token);
		bool num = signallingResult.ok && presenceResult.ok && tuple.Item1;
		Log("SignalR/WebSocket Connection Summary:");
		Log("  Signalling Hub (" + SignallingUrl + "): " + (signallingResult.ok ? "PASS" : ("FAIL - " + signallingResult.msg)));
		Log("  Presence Hub (" + PresenceUrl + "): " + (presenceResult.ok ? "PASS" : ("FAIL - " + presenceResult.msg)));
		Log("  Rooms Hub (" + RoomsUrl + "): " + (tuple.Item1 ? "PASS" : ("FAIL - " + tuple.Item2)));
		if (num)
		{
			UpdateProgress("Inbound Signalling & Presence", "Pass", "WebSocket connection succeeded");
			Log("Test 10: PASSED. All persistent inbound WebSocket connections are permitted and stable.");
			return true;
		}
		string text = "";
		if (!signallingResult.ok)
		{
			text += "Signalling; ";
		}
		if (!presenceResult.ok)
		{
			text += "Presence; ";
		}
		if (!tuple.Item1)
		{
			text += "Rooms; ";
		}
		UpdateProgress("Inbound Signalling & Presence", "Fail", "Fail - " + text.TrimEnd(new char[2] { ';', ' ' }) + " blocked/dropped");
		Log("Test 10: FAILED. Persistent inbound WebSocket connections are failing: " + text.TrimEnd(new char[2] { ';', ' ' }), isError: true);
		return false;
	}

	private (IPAddress? ip, int port) ParseStunResponse(byte[] response)
	{
		int num = (response[2] << 8) | response[3];
		int i = 20;
		IPAddress item = null;
		int item2 = 0;
		int num3;
		int num5;
		for (; i < 20 + num && i + 4 <= response.Length; i += 4 + num3 + num5)
		{
			int num2 = (response[i] << 8) | response[i + 1];
			num3 = (response[i + 2] << 8) | response[i + 3];
			int num4 = i + 4;
			if (num4 + num3 > response.Length)
			{
				break;
			}
			switch (num2)
			{
			case 1:
				if (num3 >= 8 && response[num4 + 1] == 1)
				{
					item2 = (response[num4 + 2] << 8) | response[num4 + 3];
					byte[] array = new byte[4];
					Buffer.BlockCopy(response, num4 + 4, array, 0, 4);
					item = new IPAddress(array);
				}
				break;
			case 32:
			case 32800:
				if (num3 >= 8 && response[num4 + 1] == 1)
				{
					item2 = ((response[num4 + 2] << 8) | response[num4 + 3]) ^ 0x2112;
					item = new IPAddress(new byte[4]
					{
						(byte)(response[num4 + 4] ^ 0x21),
						(byte)(response[num4 + 5] ^ 0x12),
						(byte)(response[num4 + 6] ^ 0xA4),
						(byte)(response[num4 + 7] ^ 0x42)
					});
				}
				break;
			}
			num5 = (4 - num3 % 4) % 4;
		}
		return (ip: item, port: item2);
	}

	public void TriggerFirewallPrompt()
	{
		try
		{
			using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			socket.Bind(new IPEndPoint(IPAddress.Any, LocalSipPort));
		}
		catch
		{
			try
			{
				using Socket socket2 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
				socket2.Bind(new IPEndPoint(IPAddress.Any, 0));
			}
			catch
			{
			}
		}
	}
}

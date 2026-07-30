using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MerlinSip.Models;

namespace MerlinSip.Services;

public static class VoipTools
{
	public static double CalculateMosScore(double latencyMs, double jitterMs, double lossPercentage)
	{
		double num = latencyMs + 2.0 * jitterMs + 10.0;
		double num2 = (!(num < 160.0)) ? (num / 40.0 + (num - 160.0) / 10.0) : (num / 40.0);
		double num3 = 95.0 * (lossPercentage / (lossPercentage + 10.0));
		double num4 = 93.2 - num2 - num3;
		if (num4 < 0.0)
		{
			num4 = 0.0;
		}
		double num5;
		if (num4 <= 0.0)
		{
			num5 = 1.0;
		}
		else if (num4 >= 100.0)
		{
			num5 = 4.4;
		}
		else
		{
			num5 = 1.0 + 0.035 * num4 + num4 * (num4 - 60.0) * (100.0 - num4) * 7E-06;
			if (num5 < 1.0)
			{
				num5 = 1.0;
			}
			if (num5 > 4.4)
			{
				num5 = 4.4;
			}
		}
		return Math.Round(num5, 2);
	}

	public static async Task<List<SrvRecord>> ResolveSrvAsync(string service, string domain, string dnsServer = "8.8.8.8")
	{
		List<SrvRecord> records = new List<SrvRecord>();
		try
		{
			using UdpClient client = new UdpClient(0);
			client.Client.SendTimeout = 3000;
			client.Client.ReceiveTimeout = 3000;
			ushort txId = (ushort)Random.Shared.Next(1, 65535);
			byte[] array = BuildSrvQuery(service, domain, txId);
			IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(dnsServer), 53);
			await client.SendAsync(array, array.Length, endPoint);
			Task<UdpReceiveResult> receiveTask = client.ReceiveAsync();
			Task task = Task.Delay(3000);
			if (await Task.WhenAny(receiveTask, task) == receiveTask)
			{
				UdpReceiveResult udpReceiveResult = await receiveTask;
				if (udpReceiveResult.Buffer.Length > 12 && (ushort)((udpReceiveResult.Buffer[0] << 8) | udpReceiveResult.Buffer[1]) == txId)
				{
					records = ParseSrvResponse(udpReceiveResult.Buffer);
				}
			}
		}
		catch
		{
		}
		return records;
	}

	private static byte[] BuildSrvQuery(string service, string domain, ushort transactionId)
	{
		List<byte> list = new List<byte>();
		list.Add((byte)(transactionId >> 8));
		list.Add((byte)(transactionId & 0xFF));
		list.Add(1);
		list.Add(0);
		list.Add(0);
		list.Add(1);
		list.Add(0);
		list.Add(0);
		list.Add(0);
		list.Add(0);
		list.Add(0);
		list.Add(0);
		string[] array = (service.Trim().TrimEnd('.') + "." + domain.Trim().TrimStart('.').TrimEnd('.')).Split('.');
		foreach (string s in array)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(s);
			list.Add((byte)bytes.Length);
			list.AddRange(bytes);
		}
		list.Add(0);
		list.Add(0);
		list.Add(33);
		list.Add(0);
		list.Add(1);
		return list.ToArray();
	}

	private static List<SrvRecord> ParseSrvResponse(byte[] response)
	{
		List<SrvRecord> list = new List<SrvRecord>();
		if (response.Length < 12)
		{
			return list;
		}
		ushort num = (ushort)((response[4] << 8) | response[5]);
		ushort num2 = (ushort)((response[6] << 8) | response[7]);
		int offset = 12;
		for (int i = 0; i < num; i++)
		{
			SkipName(response, ref offset);
			offset += 4;
		}
		for (int j = 0; j < num2; j++)
		{
			SkipName(response, ref offset);
			if (offset + 10 > response.Length)
			{
				break;
			}
			ushort num3 = (ushort)((response[offset] << 8) | response[offset + 1]);
			offset += 8;
			ushort num4 = (ushort)((response[offset] << 8) | response[offset + 1]);
			offset += 2;
			int num5 = offset;
			if (num3 == 33 && num5 + num4 <= response.Length)
			{
				ushort priority = (ushort)((response[num5] << 8) | response[num5 + 1]);
				ushort weight = (ushort)((response[num5 + 2] << 8) | response[num5 + 3]);
				ushort port = (ushort)((response[num5 + 4] << 8) | response[num5 + 5]);
				int offset2 = num5 + 6;
				string target = ParseName(response, ref offset2);
				list.Add(new SrvRecord
				{
					Priority = priority,
					Weight = weight,
					Port = port,
					Target = target
				});
			}
			offset += num4;
		}
		return list;
	}

	private static void SkipName(byte[] response, ref int offset)
	{
		while (offset < response.Length)
		{
			byte b = response[offset];
			if (b == 0)
			{
				offset++;
				break;
			}
			if ((b & 0xC0) == 192)
			{
				offset += 2;
				break;
			}
			offset += 1 + b;
		}
	}

	private static string ParseName(byte[] response, ref int offset)
	{
		List<string> list = new List<string>();
		int num = offset;
		bool flag = false;
		int num2 = -1;
		int num3 = 0;
		while (num < response.Length)
		{
			byte b = response[num];
			if (b == 0)
			{
				num++;
				break;
			}
			if ((b & 0xC0) == 192)
			{
				if (num + 1 >= response.Length)
				{
					break;
				}
				int num4 = ((b & 0x3F) << 8) | response[num + 1];
				if (!flag)
				{
					num2 = num + 2;
					flag = true;
				}
				num = num4;
				num3++;
				if (num3 > 20)
				{
					break;
				}
			}
			else
			{
				num++;
				if (num + b > response.Length)
				{
					break;
				}
				list.Add(Encoding.UTF8.GetString(response, num, b));
				num += b;
			}
		}
		offset = (flag ? num2 : num);
		return string.Join(".", list);
	}

	public static async Task<PortProbeResult> ProbeTcpPortAsync(string target, int port, string serviceName, CancellationToken token)
	{
		PortProbeResult result = new PortProbeResult
		{
			Port = port,
			Protocol = "TCP",
			ServiceName = serviceName,
			Target = target,
			Status = "Closed",
			RttMs = null
		};
		Stopwatch sw = Stopwatch.StartNew();
		try
		{
			using TcpClient client = new TcpClient();
			Task connectTask = client.ConnectAsync(target, port);
			Task task = Task.Delay(1500, token);
			if (await Task.WhenAny(connectTask, task) == connectTask)
			{
				await connectTask;
				sw.Stop();
				result.Status = "Open";
				result.RttMs = sw.Elapsed.TotalMilliseconds;
			}
			else
			{
				result.Status = "Blocked";
			}
		}
		catch (SocketException ex)
		{
			if (ex.SocketErrorCode == SocketError.ConnectionRefused)
			{
				result.Status = "Closed";
			}
			else
			{
				result.Status = "Blocked";
			}
		}
		catch
		{
			result.Status = "Blocked";
		}
		return result;
	}

	public static async Task<PortProbeResult> ProbeUdpPortAsync(string target, int port, string serviceName, CancellationToken token)
	{
		PortProbeResult result = new PortProbeResult
		{
			Port = port,
			Protocol = "UDP",
			ServiceName = serviceName,
			Target = target,
			Status = "Unresponsive",
			RttMs = null
		};
		Stopwatch sw = Stopwatch.StartNew();
		try
		{
			IPAddress[] array = await Dns.GetHostAddressesAsync(target, token);
			if (array.Length == 0)
			{
				result.Status = "Blocked";
				return result;
			}
			IPEndPoint endPoint = new IPEndPoint(array[0], port);
			using UdpClient client = new UdpClient(0);
			client.Client.SendTimeout = 1500;
			client.Client.ReceiveTimeout = 1500;
			byte[] array2;
			switch (port)
			{
			case 3478:
			{
				byte[] array3 = new byte[12];
				Random.Shared.NextBytes(array3);
				array2 = BuildStunRequest(array3);
				break;
			}
			case 5060:
			case 5061:
				array2 = BuildSipOptionsRequest(target, port);
				break;
			default:
				array2 = new byte[4] { 13, 10, 13, 10 };
				break;
			}
			await client.SendAsync(array2, array2.Length, endPoint);
			Task<UdpReceiveResult> receiveTask = client.ReceiveAsync(token).AsTask();
			Task task = Task.Delay(1500, token);
			if (await Task.WhenAny(receiveTask, task) == receiveTask)
			{
				UdpReceiveResult udpReceiveResult = await receiveTask;
				sw.Stop();
				switch (port)
				{
				case 3478:
					if (udpReceiveResult.Buffer.Length >= 20 && ((udpReceiveResult.Buffer[0] << 8) | udpReceiveResult.Buffer[1]) == 257)
					{
						result.Status = "Open";
					}
					break;
				case 5060:
				case 5061:
					if (Encoding.UTF8.GetString(udpReceiveResult.Buffer).Contains("SIP/2.0"))
					{
						result.Status = "Open";
					}
					break;
				default:
					result.Status = "Open";
					break;
				}
				result.RttMs = sw.Elapsed.TotalMilliseconds;
			}
			else
			{
				result.Status = "Unresponsive";
			}
		}
		catch (SocketException ex)
		{
			if (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.ConnectionRefused)
			{
				result.Status = "Open";
			}
			else
			{
				result.Status = "Blocked";
			}
		}
		catch
		{
			result.Status = "Blocked";
		}
		return result;
	}

	private static byte[] BuildStunRequest(byte[] transactionId)
	{
		byte[] array = new byte[20]
		{
			0, 1, 0, 0, 33, 18, 164, 66, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0
		};
		Buffer.BlockCopy(transactionId, 0, array, 8, 12);
		return array;
	}

	private static byte[] BuildSipOptionsRequest(string host, int port)
	{
		string value = "z9hG4bK" + Guid.NewGuid().ToString("N").Substring(0, 10);
		string value2 = Guid.NewGuid().ToString("N").Substring(0, 10);
		string value3 = Guid.NewGuid().ToString("N").Substring(0, 16) + "@chriskendall.media";
		string value4 = "127.0.0.1";
		int value5 = 5060;
		string s = $"OPTIONS sip:{host} SIP/2.0\r\nVia: SIP/2.0/UDP {value4}:{value5};rport;branch={value}\r\nMax-Forwards: 70\r\nTo: <sip:checker@{host}>\r\nFrom: <sip:checker@{value4}:{value5}>;tag={value2}\r\nCall-ID: {value3}\r\nCSeq: 1 OPTIONS\r\nContact: <sip:checker@{value4}:{value5}>\r\nUser-Agent: CK Media Services SIP Toolkit\r\nContent-Length: 0\r\n\r\n";
		return Encoding.UTF8.GetBytes(s);
	}
}

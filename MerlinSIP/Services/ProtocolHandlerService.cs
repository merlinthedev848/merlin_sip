using System;
using System.Reflection;
using Microsoft.Win32;

namespace MerlinSip.Services;

public static class ProtocolHandlerService
{
	public static void RegisterProtocolHandlers()
	{
		try
		{
			string text = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
			if (!string.IsNullOrWhiteSpace(text))
			{
				RegisterProtocol("tel", "URL:Tel Protocol", text);
				RegisterProtocol("callto", "URL:CallTo Protocol", text);
				RegisterProtocol("sip", "URL:SIP Protocol", text);
				DebugLog.Write("Registered Windows tel:, callto:, and sip: protocol handlers.");
			}
		}
		catch (Exception ex)
		{
			DebugLog.Write("Failed to register protocol handlers: " + ex.Message);
		}
	}

	private static void RegisterProtocol(string scheme, string description, string exePath)
	{
		using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Classes\\" + scheme);
		registryKey.SetValue("", description);
		registryKey.SetValue("URL Protocol", "");
		using RegistryKey registryKey2 = registryKey.CreateSubKey("DefaultIcon");
		registryKey2.SetValue("", "\"" + exePath + "\",0");
		using RegistryKey registryKey3 = registryKey.CreateSubKey("shell\\open\\command");
		registryKey3.SetValue("", "\"" + exePath + "\" \"%1\"");
	}

	public static string ParseTelUrl(string input)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return string.Empty;
		}
		string text = input.Trim();
		if (text.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring(4);
		}
		else if (text.StartsWith("callto:", StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring(7);
		}
		else if (text.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring(4);
		}
		int num = text.IndexOf('?');
		if (num >= 0)
		{
			text = text.Substring(0, num);
		}
		return Uri.UnescapeDataString(text).Trim();
	}
}

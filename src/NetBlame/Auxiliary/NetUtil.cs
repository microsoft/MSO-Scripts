// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices; // MethodImpl

using Microsoft.Windows.EventTracing.Symbols;

using QWord = System.UInt64;


namespace NetBlameCustomDataSource
{
	public enum Protocol : byte
	{
		// Order of increasing priority
		Unknown = 0, // anomalous
		Rundown = 1, // preexisting connection
		TCP = 2,
		UDP = 4,
		Winsock = 8,
		WinINet = 16,
		WinHTTP = 32, // WebIO
		Chromium = 64
	};

	public static class Util
	{
		public static string strNA = "N/A";

		static int assertLevel = 0; // >=-1: Critical, >=0: Important, >0: Info

		[Conditional("DEBUG")]
		[DebuggerStepThrough()]
		public static void AssertInfo(bool c)
		{
			if (!c)
			{
				if (assertLevel > 0 && Debugger.IsAttached)
					Debugger.Break();
			}
		}

		[Conditional("DEBUG")]
		[DebuggerStepThrough()]
		public static void AssertImportant(bool c)
		{
			if (!c)
			{
				if (assertLevel >= 0 && Debugger.IsAttached)
					Debugger.Break();
			}
		}

		[Conditional("DEBUG")]
		[DebuggerStepThrough()]
		public static void AssertCritical(bool c)
		{
			if (!c)
			{
				if (assertLevel >= -1 && Debugger.IsAttached)
					Debugger.Break();
			}
		}

		[Conditional("DEBUG")]
		public static void DEBUG<T>(T e) { }

#if DEBUG
		// Must remove references to build RELEASE.
		[DebuggerStepThrough()]
		public static void BreakWhen(bool c)
		{
			if (c)
			{
				if (assertLevel >= -1 && Debugger.IsAttached)
					Debugger.Break();
			}
		}
#endif // DEBUG

		public static bool SUCCEEDED(UInt32 err) => (Int32)err >= 0;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool FImplies(bool a, bool b) => !a || b;

		// https://github.com/dotnet/runtime/issues/58378
		public static AddressFamily AF_HYPERV = (AddressFamily)34;
		public static AddressFamily AF_VSOCK = (AddressFamily)40;

		static readonly IPEndPoint ipEndPointv4 = new IPEndPoint(0, 0);
		static readonly IPEndPoint ipEndPointv6 = new IPEndPoint(IPAddress.IPv6Any, 0);

		public static IPEndPoint NewEndPoint(in SocketAddress socket)
		{
			if (socket.Empty())
				return new IPEndPoint(0, 0);

			// IPEndPoint.Create throws an exception if (this.AddressFamily != socket.Family)

			if (socket.Family == AddressFamily.InterNetworkV6)
			{
				IPEndPoint ipep = (IPEndPoint)ipEndPointv6.Create(socket);
				ipep.Address.ScopeId = 0; // We ignore the zone index for simplicity.
				return ipep;
			}

			if (socket.Family == AddressFamily.InterNetwork)
				return (IPEndPoint)ipEndPointv4.Create(socket);

			// Handle other AddressFamily values as best we can.
			// Ultimately everything must be expressed as IPv4 or IPv6.

			// AF_HYPERV & AF_VSOCK:
			// https://learn.microsoft.com/en-us/virtualization/hyper-v-on-windows/user-guide/make-integration-service#bind-to-a-hyper-v-socket
			// https://man7.org/linux/man-pages/man7/vsock.7.html
			// https://github.com/search?q=repo%3Amicrosoft%2FWSL2-Linux-Kernel+AF_HYPERV

			if (socket.Family == AF_HYPERV && socket.Size >= 20)
			{
				// Create an IpV6 socket and copy the VmId (GUID) to the IpV6 address, and that's the best we can do.

				SocketAddress sa = new SocketAddress(AddressFamily.InterNetworkV6, 64);

				// Copy the VmId GUID into the IPv6 address such that they display similarly.
				sa[0+8] = socket[3+4]; sa[3+8] = socket[0+4];
				sa[1+8] = socket[2+4]; sa[2+8] = socket[1+4];
				sa[4+8] = socket[5+4]; sa[5+8] = socket[4+4];
				sa[6+8] = socket[7+4]; sa[7+8] = socket[6+4];
				for (int i = 8; i < 16; ++i) { sa[i+8] = socket[i+4]; }
				sa[3] = (byte)AF_HYPERV; // port = HyperV tag, big-endian

				return (IPEndPoint)ipEndPointv6.Create(sa);
			}

			if (socket.Family == AF_HYPERV && socket.Size >= 16)
			{
				// Create an IpV6 socket and copy the data to the IpV6 address, and that's the best we can do.

				SocketAddress sa = new SocketAddress(AddressFamily.InterNetworkV6, 64);

				for (int i = 0; i < 12; ++i) { sa[i+12] = socket[i+4]; }
				sa[3] = (byte)AF_HYPERV; // port = HyperV tag, big-endian

				return (IPEndPoint)ipEndPointv6.Create(sa);
			}

			if (socket.Family == AF_VSOCK && socket.Size >= 12)
			{
				// Copy the svm_port and the svm_cid to the IPv6 address, and that's the best we can do.

				SocketAddress sa = new SocketAddress(AddressFamily.InterNetworkV6, 64);

				// Copy the CID (address) and Port into the IpV6 address such that they display as: cid::port
				sa[0+8] = socket[3+8]; sa[1+8] = socket[2+8];
				sa[2+8] = socket[1+8]; sa[3+8] = socket[0+8];
				sa[0+20] = socket[3+4]; sa[1+20] = socket[2+4];
				sa[2+20] = socket[1+4]; sa[3+20] = socket[0+4];
				sa[3] = (byte)AF_VSOCK; // port = VSock tag, big-endian

				return (IPEndPoint)ipEndPointv6.Create(sa);
			}

			// Catch-all

			// dummy: 42.42.42.42 / port = family
			return new IPEndPoint((Int64)0x2A2A2A2A, (int)socket.Family);
		} // NewEndPoint

		public static IPEndPoint NewEndPoint(in Microsoft.Windows.EventTracing.Events.IGenericEvent evt)
		{
			if (evt.GetUInt32("AddressLen") == 0)
				return new IPEndPoint(0, 0);

			return NewEndPoint(evt.GetSocketAddress());
		}


		static public readonly char[] rgchEOLSplit = new char[] { '\r', '\n' };


		static public string ServerNameFromURL(string strURL)
		{
			if (!strURL.IsNA())
			{
				if (Uri.TryCreate(strURL, UriKind.Absolute, out Uri uri))
					return uri.Host;
			}

			return string.Empty;
		}

		/*
			Return true if the server part of the URL strings is the same (case insensitive).
		*/
		static public bool FSameServer(string strURL1, string strURL2)
		{
			string strServer1 = ServerNameFromURL(strURL1);
			string strServer2 = ServerNameFromURL(strURL2);

			if (String.IsNullOrWhiteSpace(strServer1))
				return false;

			return String.Equals(strServer1, strServer2, StringComparison.OrdinalIgnoreCase);
		}

		/*
			The Protocol is OR-able.
			Strip off the lower priority protocol bits, returning just the MSB.
		*/
		static public Protocol Prominent(Protocol b)
		{
			while (true)
			{
				var bNext = b & (b - 1);
				if (bNext == 0) break;
				b = (Protocol)bNext;
			}
			return b;
		}


		/*
			Return true if a module with the given name.ext appears in the call stack.
		*/
		static bool ModuleInStack(IStackSnapshot stack, string module)
		{
			if (stack?.Frames == null)
				return false;

			foreach (var frame in stack.Frames)
			{
				string name = frame.Image?.FileName;
				if (module.Equals(name))
					return true;
			}
			return false;
		}


		const int portLDAP = 389;

		/*
			Name some common TCP/UDP ports.
			https://en.wikipedia.org/wiki/List_of_TCP_and_UDP_port_numbers
		*/
		static public string ServiceFromPort(int port)
		{
			return port switch
			{
				20 or 21 => "FTP",
				22 => "SSH/SCP/SFTP",
				23 => "TELNET",
				25 => "SMTP",
				53 => "DNS",
				80 or 8080 or 8081 => "HTTP",
				88 => "Kerberos",
				110 => "POP3",
				119 => "NNTP",
				123 => "NTP",
				135 => "DCE/DHCP/DNS/WINS/DCOM",
				137 => "NetBIOS",
				143 => "IMAP",
				portLDAP => "LDAP", // 389
				443 or 8443 => "HTTPS",
				445 => "AD/SMB",
				465 => "SMTPS",
				546 or 547 => "DHCPv6",
				554 => "RTSP",
				563 => "NNTPS",
				636 => "LDAPs",
				853 => "DNS/TLS",
				989 or 990 => "FTPS",
				993 => "IMAPS",
				995 => "POP3S",
				1433 => "MSSQL",
				1900 => "SSDP",
				2555 or 2869 or 5000 => "UPnP",
				3268 => "LDAP/AD",
				3269 => "LDAPs/AD",
				3389 => "TS/RDP",
				5353 => "mDNS",
				5355 => "LLMNR",
				5985 => "CIM/DMTF",
				7680 => "DeliveryOpt",
				8888 => "HTTP/LocalHost",
				_ => null
			};
		}


		/*
			Return "LDAP/TCP" or "LDAP/UDP" or "[PortService]" or "[PortService]/UDP", etc.
		*/
		static public string ComposeMethod(this WinsockAFD.Connection cxn)
		{
			string service = ServiceFromPort(cxn.addrRemote.Port);

			if (service == null)
			{
				// Not sure what the service is. Might still be LDAP, else just return the protocol: TCP or UDP
				if (cxn.fSuperConnect || !ModuleInStack(cxn.stack, "Wldap32.dll"))
					return cxn.ipProtocol.ToString();

				// It's some form of LDAP: an LDAP module is in the call stack (and not AFD_SUPER_CONNECT).
				service = ServiceFromPort(portLDAP);
			}

			// "DNS" => "DNS:UDP" etc.
			if (cxn.ipProtocol != WinsockAFD.IPPROTO.TCP)
				service += ":" + cxn.ipProtocol.ToString();

			return service;
		}

		static class IPSpecial
		{
			const string strGoogleDNS = "Google DNS";
			const string strCFlareDNS = "Cloudflare DNS";
			const string strQuad9DNS = "Quad9 DNS";
			const string strOpenDNS = "OpenDNS";

			private static readonly System.Collections.Generic.Dictionary<IPAddress, string> _map =
				new()
				{
					{ IPAddress.Parse("1.0.0.1"), strCFlareDNS },
					{ IPAddress.Parse("1.1.1.1"), strCFlareDNS },
					{ IPAddress.Parse("[2606:4700:4700::1001]"), strCFlareDNS },
					{ IPAddress.Parse("[2606:4700:4700::1111]"), strCFlareDNS },
					{ IPAddress.Parse("8.8.4.4"), strGoogleDNS},
					{ IPAddress.Parse("8.8.8.8"), strGoogleDNS},
					{ IPAddress.Parse("[2001:4860:4860::8844]"), strGoogleDNS },
					{ IPAddress.Parse("[2001:4860:4860::8888]"), strGoogleDNS },
					{ IPAddress.Parse("9.9.9.9"), strQuad9DNS},
					{ IPAddress.Parse("[2620:fe::fe]"), strQuad9DNS},
					{ IPAddress.Parse("[2620:fe::9]"), strQuad9DNS},
					{ IPAddress.Parse("208.67.222.222"), strOpenDNS },
					{ IPAddress.Parse("[2620:0:ccc::2]"), strOpenDNS },
					{ IPAddress.Parse("[2620:0:ccd::2]"), strOpenDNS },
					{ IPAddress.Parse("255.255.255.255"), "Broadcast"}
				};

			public static string Get(IPAddress ip) => _map.TryGetValue(ip, out var v) ? v : null;
		}

		static public string AddressType(IPAddress addr)
		{
			if (addr.Empty())
				return null;

			if (addr.IsIPv4MappedToIPv6)
				addr = addr.MapToIPv4();

			if (addr.AddressFamily == AddressFamily.InterNetwork)
			{
				byte[] bytes = addr.GetAddressBytes();
				switch (bytes[0])
				{
				case 10:
					return "Private Network (A)";
				case 127:
					return "Loopback";
				case 169:
					if (bytes[1] == 254)
						return "Link-Local";
					break;
				case 172:
					if (bytes[1] >= 16 && bytes[1] < 32)
						return "Private Network (B)";
					break;
				case 192:
					if (bytes[1] == 168)
						return "Private Network (C)";
					break;
				default:
					if (bytes[0] >= 224 && bytes[0] < 240)
						return "Multicast";
					break;
				}
				return IPSpecial.Get(addr); // usually null
			}

			if ((int)addr.AddressFamily == 34)
				return "Hyper-V";

			if (addr.AddressFamily != AddressFamily.InterNetworkV6)
				return null;

			if (addr.IsIPv6LinkLocal)
				return "Link Local";

			if (addr.IsIPv6Multicast)
				return "Multicast";

			if (addr.IsIPv6SiteLocal)
				return "Site Local";

			if (addr.IsIPv6UniqueLocal)
				return "Unique Local";

			return IPSpecial.Get(addr); // usually null
		}
	} // class Util
}

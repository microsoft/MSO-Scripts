﻿// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Net;
using System.Net.Sockets;

using System.Diagnostics;
using Microsoft.Windows.EventTracing.Symbols;

using QWord = System.UInt64;


namespace NetBlameCustomDataSource
{
	public enum Protocol : byte
	{
		// Order of increasing priority
		Unknown = 0, // anomalous
		Rundown = 1, // preexisting connection
		TCP     = 2,
		UDP     = 4,
		Winsock = 8,
		WinINet = 16,
		WinHTTP = 32 // WebIO
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

		public static bool FImplies(bool a, bool b) => !a || b;

		public static uint BitFromI(uint i) => (uint)1 << ((int)i - 1); // i: 1-based

		static readonly IPEndPoint ipEndPointv4 = new IPEndPoint(0, 0);
		static readonly IPEndPoint ipEndPointv6 = new IPEndPoint(IPAddress.IPv6Any, 0);

		public static IPEndPoint NewEndPoint(in SocketAddress socket)
		{
			// TODO: What about other AddressFamily values? AF_HYPERV = 34
			AssertCritical(socket.Family == AddressFamily.InterNetwork || socket.Family == AddressFamily.InterNetworkV6);

			// IPEndPoint.Create throws an exception if (this.AddressFamily != socket.Family)

			if (socket.Family == AddressFamily.InterNetworkV6)
			{
				IPEndPoint ipep = (IPEndPoint)ipEndPointv6.Create(socket);
				ipep.Address.ScopeId = 0; // We ignore the zone index for simplicity.
				return ipep;
			}

			if (socket.Family == AddressFamily.InterNetwork)
				return (IPEndPoint)ipEndPointv4.Create(socket);

			// TODO: Handle other AddressFamily values: extract their address/port
			// Ultimately everything must be expressed as IPv4 or IPv6.

			return new IPEndPoint((Int64)0x42424242, 42); // dummy ipv6 address/port
		}

		static public readonly char[] rgchURLSplit = new char[] { ':', '/' };
		static public readonly char[] rgchEOLSplit = new char[] { '\r', '\n' };

		/*
			Return the string "ServerName" from "http:// ServerName / Path"
			Else return String.Empty (never null).
		*/
		static public string ServerNameFromURL(string strURL)
		{
			if (strURL.IsNA())
				return String.Empty;

			// Skip past the "http://"

			string[] rgstrURL = strURL.Split(rgchURLSplit, StringSplitOptions.RemoveEmptyEntries);

			if (rgstrURL.Length == 0)
				return String.Empty;

			if (rgstrURL.Length > 1 && rgstrURL[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
				return rgstrURL[1].ToLowerInvariant();

			return rgstrURL[0].ToLowerInvariant();
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
			switch (port)
			{
			case 53:
				return "DNS";
			case 80:
			case 8080:
			case 8081:
				return "HTTP";
			case 88:
				return "Kerberos";
			case 135:
				return "DCE/DHCP/DNS/WINS/DCOM";
			case portLDAP:
				return "LDAP";
			case 443:
			case 8443:
				return "HTTPS";
			case 445:
				return "AD/SMB";
			case 636:
				return "LDAPs";
			case 1433:
				return "MSSQL";
			case 2555:
				return "UPnP";
			case 3268:
				return "LDAP/AD";
			case 3269:
				return "LDAPs/AD";
			case 3389:
				return "TS/RDP";
			case 5985:
				return "CIM/DMTF";
			case 7680:
				return "DeliveryOpt";
			case 8888:
				return "HTTP/LocalHost";
			default:
				return null;
			}
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


		/*
			qw.RotateLeft()
			.NET Core 3:0 - System.Numerics.BitOperations.RotateLeft()
		*/
		static public void RotateLeft(ref this QWord ul) => ul = (ul << 1) | (ul >> 63);

		/*
			Return the 1-based index of the lowest bit, else return 0.
		*/
		static public uint BitScanLeft(uint grbit)
		{
			uint i = 0;
			for (grbit &= (uint)-grbit /*isolate low bit*/; grbit != 0; grbit >>= 1) ++i;
			return i;
		}
	} // class Util
}

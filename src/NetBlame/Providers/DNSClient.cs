// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic; // List<>
using System.Linq;
using System.Net; // IPAddress
using System.Net.Sockets; // AddressFamily

using Microsoft.Windows.EventTracing.Events;

using static NetBlameCustomDataSource.Util; // Assert


namespace NetBlameCustomDataSource.DNSClient
{
	public class DNSEntry
	{
		public string strServer;
		public string strNameAlt;

		public List<IPAddress> rgIpAddr;

		void Init() { this.rgIpAddr = new List<IPAddress>(8); }
		public DNSEntry() { Init(); }
		public DNSEntry(string strSrv) { strServer = strSrv; Init(); }
	}

	public class DNSTable : List<DNSEntry>
	{
		public const string strLocalHost = "localhost";
		public const string strAddrLocalHost = "127.0.0.1";

		public DNSTable(int capacity) : base(capacity)
		{
			ParseDNSEntries(strLocalHost, strAddrLocalHost);
		}

		/*
			1-based index -> DNSEntry, else null
		*/
		public DNSEntry DNSEntryFromI(uint iDNSEntry) => (iDNSEntry != 0) ? this[(int)iDNSEntry-1] : null;

		/*
			1-based iDNS,iAddr -> IPAddress, else null
		*/
		public IPAddress AddressFromI(uint iDNS, uint iAddr)
		{
			DNSEntry dnsE = DNSEntryFromI(iDNS);
			if (dnsE?.rgIpAddr == null) return null;
			if (iAddr == 0 || (int)iAddr > dnsE.rgIpAddr.Count) return null;
			return dnsE.rgIpAddr[(int)iAddr-1];
		}


		/*
			Return the 1-based index of the earliest DNS entry with the given address, else 0.
		*/
		public uint IDNSFromAddress(IPAddress ipAddress)
		{
			if (ipAddress.Empty())
				return 0;

			if (ipAddress.IsIPv4MappedToIPv6)
				ipAddress = ipAddress.MapToIPv4();

			for (int iDNS = 0; iDNS < this.Count; ++iDNS)
			{
				if (this[iDNS].rgIpAddr.IndexOf(ipAddress) >= 0)
					return (uint)iDNS + 1;
			}
			return 0;
		}

		/*
			Find and return the 1-based DNS index, with the IP address which occurs earliest in its list.
			iAddrOut is 0-based.
		*/
		public uint IFindDNSEntryByIPAddress0(IPAddress ipAddress, out int iAddrOut)
		{
			uint iDnsFound = 0;
			iAddrOut = int.MaxValue;

			if (ipAddress.IsIPv4MappedToIPv6)
				ipAddress = ipAddress.MapToIPv4();

			for (int iDNS = 0; iDNS < this.Count; ++iDNS)
			{
				int iAddr = this[iDNS].rgIpAddr.IndexOf(ipAddress);
				if (iAddr >= 0 && iAddrOut > iAddr)
				{
					iAddrOut = iAddr;
					iDnsFound = (uint)iDNS+1;
				}
			}
			return iDnsFound;
		}

		/*
			Find and return the 1-based DNS index, with the IP address which occurs earliest in its list.
			iAddrOut is 1-based.
		*/
		public uint IFindDNSEntryByIPAddress1(IPAddress ipAddress, out uint iAddrOutU)
		{
			uint iDNS = IFindDNSEntryByIPAddress0(ipAddress, out int iAddrOut);
			iAddrOutU = (iDNS != 0) ? (uint)iAddrOut + 1 : 0;
			return iDNS;
		}

		/*
			Find and return the oldest DNSEntry which contains the given address.
		*/
		DNSEntry FindDNSEntryByIPAddressRev(IPAddress ipAddress)
		{
			if (ipAddress.IsIPv4MappedToIPv6)
				ipAddress = ipAddress.MapToIPv4();

			foreach (DNSEntry dnsE in this)
			{
				if (dnsE.rgIpAddr.IndexOf(ipAddress) >= 0)
					return dnsE;
			}
			return null;
		}

		/*
			Return the 1-based index of DNS record with server name = strNA and with the given address.
		*/
		uint IFindBlankDNSEntryByAddress(IPAddress ipAddress)
		{
			if (ipAddress == null)
				return 0;

			int i = this.FindIndex(dns => dns.strServer.IsNA() && dns.rgIpAddr.FindIndex(ipa => ipa.Equals(ipAddress)) >= 0);
			return (uint)(i + 1);
		}

		/*
			Find the given address in the given DNS entry and return the 1-based index, else 0.
		*/
		public uint IFindAddress(uint iDNS, uint grbitAddrDNS, IPAddress ipAddr)
	   	{
			if (iDNS == 0)
				return 0;
		
			DNSEntry dnsE = DNSEntryFromI(iDNS);

			if (ipAddr.IsIPv4MappedToIPv6)
				ipAddr = ipAddr.MapToIPv4();

			for (int iAddr = 0; iAddr < dnsE.rgIpAddr.Count; ++iAddr, grbitAddrDNS >>= 1)
			{
				// The 32-bit grbit thing is an optimization. If there are more than 32 addresses, test them all.
				if ((grbitAddrDNS & 1) == 0 && iAddr < 32)
					continue;

				if (dnsE.rgIpAddr[iAddr].Equals(ipAddr))
					return (uint)iAddr + 1;
			}

			AssertImportant(grbitAddrDNS == 0); // Tested all the addresses?

			return 0;
		}

		/*
			Return the 1-based index of the most recent DNSEntry with that server name, else 0.
		*/
		public uint IFindDNSEntryByServer(string srvName)
		{
			AssertImportant(srvName != null);

			if (srvName.IsNA())
				return 0;

			for (int iDNS = this.Count - 1; iDNS >= 0; --iDNS)
			{
				if (String.Equals(srvName, this[iDNS].strServer, StringComparison.OrdinalIgnoreCase))
					return (uint)iDNS+1;
			}
			return 0;
		}


		/*
			Return the server name and an alternate server name for the given iDNS/iAddr.
			Returns a string for the server name; may be null.
			Returns a string for the alternate name; should be pre-initialized to a default value.
		*/
		string DNSNameAndAlt(uint iDNS, ref string strDNSAlt, int iAddr /*0-based*/)
		{
			string strDNSName = null;

			if (iDNS != 0)
			{
				DNSEntry dns = this.DNSEntryFromI(iDNS);

				if (dns.strNameAlt != null)
				{
					strDNSAlt = dns.strNameAlt;
				}
				else if (dns.rgIpAddr.Count > iAddr)
				{
					// Find an alternate match.
					DNSEntry dnsAlt = FindDNSEntryByIPAddressRev(dns.rgIpAddr[iAddr]);

					if (dnsAlt != dns)
					{
						if (dnsAlt.strNameAlt != null)
							strDNSAlt = dnsAlt.strNameAlt;
						else if (!dns.strServer.Equals(dnsAlt.strServer, StringComparison.OrdinalIgnoreCase))
							strDNSAlt = dnsAlt.strServer;
					}
				}

				strDNSName = dns.strServer;
			}

			return strDNSName;
		}

		string DNSNameAndAlt(IPAddress ipAddr, ref string strDNSAlt)
		{
			uint iDNS = IFindDNSEntryByIPAddress0(ipAddr, out int iAddr);
			return DNSNameAndAlt(iDNS, ref strDNSAlt, iAddr /*0-based*/);
		}

		string GetServerNameAndAlt(string strURL /*opt*/, string strServer /*opt*/, string strServer2 /*opt*/, string strServer3 /*opt*/, ref string strServerAlt)
		{
			AssertImportant(strServer != null); // Should be at least String.Empty or strNA

			// Create the server name extracted from the URL.
			if (strURL != null)
			{
				if (strServer.IsNA())
					strServer = ServerNameFromURL(strURL);
				else if (strServer2.IsNA())
					strServer2 = ServerNameFromURL(strURL);
			}

			// We now have up to three server strings. Choose the top two.

			if (strServer2 != null)
			{
				if (strServer.IsNA()) // null, empty or "N/A"
					strServer = strServer2;
				else if (!String.Equals(strServer, strServer2, StringComparison.OrdinalIgnoreCase))
					strServer3 = strServer2;
			}

			if (strServer3 != null)
				strServerAlt = strServer3;

			AssertInfo(!strServer.IsNA());
			AssertInfo(!strServerAlt.IsNA());

			if (string.IsNullOrEmpty(strServer))
				strServer = Util.strNA;

			return strServer;
		}

		/*
			Get a server name and alternate name from the URL and IP Address.
			strServer and strServerAlt should enter with a default value.
			All params can be null except strServer (use strNA).
		*/
		public string GetServerNameAndAlt(IPAddress ipAddr /*opt*/, string strURL /*opt*/, string strServer, out string strServerAlt)
		{
			string strServer2 = null;
			string strServer3 = null;

			if (!ipAddr.Empty())
				strServer2 = DNSNameAndAlt(ipAddr, ref strServer3);

			strServerAlt = strNA;
			return GetServerNameAndAlt(strURL, strServer, strServer2, strServer3, ref strServerAlt);
		}

		/*
			Get a server name and alternate name from the URL and IP Address.
			strServer should enter with a default value.
			All string params can be null except strServer (use strNA).
			iDNS and iAddr are 1-based.
		*/
		public string GetServerNameAndAlt(uint iDNS, uint iAddr, string strURL, string strServer, out string strServerAlt)
		{
			string strServer3 = null;

			string strServer2 = DNSNameAndAlt(iDNS, ref strServer3, (iAddr != 0) ? (int)iAddr-1 : 0);

			strServerAlt = strNA;
			return GetServerNameAndAlt(strURL, strServer, strServer2, strServer3, ref strServerAlt);
		}


		/*
			iDNSCache (in,out): the 1-based index of the DNSEntry with the given server name.
			return: the 1-based index of the IP address entry, else 0.
		*/
		public uint AddDNSEntry(string srvName, IPAddress ipAddress, ref uint iDNSCache)
		{
			DNSEntry dnsE = null;
			if (iDNSCache == 0)
			{
				// 127.0.0.1 = "LocalHost"
				if (srvName.Equals(strAddrLocalHost))
					srvName = strLocalHost;

				iDNSCache = IFindDNSEntryByServer(srvName);

				if (iDNSCache == 0)
				{
					iDNSCache = IFindBlankDNSEntryByAddress(ipAddress);

					if (iDNSCache == 0 && srvName.IsNA())
					{
						// No server name. Match to any DNS entry with the given address.
						iDNSCache = IFindDNSEntryByIPAddress1(ipAddress, out uint iAddr1);
						if (iDNSCache != 0)
							return iAddr1;
					}

					if (iDNSCache != 0)
					{
						// Give the no-name record a server name.

						dnsE = DNSEntryFromI(iDNSCache);
						dnsE.strServer = srvName; // could be strNA

						return (uint)(dnsE.rgIpAddr.FindIndex(ipa => ipa.Equals(ipAddress)) + 1);
					}

					dnsE = new DNSEntry(srvName);
					this.Add(dnsE);
					iDNSCache = (uint)this.Count;
				}
			}
			else if (!srvName.IsNA())
			{
				uint iDNSBlank = IFindBlankDNSEntryByAddress(ipAddress);
				if (iDNSBlank != 0)
				{
					// In passing, update the name of the blank DNS record.
					DNSEntry dnsBlank = DNSEntryFromI(iDNSBlank);
					dnsBlank.strServer = srvName;
				}
			}

			if (ipAddress == null) return 0;

			AssertImportant(!ipAddress.IsIPv4MappedToIPv6); // Should normalize such addresses.

			if (dnsE == null)
			{
				dnsE = DNSEntryFromI(iDNSCache);
				AssertImportant(String.Equals(dnsE.strServer, srvName, StringComparison.OrdinalIgnoreCase) || srvName.IsNA());
			}

			// Find or Add the IP Address.

			int iAddr = dnsE.rgIpAddr.FindIndex(ipa => ipa.Equals(ipAddress));
			if (iAddr >= 0)
				return (uint)iAddr + 1;

			dnsE.rgIpAddr.Add(ipAddress);
			return (uint)dnsE.rgIpAddr.Count;
		}


		/*
			IPv4 with port - 1.2.3.4:80 - fail
			IPv6 with port - [1::23]:80 - succeed, ignoring the port
			IPv6 with port - 1::23%4 - succeed, ignoring the %scope
			Strips the %ZoneID suffix.
			Converts IPv4 mapped to IPv6 back to standard IPv4.
		*/
		static public bool TryParseEx(string strAddr, out IPAddress ipAddress)
		{
			int iPct = strAddr.LastIndexOf('%');
			if (iPct >= 0)
			{
				AssertImportant(strAddr[0] != '[');
				strAddr = strAddr[..iPct];
			}

			if (!IPAddress.TryParse(strAddr, out ipAddress))
				return false;

			if (ipAddress.IsIPv4MappedToIPv6)
				ipAddress = ipAddress.MapToIPv4();

			return true;
		}


		static bool TryParseV4WithPort(string strAddr, ref IPAddress ipAddress, ref ushort port)
		{
			// Might be IPv4 with port: 1.2.3.4:80

			int iDot = strAddr.LastIndexOf('.');
			if (iDot <= 0)
				return false;

			int iColon = strAddr.IndexOf(':', StringComparison.Ordinal);
			if (iColon <= iDot)
				return false;

			string[] rgstrAddr = strAddr.Split(':');
			if (rgstrAddr.Count() != 2)
				return false;

			if (!ushort.TryParse(rgstrAddr[1], out port))
				return false;

			return TryParseEx(rgstrAddr[0], out ipAddress);
		}


		static public bool TryParseWithPort(string strAddr, out IPAddress ipAddress, out ushort port)
		{
			port = 0;
			ipAddress = IPAddress.None;

			if (TryParseV4WithPort(strAddr, ref ipAddress, ref port))
				return true;

			if (!TryParseEx(strAddr, out ipAddress))
				return false;

			// Might be IPv6 with port: [1:2:3::4]:80
			if (ipAddress.AddressFamily != AddressFamily.InterNetworkV6)
				return true;

			int iBracket = strAddr.IndexOf("]:", StringComparison.Ordinal);
			if (iBracket > 2 && strAddr.Length > iBracket + 2)
				return ushort.TryParse(strAddr[(iBracket+2)..], out port);

			return true;
		}


		static public bool TryParseIgnorePort(string strAddr, out IPAddress ipAddress)
		{
			// Handle "[ipv6]:port"
			if (strAddr[0] == '[')
			{
				int iBracket = strAddr.IndexOf("]:", StringComparison.Ordinal);
				if (iBracket < 2)
				{
					ipAddress = null;
					return false;
				}

				strAddr = strAddr[1..iBracket];
			}

			if (TryParseEx(strAddr, out ipAddress)) return true;

			ushort port = 0;
			return TryParseV4WithPort(strAddr, ref ipAddress, ref port);
		}


		public uint ParseDNSEntries(string hostName, string addrList, out uint iAddr)
		{
			string dnsNameAlt = null;
			uint iDNSCache = 0;

			iAddr = 0;

			AssertInfo(!String.IsNullOrWhiteSpace(hostName));

			if (String.IsNullOrWhiteSpace(hostName))
				hostName = strNA; // "N/A"

			if (String.IsNullOrWhiteSpace(addrList))
			{
				// Create an entry with a server name and no addresses.
				// Either addresses get added later,
				// or we have an indication that we tried to contact a non-existent server.

				if (!hostName.IsNA())
					iAddr = AddDNSEntry(hostName, null, ref iDNSCache);

				return iDNSCache;
			}

			// Note that ETW truncates the addrList string at 1024 chars (~44 aggregated IPv6 Addr strings).

			foreach (string strDNS in addrList.Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				// dnsName is of several forms:
				//	"1.2.3.4"
				//	"1.2.3.4:80"
				//	"::ffff:1.2.3.4"
				//	"[::ffff:1.2.3.4]:80
				//  "1:2:3:4:5:6:7:8%9"
				//	"type: 5 foo.bar.com"

				int iSpace = strDNS.LastIndexOf(' ');
				string strAddr = (iSpace >= 0) ? strDNS[(iSpace + 1)..] : strDNS;

				if (TryParseIgnorePort(strAddr, out IPAddress ipAddress))
				{
					uint iAddrT = AddDNSEntry(hostName, ipAddress, ref iDNSCache);
					// Return the index of the first address parsed.
					if (iAddr == 0)
						iAddr = iAddrT;
				}
				else if (strAddr.LastIndexOf('.') >= 0) // reality check
				{
					// This is not an address. It is almost surely an alternate server name.  Remember the last one.
					dnsNameAlt = strAddr;
				}
			}

			if (dnsNameAlt != null && iDNSCache != 0)
			{
				DNSEntry dnsE = DNSEntryFromI(iDNSCache);

				dnsE.strNameAlt ??= dnsNameAlt;
			}

			return iDNSCache;
		} // ParseDNSEntries

		public uint ParseDNSEntries(string hostName, string addrList)
		{
			return ParseDNSEntries(hostName, addrList, out uint iAddr);
		}


		/*
			Get the server name(s) and build a SockAddress. Create a new DNS entry.
			Return the primary server name.
		*/
		public string ConnectNameResolution(in IGenericEvent evt, string strServer)
		{
			UInt32 addrCount = evt.GetUInt32("AddressCount");
			if (addrCount == 0) return strServer;

			UInt32 addrLength = evt.GetUInt32("SockaddrLength");

			UInt32 addrSize = addrLength / addrCount;
			AssertCritical(addrSize * addrCount == addrLength); // no rounding

			// family + port + v4 address
			if (addrSize < 8) return strServer;

			string strServerName = evt.GetString("FQDN");
			string strCanonicalName = evt.GetString("CanonicalName");

			if (!string.IsNullOrWhiteSpace(strServer))
			{
				// The "canonical name" is added below, so use the server name from the URL.
				if (strServerName.Equals(strCanonicalName, StringComparison.OrdinalIgnoreCase))
					strServerName = strServer;
			}
			else
			{
				strServer = strServerName;
			}

			uint iDNS = 0;
			IReadOnlyList<byte> rgbSockAddr = evt.GetBinary("SockAddr");

			// Transfer the M X N bytes into N SocketAddress objects of size M.

			int ibAddr = 0;
			for (uint iSock = 0; iSock < addrCount; ++iSock)
			{
				SocketAddress sock = new SocketAddress((AddressFamily)rgbSockAddr[ibAddr], (int)addrSize);

				for (int ib = 0; ib < addrSize; ++ib)
					sock[ib] = rgbSockAddr[ibAddr++];

				System.Net.IPEndPoint ipep = NewEndPoint(sock);
				uint iAddr = this.AddDNSEntry(strServerName, ipep.Address, ref iDNS);
				AssertImportant(iAddr > 0); // 1-based
			}

			if (iDNS != 0)
			{
				DNSClient.DNSEntry dnsEntry = this.DNSEntryFromI(iDNS);
				if (dnsEntry.strNameAlt == null)
				{
					if (!String.Equals(dnsEntry.strServer, strCanonicalName, StringComparison.OrdinalIgnoreCase))
						dnsEntry.strNameAlt = strCanonicalName;
				}
				else
				{
					AssertInfo(String.Equals(dnsEntry.strNameAlt, strCanonicalName, StringComparison.OrdinalIgnoreCase));
				}
			}

			return strServer;
		} // ConnectNameResolution


		/*
			Parse the given Address/Port string, and return the 1-based iDNS, 1-based iAddr, and Port.
		*/
		public uint ParseAddressPortString(string strAddress, out uint iAddrOut, out uint portOut)
		{
			iAddrOut = 0;
			portOut = 0;

			if (strAddress == null) return 0;

			// Indicate the DNS entry.

			if (!DNSTable.TryParseWithPort(strAddress, out IPAddress addrRemote, out ushort port)) return 0;

			// iDNS is 1-based; iAddr is 0-based.
			uint iDNS = this.IFindDNSEntryByIPAddress1(addrRemote, out iAddrOut);

			// Rather than storing the address, store a DNS index and bits indicating which IP address.
			// Note that a single DNS entry may store a server name and multiple IP addresses.

			AssertInfo(iDNS != 0);

			if (iDNS == 0)
			{
				// If this event is near the beginning of the trace, there may be no DNS info.
				// We need to put together something so that the Socket->TCB connection can be made via CorrelateByAddress.

				iAddrOut = this.AddDNSEntry(strNA, addrRemote, ref iDNS);
			}

			portOut = port;

			return iDNS;
		} // ParseAddressPortString


		public static readonly Guid guid = new Guid("{1c95126e-7eea-49a9-a3fe-a378b03ddb4d}"); // Microsoft-Windows-DNS-Client

		public void Dispatch(in IGenericEvent evtGeneric)
		{
			const uint DNSQueryExComplete = 3008;
			const uint DNSNetworkQuery = 3009;
			const uint DNSCacheLookupExit = 3018;
			const uint QueryType1 = 1;
			const uint QueryType28 = 28;
			const uint S_OK = 0;

			if (evtGeneric.Id == DNSQueryExComplete || evtGeneric.Id == DNSCacheLookupExit)
			{
				// DNS_ERROR_RECORD_DOES_NOT_EXIST = 0x25e5
				AssertInfo(evtGeneric.GetUInt32((evtGeneric.Id == DNSQueryExComplete) ? "QueryStatus" : "Status") == S_OK);

				switch (evtGeneric.GetUInt32("QueryType"))
				{
				case QueryType28:
					// QueryResults == L"type: 5 server.com;...;aaaa:bbbb:cccc::hhhh;...;::ffff:ip.address;" OR some combination in that order
					// Let the parser decide which ones are valid addresses.
					// fall through...
				case QueryType1:
					// QueryResults == L"10.1.2.3;"
					ParseDNSEntries(evtGeneric.GetString("QueryName"), evtGeneric.GetString("QueryResults"));
					break;
				}
			}
			else if (evtGeneric.Id == DNSNetworkQuery)
			{
				// QueryResults == L"10.1.2.3;"
				ParseDNSEntries(evtGeneric.GetString("QueryName"), evtGeneric.GetString("DNSServerAddress"));
			}
		} // Dispatch
	} // DNSTable
} // NetBlameCustomDataSource.DNSClient
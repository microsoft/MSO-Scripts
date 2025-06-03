// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Net;

using Microsoft.Windows.EventTracing.Events;


namespace NetBlameCustomDataSource.WinsockNameRes
{
	public class WinsockNameResolution
	{
		public static readonly Guid guid = new Guid("{55404e71-4db9-4deb-a5f5-8f86e46dde56}"); // Microsoft-Windows-Winsock-NameResolution

		readonly AllTables allTables;

		public WinsockNameResolution(in AllTables _allTables) { this.allTables = _allTables; }

		public void Dispatch(in IGenericEvent evtGeneric)
		{
			const int GetAddrInfo_Stop = 1001;
			const int GetAddrInfoX_Stop = 1004;
			const uint S_OK = 0;

			switch (evtGeneric.Id)
			{
			case GetAddrInfo_Stop:
			case GetAddrInfoX_Stop:
				if (evtGeneric.GetUInt32("Status") == S_OK)
				{
					// If the server name is NOT an address, add it to the DNS table.
					string strNodeName = evtGeneric.GetString("NodeName");
					if (String.IsNullOrWhiteSpace(strNodeName) || !IPAddress.TryParse(strNodeName, out IPAddress ipAddress))
					{
						this.allTables.dnsTable.ParseDNSEntries(strNodeName, evtGeneric.GetString("Result"));
					}
				}
				break;
			}
		}
	}
}
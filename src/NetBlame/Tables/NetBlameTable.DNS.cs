// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

#if AUX_TABLES

using System;
using System.Net;

using Microsoft.Performance.SDK.Processing;

using NetBlameCustomDataSource.DNSClient;


namespace NetBlameCustomDataSource.Tables
{
	[Table]
	public sealed class NetBlameTableDNS : NetBlameTableBase
	{
		public NetBlameTableDNS(PendingSources sources, AllTables tables, IApplicationEnvironment environ) : base(sources, tables, environ) {}

		public static TableDescriptor TableDescriptor => new TableDescriptor(
			new Guid("53F8C147-47C0-4C5F-B620-F8E3C5DC2405"), // The GUID must be unique across all tables.
			"NetBlame DNS Table",                             // The Table must have a name.
			"Office Network Analyzer - DNS Activity",         // The Table must have a description.
			"Network",                                        // Optional category for grouping different types of tables in WPA UI.
			false,                                            // Not Metadata
			TableLayoutStyle.Table                            // Table Only - No Chart
		);


		// Declare columns here. You can do this using the ColumnConfiguration class.
		// It is possible to declaratively describe the table configuration as well. Please refer to our Advanced Topics Wiki page for more information.
		//
		// The Column metadata describes each column in the table.
		// Each column must have a unique GUID and a unique name. The GUID must be unique globally; the name only unique within the table.

		static class Columns
		{
			public static readonly ColumnConfiguration colServer = DeclareColumn
			(
				"Server",
				"Base Server Name",
				width: 180,
				visible: true
			);

			public static readonly ColumnConfiguration colAltServer =
			DeclareColumn
			(
				"Alt Server",
				"Alternate DNS Server Name",
				width: 180,
				visible: true
			);

			public static readonly ColumnConfiguration colCount =
			DeclareColumn
			(
				"Count",
				"Count of Server / IP Address",
				align: TextAlignment.Right,
				mode: AggregationMode.Sum,
				width: 58,
				visible: true
			);

			public static readonly ColumnConfiguration colAddr =
			DeclareColumn
			(
				"IP Address",
				"Remote IP Address",
				width: 128,
				visible: true
			);

			public static readonly ColumnConfiguration colFamily =
			DeclareColumn
			(
				"Family",
				"Address Family:\r\n  2 = IPv4\r\n23 = IPv6\r\n34 = HYPERV",
				width: 50,
				align: TextAlignment.Right,
				visible: false
			);

			public static readonly ColumnConfiguration colIndex =
			DeclareColumn
			(
				"Index",
				"1-based Internal Indices - DNS:Address",
				width: 46,
				align: TextAlignment.Center,
				visible: false
			);

			public static readonly ColumnConfiguration colGeoLocation =
			DeclareColumn
			(
				"GeoLocation",
				GeoLocation.Attribution,
				width: 180,
				visible: false
			);
/*
			public static readonly ColumnConfiguration colNAME =
			DeclareColumn
			(
				"NAME",
				"DESC",
				width: 180,
				visible: true
			);
*/
		} // Columns

		// Generators for use in Projections
		// Static functions are more efficient than non-static, inline lambdas.
		// None of these should return null.
		static class Generators
		{
			public static DNSEntry DnsEntry(DNSIndex dnsiObj, DNSTable dnsTable) => dnsTable.DNSEntryFromI(dnsiObj.iDNS);

			public static string Server(DNSEntry dnsObj) => dnsObj.strServer ?? String.Empty;

			public static string AltServer(DNSEntry dnsObj) => dnsObj.strNameAlt ?? String.Empty;

			public static IPAddress IPAddress(DNSIndex dnsiObj, DNSEntry dnsObj) => (dnsiObj.iAddr != 0 ? dnsObj?.rgIpAddr[dnsiObj.iAddr - 1] : null);

			public static string IPAddressStr(DNSIndex dnsiObj, DNSEntry dnsObj) => IPAddress(dnsiObj, dnsObj).ToGraphable(); // never null

			public static int Family(DNSIndex dnsiObj, DNSEntry dnsObj) => (int?)IPAddress(dnsiObj, dnsObj)?.AddressFamily ?? 0;

			public static string Index(DNSIndex dnsiObj) => String.Format("{0:D3}:{1:D3}", dnsiObj.iDNS, dnsiObj.iAddr);

			public static string GeoLoc(DNSIndex dnsiObj, DNSEntry dnsObj) => GeoLocation.GetGeoLocation(IPAddress(dnsiObj, dnsObj));
		} // Generators

		public override void Build(ITableBuilder tableBuilder)
		{
			// Implement your columns here.
			// Columns are implemented via Projections, which are simply functions that map a row index to a data point.

			// Process the data here.

			// Create projection for each column by composing the base projection with another projection that maps to the data point as needed.

			var tableBase = this.Tables?.dnsIndexTable;

			if (tableBase == null) return;

			// int -> DNSIndex
			var dnsiBaseProjector = Projection.Index(tableBase);

			// int -> constant
			var dnsCountProjector = Projection.Constant(1);

			// int -> DNSTable
			var dnsTableProjector = Projection.Constant(this.Tables.dnsTable);

			// int -> DNSEntry
			var dnsiEntryProjector = Projection.Project(dnsiBaseProjector, dnsTableProjector, Generators.DnsEntry);

			// int -> DNSEntry -> string: Server, AltServer, Index, GeoLocation
			var dnsiServerProjector = Projection.Project(dnsiEntryProjector, Generators.Server);
			var dnsiAltServerProjector = Projection.Project(dnsiEntryProjector, Generators.AltServer);
			var dnsiIndexProjector = Projection.Project(dnsiBaseProjector, Generators.Index);
			var dnsiGeoProjector = Projection.Project(dnsiBaseProjector, dnsiEntryProjector, Generators.GeoLoc);
			var dnsiGeoCacheProjector = Projection.CacheOnFirstUse(tableBase.Count, dnsiGeoProjector);

			// int -> IPAddress
			var dnsiAddressProjector = Projection.Project(dnsiBaseProjector, dnsiEntryProjector, Generators.IPAddressStr);

			// int -> int / AddressFamily
			var dnsiFamilyProjector = Projection.Project(dnsiBaseProjector, dnsiEntryProjector, Generators.Family);

			// Table Configurations describe how your table should be presented to the user:
			// the columns to show, what order to show them, which columns to aggregate, and which columns to graph.
			// You may provide a number of columns in your table, but only want to show a subset of them by default so as not to overwhelm the user.
			// The user can still open the table properties in WPA to turn on or off columns.
			// The table configuration class also exposes four (4) columns that WPA explicitly recognizes: Pivot Column, Graph Column, Left Freeze Column, Right Freeze Column
			// For more information about what these columns do, go to "Advanced Topics" -> "Table Configuration" in our Wiki. Link can be found in README.md

			var config = new TableConfiguration("DNS Info")
			{
				Columns = new[]
				{
					Columns.colServer,
					TableConfiguration.PivotColumn, /*------------*/
					Columns.colCount,
					Columns.colAltServer,
					Columns.colAddr,
					Columns.colFamily,
#if DEBUG
					Columns.colGeoLocation,
#endif // DEBUG
					Columns.colIndex,
					TableConfiguration.RightFreezeColumn, /*------*/
					TableConfiguration.GraphColumn, /*------------*/
				}
			};

			//  Use the table builder to build the table.
			//  Add and set table configuration if applicable.
			//  Then set the row count and then add the columns using AddColumn.

			tableBuilder
				.AddTableConfiguration(config)
			//  .AddTableConfiguration(config2)
				.SetDefaultTableConfiguration(config)
				.SetRowCount(tableBase.Count)
				.AddColumn(Columns.colServer, dnsiServerProjector)
				.AddColumn(Columns.colAltServer, dnsiAltServerProjector)
				.AddColumn(Columns.colCount, dnsCountProjector)
				.AddColumn(Columns.colAddr, dnsiAddressProjector)
				.AddColumn(Columns.colFamily, dnsiFamilyProjector)
				.AddColumn(Columns.colIndex, dnsiIndexProjector)
				.AddColumn(Columns.colGeoLocation, dnsiGeoCacheProjector);
				;

			// this.Sources.Release();
		} // Build
	} // NetBlameTable
} // NetBlameCustomDataSource.Tables

#endif // AUX_TABLES

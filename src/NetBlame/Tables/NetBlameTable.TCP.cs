// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

#if AUX_TABLES

using System;

using Microsoft.Performance.SDK.Processing;

using NetBlameCustomDataSource.DNSClient;
using NetBlameCustomDataSource.TcpIp;

using static NetBlameCustomDataSource.Util;

using QWord = System.UInt64;


namespace NetBlameCustomDataSource.Tables
{
	[Table]
	public sealed class NetBlameTableTCB : NetBlameTableBase
	{
		public NetBlameTableTCB(PendingSources sources, AllTables tables, IApplicationEnvironment environ) : base(sources, tables, environ) {}

		public static TableDescriptor TableDescriptor => new TableDescriptor(
			new Guid("7A86A168-714B-4C05-A527-AF561D10DAE5"), // The GUID must be unique across all tables.
			"NetBlame TCB Table",                             // The Table must have a name.
			"Office Network Analyzer - Tx Control Blocks",    // The Table must have a description.
			"Network"                                         // Optional category for grouping different types of tables in WPA UI.
		);


		// Declare columns here. You can do this using the ColumnConfiguration class.
		// It is possible to declaratively describe the table configuration as well. Please refer to our Advanced Topics Wiki page for more information.
		//
		// The Column metadata describes each column in the table.
		// Each column must have a unique GUID and a unique name. The GUID must be unique globally; the name only unique within the table.

		static class Columns
		{
			// These are created via ColumnsCommon(), below:
			// colProcessName, colProcess, colThread, colDuration, colOpenTime, colCloseTime

			public static readonly ColumnConfiguration colServer =
			DeclareColumn
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

			public static readonly ColumnConfiguration colProtocol =
			DeclareColumn
			(
				"Protocol",
				"WinHTTP, WinINet, WinSock, LDAP",
				width: 70,
				visible: true
			);

			public static readonly ColumnConfiguration colIPProtocol =
			DeclareColumn
			(
				"IP Protocol",
				"TCP, UDP",
				width: 72,
				visible: true
			);

			public static readonly ColumnConfiguration colPost =
			DeclareColumn
			(
				"Post (B)",
				"Bytes Posted",
				mode: AggregationMode.Sum,
				width: 70,
				align: TextAlignment.Right,
				format: ColumnFormats.NumberFormat,
				visible: true
			);

			public static readonly ColumnConfiguration colSend =
			DeclareColumn
			(
				"Send (B)",
				"Bytes Sent",
				mode: AggregationMode.Sum,
				width: 70,
				align: TextAlignment.Right,
				format: ColumnFormats.NumberFormat,
				visible: true
			);

			public static readonly ColumnConfiguration colRecv =
			DeclareColumn
			(
				"Recv (B)",
				"Bytes Received",
				mode: AggregationMode.Sum,
				width: 70,
				align: TextAlignment.Right,
				format: ColumnFormats.NumberFormat,
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
		
			public static readonly ColumnConfiguration colPort =
			DeclareColumn
			(
				"Port",
				"80=http, 443=https, etc.",
				width: 38,
				align: TextAlignment.Center,
				visible: true
			);

			public static readonly ColumnConfiguration colSocket =
			DeclareColumn
			(
				"Socket ID",
				"TCP / WinSock / WinINet Socket ID",
				width: 62,
				align: TextAlignment.Center,
				visible: false
			);

			public static readonly ColumnConfiguration colTCB =
			DeclareColumn
			(
				"TCB/UDP ID",
				"Transfer Control Block ID\r\nor UDP Endpoint ID (reusable)",
				width: 126,
				format: ColumnFormats.HexFormat,
				visible: false
			);

			public static readonly ColumnConfiguration colStatus =
			DeclareColumn
			(
				"Status",
				"Final Status of the Transfer Control Block",
				width: 106,
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
			// These generators are in GeneratorCommon<>:
			// ProcessData, ProcName, ProcFullName, Thread, OpenTime, CloseTime, Duration

			public static string Server(TcbRecord tcbObj, DNSTable dnsTable) => dnsTable.DNSEntryFromI(tcbObj.iDNS)?.strServer ?? strNA;

			public static string AltServer(TcbRecord tcbObj, DNSTable dnsTable) => dnsTable.DNSEntryFromI(tcbObj.iDNS)?.strNameAlt ?? String.Empty;

			public static uint Socket(TcbRecord tcbObj) => tcbObj.socket;

			public static string Protocol(TcbRecord tcbObj) => Prominent((Protocol)tcbObj.grbitProtocol).ToString();

			public static string IPProtocol(TcbRecord tcbObj) => tcbObj.fUDP ? "UDP" : "TCP";

			public static uint Port(TcbRecord tcbObj) => tcbObj.addrRemote.PortGraphable();

			public static string IPAddress(TcbRecord tcbObj) => tcbObj.addrRemote.AddrGraphable();

			public static uint Post(TcbRecord tcbObj) => tcbObj.cbPost;

			public static uint Send(TcbRecord tcbObj) => tcbObj.cbSend;

			public static uint Recv(TcbRecord tcbObj) => tcbObj.cbRecv;

			public static QWord TCB(TcbRecord tcbObj) => tcbObj.tcb;

			public static string Status(TcbRecord tcbObj) => tcbObj.status.ToString(); // pinned enum name string
		} // Generators


		public override void Build(ITableBuilder tableBuilder)
		{
			// Implement your columns here.
			// Columns are implemented via Projections, which are simply functions that map a row index to a data point.
			// Create projection for each column by composing the base projection with another projection that maps to the data point as needed.

			var tableBase = this.Tables?.tcpTable;

			if (tableBase == null) return;

			// int -> TcbRecord
			var tcbBaseProjector = Projection.Index(tableBase);

			// int -> TCBTable
			var dnsTableProjector = Projection.Constant(this.Tables.dnsTable);

			// int -> TimestampUI (LastEventTime)
			var tcbEndTimeProjector = Projection.Constant(this.Sources.traceMetadata.LastEventTime.ToGraphable());

			// int -> DNSEntry -> string: Server, AltServer
			var tcbServerProjector = Projection.Project(tcbBaseProjector, dnsTableProjector, Generators.Server);
			var tcbAltServerProjector = Projection.Project(tcbBaseProjector, dnsTableProjector, Generators.AltServer);

			// int -> string: Protocol
			var tcbProtocolProjector = Projection.Project(tcbBaseProjector, Generators.Protocol);
			var tcbIPProtocolProjector = Projection.Project(tcbBaseProjector, Generators.IPProtocol);
			var tcbStatusProjector = Projection.Project(tcbBaseProjector, Generators.Status);

			// int -> uint: Send, Recv, Port, Socket
			var tcbPostProjector = Projection.Project(tcbBaseProjector, Generators.Post);
			var tcbSendProjector = Projection.Project(tcbBaseProjector, Generators.Send);
			var tcbRecvProjector = Projection.Project(tcbBaseProjector, Generators.Recv);
			var tcbPortProjector = Projection.Project(tcbBaseProjector, Generators.Port);
			var tcbSocketProjector = Projection.Project(tcbBaseProjector, Generators.Socket);

			// int -> string / IPAddress
			var tcbAddressProjector = Projection.Project(tcbBaseProjector, Generators.IPAddress);

			// int -> QWord
			var tcbTCBProjector = Projection.Project(tcbBaseProjector, Generators.TCB);

			// standard projectors: process, start/end time, duration
			var commonProjectors = new ProjectorCommon<TcbRecord>(this.Sources, in tcbBaseProjector, tableBase.Count);

			// Table Configurations describe how your table should be presented to the user:
			// the columns to show, what order to show them, which columns to aggregate, and which columns to graph.
			// You may provide a number of columns in your table, but only want to show a subset of them by default so as not to overwhelm the user.
			// The user can still open the table properties in WPA to turn on or off columns.
			// The table configuration class also exposes four (4) columns that WPA explicitly recognizes: Pivot Column, Graph Column, Left Freeze Column, Right Freeze Column
			// For more information about what these columns do, go to "Advanced Topics" -> "Table Configuration" in our Wiki. Link can be found in README.md

			// Common columns: colProcessName, colProcess, colStack, colDuration, colOpenTime, colCloseTime
			ColumnsCommon commonColumns = new ColumnsCommon();

			var config = new TableConfiguration("TcpIp Info")
			{
				Columns = new[]
				{
					commonColumns.colProcessName,
					commonColumns.colProcess,
					Columns.colServer,
					TableConfiguration.PivotColumn, /*------------*/
					commonColumns.colCount,
					commonColumns.colThread,
					Columns.colAltServer,
					Columns.colProtocol,
					Columns.colIPProtocol,
					Columns.colPost,
					Columns.colSend,
					Columns.colRecv,
					Columns.colAddr,
					Columns.colPort,
					Columns.colSocket,
					Columns.colTCB,
					Columns.colStatus,
					TableConfiguration.RightFreezeColumn, /*------*/
					commonColumns.colDuration,
					TableConfiguration.GraphColumn, /*------------*/
					commonColumns.colOpenTime,
					commonColumns.colCloseTime,
				}
			};
#if !DEBUG
/*
			When open/close timestamps are given this meta-data, zeros get eliminated.
*/
			// Advanced Settings / Graph Configuration
			config.AddColumnRole(ColumnRole.StartTime, commonColumns.colOpenTime.Metadata.Guid);
			config.AddColumnRole(ColumnRole.EndTime, commonColumns.colCloseTime.Metadata.Guid);
#endif // !DEBUG
			config.AddColumnRole(ColumnRole.Duration, commonColumns.colDuration.Metadata.Guid);

			//  Use the table builder to build the table.
			//  Add and set table configuration if applicable.
			//  Then set the row count and then add the columns using AddColumn.

			tableBuilder
				.AddTableConfiguration(config)
			//  .AddTableConfiguration(config2)
				.SetDefaultTableConfiguration(config)
				.SetRowCount(tableBase.Count)
				.AddCommonColumns(commonColumns, commonProjectors, null) // Process, Thread, Duration, Open/CloseTime
				.AddColumn(Columns.colServer, tcbServerProjector)
				.AddColumn(Columns.colAltServer, tcbAltServerProjector)
				.AddColumn(Columns.colProtocol, tcbProtocolProjector)
				.AddColumn(Columns.colIPProtocol, tcbIPProtocolProjector)
				.AddColumn(Columns.colPost, tcbPostProjector)
				.AddColumn(Columns.colSend, tcbSendProjector)
				.AddColumn(Columns.colRecv, tcbRecvProjector)
				.AddColumn(Columns.colAddr, tcbAddressProjector)
				.AddColumn(Columns.colPort, tcbPortProjector)
				.AddColumn(Columns.colSocket, tcbSocketProjector)
				.AddColumn(Columns.colTCB, tcbTCBProjector)
				.AddColumn(Columns.colStatus, tcbStatusProjector)
				;

			// this.Sources.Release();
		} // Build
	} // NetBlameTable
} // NetBlameCustomDataSource.Tables

#endif // AUX_TABLES
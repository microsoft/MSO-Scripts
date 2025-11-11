// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

#if AUX_TABLES

using System;

using Microsoft.Performance.SDK.Processing;

using NetBlameCustomDataSource.WinsockAFD;

using static NetBlameCustomDataSource.Util;

using QWord = System.UInt64;


namespace NetBlameCustomDataSource.Tables
{
	[Table]
	public sealed class NetBlameTableWSock : NetBlameTableBase
	{
		public NetBlameTableWSock(PendingSources sources, AllTables tables, IApplicationEnvironment environ) : base(sources, tables, environ) {}

		public static TableDescriptor TableDescriptor => new TableDescriptor(
			new Guid("CD12EC76-3D96-4F62-8F6C-2398246FE020"), // The GUID must be unique across all tables.
			"NetBlame WinSock Table",                         // The Table must have a name.
			"Office Network Analyzer - WinSock",              // The Table must have a description.
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
			// colProcessName, colProcess, colThread, colStack, colDuration, colOpenTime, colCloseTime

			public static readonly ColumnConfiguration colServer =
			DeclareColumn
			(
				"Server",
				"Base Server Name",
				width: 180,
				visible: true
			);

			public static readonly ColumnConfiguration colTidClose =
			DeclareColumn
			(
				"Close Thread",
				"Thread ID of Socket Close",
				align: TextAlignment.Right,
				width: 82,
				visible: false
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
				width: 148,
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

			public static readonly ColumnConfiguration colProtocol =
			DeclareColumn
			(
				"Protocol",
				"WinSock, LDAP",
				width: 70,
				visible: true
			);

			public static readonly ColumnConfiguration colIPProto =
			DeclareColumn
			(
				"IP Protocol",
				"IPPROTO Header: TCP, UDP, ...",
				width: 72,
				visible: false
			);

			public static readonly ColumnConfiguration colSockType =
			DeclareColumn
			(
				"Socket Type",
				"Type: Stream, Datagram, ...",
				width: 72,
				visible: false
			);

			public static readonly ColumnConfiguration colEndpoint =
			DeclareColumn
			(
				"Endpoint",
				"WinSock Endpoint ID (reusable)",
				format: ColumnFormats.HexFormat,
				width: 126,
				visible: false
			);

			public static readonly ColumnConfiguration colTCB =
			DeclareColumn
			(
				"TCB/UDP ID",
				"Transfer Control Block ID\r\nor UDP Endpoint ID (reusable)",
				format: ColumnFormats.HexFormat,
				width: 126,
				visible: false
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
			// ProcessData, ProcName, ProcFullName, Thread, OpenStack, OpenTime, CloseTime, Duration

			public static string Server(Connection cxnObj, DNSClient.DNSTable dnsTable) => dnsTable.DNSEntryFromI(cxnObj.iDNS)?.strServer ?? strNA;

			public static string Protocol(Connection cxnObj) => Prominent((Protocol)cxnObj.grbitType).ToString(); // pinned enum names

			public static string IPProto(Connection cxnObj) => cxnObj.ipProtocol.ToString(); // pinned enum names

			public static string SockType(Connection cxnObj) => cxnObj.socktype.ToString(); // pinned enum names

			public static uint Port(Connection cxnObj) => cxnObj.addrRemote.PortGraphable();

			public static string IPAddress(Connection cxnObj) => cxnObj.addrRemote.AddrGraphable();

			public static uint Send(Connection cxnObj) => cxnObj.cbSend;

			public static uint Recv(Connection cxnObj) => cxnObj.cbRecv;

			public static QWord Endpoint(Connection cxnObj) => cxnObj.qwEndpoint;

			public static uint Socket(Connection cxnObj) => cxnObj.socket;

			public static string TidClose(Connection cxnObj) => StringFromInt(cxnObj.tidClose);

			public static QWord TCB(Connection cxnObj, TcpIp.TcpTable tcpTable) => tcpTable.TcbrFromI(cxnObj.iTCB)?.tcb ?? 0;
		} // Generators


		public override void Build(ITableBuilder tableBuilder)
		{
			// Implement your columns here.
			// Columns are implemented via Projections, which are simply functions that map a row index to a data point.
			// Create projection for each column by composing the base projection with another projection that maps to the data point as needed.

			var tableBase = this.Tables?.wsTable;

			if (tableBase == null) return;

			// int -> Connection
			var wsBaseProjector = Projection.Index(tableBase);

			// int -> DNSTable
			var wsDNSTableProjector = Projection.Constant(this.Tables.dnsTable);
			var wsTcpTableProjector = Projection.Constant(this.Tables.tcpTable);

			// int -> numeric string: TID Create/Close
			var wsTidCloseProjector = Projection.Project(wsBaseProjector, Generators.TidClose);

			// int -> string: ws, Server, Protocol, IPProto, SockType
			var wsServerProjector = Projection.Project(wsBaseProjector, wsDNSTableProjector, Generators.Server);
			var wsProtocolProjector = Projection.Project(wsBaseProjector, Generators.Protocol);
			var wsIPProtoProjector = Projection.Project(wsBaseProjector, Generators.IPProto);
			var wsSockTypeProjector = Projection.Project(wsBaseProjector, Generators.SockType);

			// int -> uint: Send, Recv, Port, Socket
			var wsSendProjector = Projection.Project(wsBaseProjector, Generators.Send);
			var wsRecvProjector = Projection.Project(wsBaseProjector, Generators.Recv);
			var wsPortProjector = Projection.Project(wsBaseProjector, Generators.Port);
			var wsSocketProjector = Projection.Project(wsBaseProjector, Generators.Socket);

			// int -> IPAddress
			var wsAddressProjector = Projection.Project(wsBaseProjector, Generators.IPAddress);

			// int -> QWord
			var wsEndpointProjector = Projection.Project(wsBaseProjector, Generators.Endpoint);
			var wsITCBProjector = Projection.Project(wsBaseProjector, wsTcpTableProjector, Generators.TCB);

			// int -> common projectors: process, thread, stack, start/end time, duration
			var commonProjectors = new ProjectorCommon<Connection>(this.Sources, in wsBaseProjector, tableBase.Count);

			// Table Configurations describe how your table should be presented to the user:
			// the columns to show, what order to show them, which columns to aggregate, and which columns to graph.
			// You may provide a number of columns in your table, but only want to show a subset of them by default so as not to overwhelm the user.
			// The user can still open the table properties in WPA to turn on or off columns.
			// The table configuration class also exposes four (4) columns that WPA explicitly recognizes: Pivot Column, Graph Column, Left Freeze Column, Right Freeze Column
			// For more information about what these columns do, go to "Advanced Topics" -> "Table Configuration" in our Wiki. Link can be found in README.md

			// Common columns: colProcessName, colProcess, colStack, colDuration, colOpenTime, colCloseTime
			ColumnsCommon commonColumns = new ColumnsCommon();

			var config = new TableConfiguration("WinSock Info")
			{
				Columns = new[]
				{
					commonColumns.colProcessName,
					commonColumns.colProcess,
					Columns.colServer,
					commonColumns.colStack,
					TableConfiguration.PivotColumn, /*------------*/
					commonColumns.colCount,
					Columns.colProtocol,
					Columns.colIPProto,
					Columns.colSockType,
					Columns.colSend,
					Columns.colRecv,
					Columns.colAddr,
					Columns.colPort,
					Columns.colSocket,
					commonColumns.colThread,
					Columns.colTidClose,
					Columns.colEndpoint,
					Columns.colTCB,
#if DEBUG
					commonColumns.colLinkIndex,
					commonColumns.colLinkType,
#endif // DEBUG
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
				.AddCommonColumns(commonColumns, commonProjectors, Sources.stackAccessProvider) // Process, Thread, Stack, Duration, Open/CloseTime
				.AddColumn(Columns.colServer, wsServerProjector)
				.AddColumn(Columns.colTidClose, wsTidCloseProjector)
				.AddColumn(Columns.colProtocol, wsProtocolProjector)
				.AddColumn(Columns.colIPProto, wsIPProtoProjector)
				.AddColumn(Columns.colSockType, wsSockTypeProjector)
				.AddColumn(Columns.colSend, wsSendProjector)
				.AddColumn(Columns.colRecv, wsRecvProjector)
				.AddColumn(Columns.colAddr, wsAddressProjector)
				.AddColumn(Columns.colPort, wsPortProjector)
				.AddColumn(Columns.colSocket, wsSocketProjector)
				.AddColumn(Columns.colEndpoint, wsEndpointProjector)
				.AddColumn(Columns.colTCB, wsITCBProjector)
				;

			// this.Sources.Release();
		} // Build
	} // NetBlameTable
} // NetBlameCustomDataSource.Tables

#endif // AUX_TABLES
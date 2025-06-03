// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

#if AUX_TABLES

using System;

using Microsoft.Performance.SDK.Processing;

using NetBlameCustomDataSource.WinINet;

using QWord = System.UInt64;


namespace NetBlameCustomDataSource.Tables
{
	[Table]
	public sealed class NetBlameTableWinINet : NetBlameTableBase
	{
		public NetBlameTableWinINet(PendingSources sources, AllTables tables, IApplicationEnvironment environ) : base(sources, tables, environ) {}

		public static TableDescriptor TableDescriptor => new TableDescriptor(
			new Guid("182DDCAF-BFBB-47B7-8E2A-AF3A36B14912"), // The GUID must be unique across all tables.
			"NetBlame WinINet Table",                         // The Table must have a name.
			"Office Network Analyzer - WinINet",              // The Table must have a description.
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

			public static readonly ColumnConfiguration colServerAlt =
			DeclareColumn
			(
				"Alt DNS Name",
				"Alternate Server DNS Name",
				width: 180,
				visible: false
			);

			public static readonly ColumnConfiguration colServerPort =
			DeclareColumn
			(
				"Server Port",
				"Original Port for the Server Connection",
				width: 70,
				align: TextAlignment.Center,
				visible: false
			);

			public static readonly ColumnConfiguration colMethod =
			DeclareColumn
			(
				"Method",
				"HTTP Method",
				width: 60,
				visible: true
			);

			public static readonly ColumnConfiguration colUrlPath =
			DeclareColumn
			(
				"URL",
				"Full Url Path",
				width: 500,
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
				width: 110,
				visible: true
			);

			public static readonly ColumnConfiguration colPort =
			DeclareColumn
			(
				"Port",
				"Remote IP Address Port:\r\n80=http, 443=https, etc.",
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

			public static readonly ColumnConfiguration colConnect =
			DeclareColumn
			(
				"Connect",
				"Connect ID (reusable)",
				width: 126,
				format: ColumnFormats.HexFormat,
				visible: false
			);

			public static readonly ColumnConfiguration colContext =
			DeclareColumn
			(
				"Context",
				"Context ID (reusable)",
				width: 126,
				format: ColumnFormats.HexFormat,
				visible: false
			);

			public static readonly ColumnConfiguration colRequest =
			DeclareColumn
			(
				"Request",
				"Request ID (reusable)",
				width: 126,
				format: ColumnFormats.HexFormat,
				visible: false
			);

			public static readonly ColumnConfiguration colTCB =
			DeclareColumn
			(
				"TCB ID",
				"Transmission Control Block ID (reusable)",
				format: ColumnFormats.HexFormat,
				width: 126,
				visible: false
			);

			public static readonly ColumnConfiguration colStatus =
			DeclareColumn
			(
				"Status",
				"Status Response String",
				width: 180,
				visible: true
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

			public static string Server(Request winetObj) => winetObj.strServerName; // ensured in GatherWinINet

			public static string ServerAlt(Request winetObj) => winetObj.strServerAlt; // set in GatherWinINet

			public static string Method(Request winetObj) => winetObj.strMethod ?? String.Empty;

			public static string UrlPath(Request winetObj) => winetObj.strURL ?? String.Empty;

			public static uint ServerPort(Request winetObj) => (uint)winetObj.portS;

			public static uint Port(Request winetObj) => (uint)winetObj.addrRemote.PortGraphable();

			public static uint Socket(Request winetObj) => winetObj.socket;

			public static string IPAddress(Request winetObj) => winetObj.addrRemote.AddrGraphable();

			public static uint Send(Request winetObj) => winetObj.cbSend;

			public static uint Recv(Request winetObj) => winetObj.cbRecv;

			public static QWord Request(Request winetObj) => winetObj.qwRequest;

			public static QWord Connect(Request winetObj) => winetObj.qwConnect;

			public static QWord Context(Request winetObj) => winetObj.qwContext;

			public static string Status(Request winetObj) => winetObj.Status;

			public static QWord TCB(Request winetObj, TcpIp.TcpTable tcbTable) => tcbTable.TcbrFromI(winetObj.iTCB)?.tcb ?? 0;
		} // Generators

		public override void Build(ITableBuilder tableBuilder)
		{
			// Implement your columns here.
			// Columns are implemented via Projections, which are simply functions that map a row index to a data point.
			// Create projection for each column by composing the base projection with another projection that maps to the data point as needed.

			var tableBase = this.Tables?.winetTable;

			if (tableBase == null) return;

			// int -> Request
			var winetBaseProjector = Projection.Index(tableBase);

			// int -> string: URL, Server, Method, Protocol, Status
			var winetServerProjector = Projection.Project(winetBaseProjector, Generators.Server);
			var winetServerAltProjector = Projection.Project(winetBaseProjector, Generators.ServerAlt);
			var winetMethodProjector = Projection.Project(winetBaseProjector, Generators.Method);
			var winetPathProjector = Projection.Project(winetBaseProjector, Generators.UrlPath);
			var winetStatusProjector = Projection.Project(winetBaseProjector, Generators.Status);

			// int -> uint: Send, Recv, Port, Socket
			var winetSendProjector = Projection.Project(winetBaseProjector, Generators.Send);
			var winetRecvProjector = Projection.Project(winetBaseProjector, Generators.Recv);
			var winetPortProjector = Projection.Project(winetBaseProjector, Generators.Port);
			var winetServerPortProjector = Projection.Project(winetBaseProjector, Generators.ServerPort);
			var winetSocketProjector = Projection.Project(winetBaseProjector, Generators.Socket);

			// int -> IPAddress
			var winetAddressProjector = Projection.Project(winetBaseProjector, Generators.IPAddress);

			// int -> QWord
			var winetRequestProjector = Projection.Project(winetBaseProjector, Generators.Request);
			var winetConnectProjector = Projection.Project(winetBaseProjector, Generators.Connect);
			var winetContextProjector = Projection.Project(winetBaseProjector, Generators.Context);
			var winetTCBProjector = Projection.Project(winetBaseProjector, Projection.Constant(this.Tables.tcpTable), Generators.TCB);

			// int -> common projectors: process, thread, stack, start/end time, duration
			var commonProjectors = new ProjectorCommon<Request>(this.Sources, in winetBaseProjector, tableBase.Count);

			// Table Configurations describe how your table should be presented to the user:
			// the columns to show, what order to show them, which columns to aggregate, and which columns to graph.
			// You may provide a number of columns in your table, but only want to show a subset of them by default so as not to overwhelm the user.
			// The user can still open the table properties in WPA to turn on or off columns.
			// The table configuration class also exposes four (4) columns that WPA explicitly recognizes: Pivot Column, Graph Column, Left Freeze Column, Right Freeze Column
			// For more information about what these columns do, go to "Advanced Topics" -> "Table Configuration" in our Wiki. Link can be found in README.md

			// Common columns: colProcessName, colProcess, colStack, colDuration, colOpenTime, colCloseTime
			ColumnsCommon commonColumns = new ColumnsCommon();

			var config = new TableConfiguration("WinINet Info")
			{
				Columns = new[]
				{
					commonColumns.colProcessName,
					commonColumns.colProcess,
					Columns.colServer,
					commonColumns.colStack,
					TableConfiguration.PivotColumn, /*------------*/
					commonColumns.colCount,
					Columns.colServerAlt,
					commonColumns.colThread,
					Columns.colMethod,
					Columns.colServerPort,
					Columns.colSend,
					Columns.colRecv,
					Columns.colUrlPath,
					Columns.colAddr,
					Columns.colPort,
					Columns.colSocket,
					Columns.colRequest,
					Columns.colConnect,
					Columns.colContext,
					Columns.colTCB,
					Columns.colStatus,
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
				.AddCommonColumns(commonColumns, commonProjectors, Sources.stackAccessProvider) // Process, Thread, Duration, Open/CloseTime, stack
				.AddColumn(Columns.colServer, winetServerProjector)
				.AddColumn(Columns.colServerAlt, winetServerAltProjector)
				.AddColumn(Columns.colServerPort, winetServerPortProjector)
				.AddColumn(Columns.colMethod, winetMethodProjector)
				.AddColumn(Columns.colSend, winetSendProjector)
				.AddColumn(Columns.colRecv, winetRecvProjector)
				.AddColumn(Columns.colUrlPath, winetPathProjector)
				.AddColumn(Columns.colAddr, winetAddressProjector)
				.AddColumn(Columns.colPort, winetPortProjector)
				.AddColumn(Columns.colSocket, winetSocketProjector)
				.AddColumn(Columns.colRequest, winetRequestProjector)
				.AddColumn(Columns.colConnect, winetConnectProjector)
				.AddColumn(Columns.colContext, winetContextProjector)
				.AddColumn(Columns.colStatus, winetStatusProjector)
				.AddColumn(Columns.colTCB, winetTCBProjector)
				;

			// this.Sources.Release();
		} // Build
	} // NetBlameTableWinINet
} // NetBlameCustomDataSource.Tables

#endif // AUX_TABLES

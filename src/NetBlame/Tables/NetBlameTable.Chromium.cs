// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

using Microsoft.Performance.SDK.Processing;

using NetBlameCustomDataSource.Chromium;

using QWord = System.UInt64;


namespace NetBlameCustomDataSource.Tables
{
	[Table]
	public sealed class NetBlameTableChromium : NetBlameTableBase
	{
		public NetBlameTableChromium(PendingSources sources, AllTables tables, IApplicationEnvironment environ) : base(sources, tables, environ) { }

		public static TableDescriptor TableDescriptor => new TableDescriptor(
			new Guid("12a5df39-85b6-42e1-8e6a-0b25498ab4aa"), // The GUID must be unique across all tables.
			"NetBlame Chromium Requests",                     // The Table must have a name.
			"NetBlame Network Analyzer - Chromium Requests",  // The Table must have a description.
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

			public static readonly ColumnConfiguration colAnonKey =
			DeclareColumn
			(
				"Origin Context",
				"Network Anonymization Key",
				width: 180,
				visible: false
			);

			public static readonly ColumnConfiguration colMethod =
			DeclareColumn
			(
				"Method",
				"HTTP Method",
				width: 82,
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

			public static readonly ColumnConfiguration colPriority =
			DeclareColumn
			(
				"Priority",
				"URL Connection Priority",
				width: 54,
				visible: true
			);

			public static readonly ColumnConfiguration colSend =
			DeclareColumn
			(
				"Send (B)",
				"Bytes Sent (estimted)",
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
				"Bytes Received (estimated)",
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
				"TCP / WinSock / Chromium Socket ID",
				width: 78,
				align: TextAlignment.Center,
				visible: false
			);

			public static readonly ColumnConfiguration colConnect =
			DeclareColumn
			(
				"Connection ID",
				"Winsock Connection ID (reusable)",
				width: 128,
				format: ColumnFormats.HexFormat,
				visible: false
			);

			public static readonly ColumnConfiguration colRequest =
			DeclareColumn
			(
				"Request ID",
				"Chromium Request ID (reusable)",
				width: 148,
				align: TextAlignment.Right,
				format: ColumnFormats.NumberFormat,
				visible: false
			);

			public static readonly ColumnConfiguration colSession =
			DeclareColumn
			(
				"Session ID",
				"ID of the Session creation event",
				width: 150,
				align: TextAlignment.Right, // number
				format: ColumnFormats.NumberFormat,
				visible: false
			);
#if DEBUG
			public static readonly ColumnConfiguration colIDs =
			DeclareColumn
			(
				"IDs",
				"ID1; ID2; ...",
				width: 160,
				visible: false
			);

			public static readonly ColumnConfiguration colSourceDeps =
			DeclareColumn
			(
				"Source Dependencies",
				"\"source_dependency\":\"id\":#", // "\"source_dependency\":{\"id\":#}" // {Curly Braces} in the description can crash WPA 11.8.423.12582
				width: 130,
				visible: false
			);
#endif // DEBUG
			public static readonly ColumnConfiguration colStatus =
			DeclareColumn
			(
				"Status",
				"HTTP Status Value",
				width: 48,
				visible: true
			);

			public static readonly ColumnConfiguration colError =
			DeclareColumn
			(
				"Error",
				"Error value or condition",
				width: 64,
				visible: false
			);

			public static readonly ColumnConfiguration colTransport =
			DeclareColumn
			(
				"Transport",
				"Session Transport Type: QUIC / HTTP2 / HTTP1",
				width: 64,
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

			public static string Server(Request req) => req.Domain;

			public static string ServerAlt(Request req) => req.Canon;

			public static string AnonKey(Request req) => Chromium.Util.ScrubAnonKey(req.anon_key);

			public static string Method(Request req) => req.method ?? String.Empty;

			public static string UrlPath(Request req) => req.URL ?? String.Empty;

			public static string Priority(Request req) => req.priority.ToString();

			public static string Port(Request req) => req.port.ToStringOrBlank();

#if DEBUG
			private static readonly System.Text.StringBuilder sb = new(160);

			public static string IDs(Request req)
			{
				sb.Clear();

				foreach (System.UInt64 uid in req.rguidDB)
					sb.AppendFormat("{0:#,#}  ", uid);

				return sb.ToString();
			}

			public static string SourceDep(Request req)
			{
				sb.Clear();

				foreach (int srcdep in req.rgsrcdepDB)
					sb.AppendFormat("{0:#}  ", srcdep);

				return sb.ToString();
			}
#endif // DEBUG

			public static uint Send(Request req) => req.stream?.CbSend() ?? 0;

			public static uint Recv(Request req) => req.stream?.CbRecv() ?? 0;

			public static string IPAddress(Request req) => req.AddressAndPort();

			public static string Socket(Request req) => (req.Session?.socket?.WSSocket()).ToStringOrBlank();

			public static string Status(Request req) => req.stream?.strHTTPStatus ?? string.Empty;

			public static string Error(Request req) => req.Error();

			public static string Transport(Request req) => req.Transport();

			public static QWord Request(Request req) => req.UIDRequest;

			public static QWord Session(Request req) => req.UIDSession;
 
			public static QWord Connect(Request req) => req.WSConnection;
		} // Generators


		public override void Build(ITableBuilder tableBuilder)
		{
			// Implement your columns here.
			// Columns are implemented via Projections, which are simply functions that map a row index to a data point.
			// Create projection for each column by composing the base projection with another projection that maps to the data point as needed.

			var tableBase = this.Tables?.chromiumTable;

			if (tableBase == null) return;

			var chromiumBaseProjector = Projection.Index(tableBase);

			// int -> string: URL, Server, Method, Protocol, Status
			var chromiumServerProjector = Projection.Project(chromiumBaseProjector, Generators.Server);
			var chromiumServerAltProjector = Projection.Project(chromiumBaseProjector, Generators.ServerAlt);
			var chromiumAnonKeyProjector = Projection.Project(chromiumBaseProjector, Generators.AnonKey);
			var chromiumMethodProjector = Projection.Project(chromiumBaseProjector, Generators.Method);
			var chromiumPathProjector = Projection.Project(chromiumBaseProjector, Generators.UrlPath);
			var chromiumPriorityProjector = Projection.Project(chromiumBaseProjector, Generators.Priority);
			var chromiumStatusProjector = Projection.Project(chromiumBaseProjector, Generators.Status);
			var chromiumErrorProjector = Projection.Project(chromiumBaseProjector, Generators.Error);
			var chromiumTransportProjector = Projection.Project(chromiumBaseProjector, Generators.Transport);

			// int -> uint: Send, Recv, Port, Socket
			var chromiumSendProjector = Projection.Project(chromiumBaseProjector, Generators.Send);
			var chromiumRecvProjector = Projection.Project(chromiumBaseProjector, Generators.Recv);
			var chromiumPortProjector = Projection.Project(chromiumBaseProjector, Generators.Port);
			var chromiumSocketProjector = Projection.Project(chromiumBaseProjector, Generators.Socket);

			// int -> IPAddress
			var chromiumAddressProjector = Projection.Project(chromiumBaseProjector, Generators.IPAddress);

			// int -> QWord
			var chromiumRequestProjector = Projection.Project(chromiumBaseProjector, Generators.Request);
			var chromiumConnectProjector = Projection.Project(chromiumBaseProjector, Generators.Connect);
			var chromiumSessionProjector = Projection.Project(chromiumBaseProjector, Generators.Session);

#if DEBUG
			// int -> "1,234; 5,678; ..."
			var chromiumIDProjector = Projection.Project(chromiumBaseProjector, Generators.IDs);

			// int -> "123; 456"
			var chromiumSourceDepProjector = Projection.Project(chromiumBaseProjector, Generators.SourceDep);
#endif // DEBUG

			// int -> common projectors: process, thread, stack, start/end time, duration
			var commonProjectors = new ProjectorCommon<Request>(this.Sources, in chromiumBaseProjector, tableBase.Count);


 			// Table Configurations describe how your table should be presented to the user:
			// the columns to show, what order to show them, which columns to aggregate, and which columns to graph.
			// You may provide a number of columns in your table, but only want to show a subset of them by default so as not to overwhelm the user.
			// The user can still open the table properties in WPA to turn on or off columns.
			// The table configuration class also exposes four (4) columns that WPA explicitly recognizes: Pivot Column, Graph Column, Left Freeze Column, Right Freeze Column
			// For more information about what these columns do, go to "Advanced Topics" -> "Table Configuration" in our Wiki. Link can be found in README.md

			// Common columns: colProcessName, colProcess, colStack, colDuration, colOpenTime, colCloseTime
			ColumnsCommon commonColumns = new ColumnsCommon();

			var config = new TableConfiguration("Chromium Info")
			{
				Columns = new[]
				{
					commonColumns.colProcessName,
					commonColumns.colProcess,
					Columns.colServer,
					commonColumns.colStack,
					TableConfiguration.PivotColumn, /*------------*/
					commonColumns.colCount,
					TableConfiguration.LeftFreezeColumn, /*------*/
					commonColumns.colThread,
					Columns.colServerAlt,
					Columns.colAnonKey,
					Columns.colMethod,
					Columns.colPriority,
					Columns.colSend,
					Columns.colRecv,
					Columns.colUrlPath,
					Columns.colAddr,
					Columns.colPort,
					Columns.colSocket,
					Columns.colConnect,
					Columns.colSession,
					Columns.colRequest,
#if DEBUG
					Columns.colIDs,
					Columns.colSourceDeps,
#endif // DEBUG
					Columns.colTransport,
					Columns.colStatus,
					Columns.colError,
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
				.SetDefaultTableConfiguration(config)
				.SetRowCount(tableBase.Count)
				.AddCommonColumns(commonColumns, commonProjectors, null) // Process, Thread, Duration, Open/CloseTime

				.AddHierarchicalColumn(commonColumns.colStack, commonProjectors.stackProjector, Sources.stackAccessProvider)
				.AddColumn(Columns.colServer, chromiumServerProjector)
				.AddColumn(Columns.colServerAlt, chromiumServerAltProjector)
				.AddColumn(Columns.colAnonKey, chromiumAnonKeyProjector)
				.AddColumn(Columns.colMethod, chromiumMethodProjector)
				.AddColumn(Columns.colPriority, chromiumPriorityProjector)
				.AddColumn(Columns.colSend, chromiumSendProjector)
				.AddColumn(Columns.colRecv, chromiumRecvProjector)
				.AddColumn(Columns.colUrlPath, chromiumPathProjector)
				.AddColumn(Columns.colAddr, chromiumAddressProjector)
				.AddColumn(Columns.colPort, chromiumPortProjector)
				.AddColumn(Columns.colSocket, chromiumSocketProjector)
				.AddColumn(Columns.colSession, chromiumSessionProjector)
				.AddColumn(Columns.colRequest, chromiumRequestProjector)
				.AddColumn(Columns.colConnect, chromiumConnectProjector)
#if DEBUG
				.AddColumn(Columns.colIDs, chromiumIDProjector)
				.AddColumn(Columns.colSourceDeps, chromiumSourceDepProjector)
#endif // DEBUG
				.AddColumn(Columns.colTransport, chromiumTransportProjector)
				.AddColumn(Columns.colStatus, chromiumStatusProjector)
				.AddColumn(Columns.colError, chromiumErrorProjector)
				;

			// this.Sources.Release();
		} // Build
	} // NetBlameTableChromium
}

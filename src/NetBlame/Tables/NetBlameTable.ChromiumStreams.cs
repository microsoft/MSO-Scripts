// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

#if AUX_TABLES
#if DEBUG // most valuable for debugging

using System;

using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;

using TimestampUI = Microsoft.Performance.SDK.Timestamp;
using QWord = System.UInt64;


namespace NetBlameCustomDataSource.Tables
{
	[Table]
	public sealed class NetBlameTableChromiumStreams : NetBlameTableBase
	{
		public NetBlameTableChromiumStreams(PendingSources sources, AllTables tables, IApplicationEnvironment environ) : base(sources, tables, environ) { }

		public static TableDescriptor TableDescriptor => new TableDescriptor(
			new Guid("9f63dd43-7265-45d9-8c2b-24872f039e2e"), // The GUID must be unique across all tables.
			"NetBlame Chromium Streams",                      // The Table must have a name.
			"NetBlame Network Analyzer - Chromium Streams",   // The Table must have a description.
			"Network",                                        // Optional category for grouping different types of tables in WPA UI.
			false,                                            // Not Metadata
			TableLayoutStyle.GraphAndTable // .Table          // Chart & Table or Table Only
		);


		// Declare columns here. You can do this using the ColumnConfiguration class.
		// It is possible to declaratively describe the table configuration as well. Please refer to our Advanced Topics Wiki page for more information.
		//
		// The Column metadata describes each column in the table.
		// Each column must have a unique GUID and a unique name. The GUID must be unique globally; the name only unique within the table.
		static class Columns
		{
			public static readonly ColumnConfiguration colProcessId =
			DeclareColumn
			(
				"PID",
				"Process ID",
				width: 56,
				align: TextAlignment.Right,
				format: ColumnFormats.NumberFormat,
				visible: true
			);

			public static readonly ColumnConfiguration colThreadId =
			DeclareColumn
			(
				"TID",
				"Thread ID",
				width: 56,
				align: TextAlignment.Right,
				format: ColumnFormats.NumberFormat,
				visible: true
			);

			public static readonly ColumnConfiguration colType =
			DeclareColumn
			(
				"Type",
				"Session Type: QUIC / HTTP2 / HTTP1",
				width: 58,
				visible: true
			);

			public static readonly ColumnConfiguration colDomain =
			DeclareColumn
			(
				"Domain",
				"Base Server Name",
				width: 180,
				visible: true
			);

			public static readonly ColumnConfiguration colStreamID =
			DeclareColumn
			(
				"#",
				"Stream ID Number within the Session",
				width: 34,
				align: TextAlignment.Right, // number
				visible: true
			);

			public static readonly ColumnConfiguration colSessionID =
			DeclareColumn
			(
				"Session ID",
				"ID of the Session creation event",
				width: 150,
				align: TextAlignment.Right, // number
				format: ColumnFormats.NumberFormat,
				visible: false
			);

			public static readonly ColumnConfiguration colSessionSourceID =
			DeclareColumn
			(
				"Src ID",
				"Session: source_dependency:id",
				width: 48,
				align: TextAlignment.Right, // number
				visible: false
			);

			public static readonly ColumnConfiguration colRequestID =
			DeclareColumn
			(
				"Request ID",
				"Stream's Chromium Request ID (reusable)",
				width: 148,
				align: TextAlignment.Right,
				format: ColumnFormats.NumberFormat,
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
				width: 2000,
				visible: true
			);

			public static readonly ColumnConfiguration colSend =
			DeclareColumn
			(
				"Send (B)",
				"Bytes Sent (estimted)",
				mode: AggregationMode.Sum,
				width: 70,
				align: TextAlignment.Right, // 1 number
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
				align: TextAlignment.Right, // 1 number
				format: ColumnFormats.NumberFormat,
				visible: true
			);

			public static readonly ColumnConfiguration colAddr =
			DeclareColumn
			(
				"IP Address",
				"Remote IP Address",
				width: 118,
				visible: true
			);

			public static readonly ColumnConfiguration colWSSocket =
			DeclareColumn
			(
				"Socket",
				"Winsock Socket ID = Local IP Address Port",
				width: 54,
				align: TextAlignment.Right, // 1 number
				visible: false
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

			public static readonly ColumnConfiguration colStatus =
			DeclareColumn
			(
				"Status",
				"HTTP Status Number",
				width: 46,
				visible: false
			);

			public static readonly ColumnConfiguration colError =
			DeclareColumn
			(
				"Error",
				"Error value or condition",
				width: 60,
				visible: false
			);

			public static readonly ColumnConfiguration colFirstTime =
			NetBlameTableBase.DeclareColumn
			(
				"First Time",
				"The Stream's SEND HEADERS event time",
				width: 102,
				mode: AggregationMode.Min,
				align: TextAlignment.Right,
				format: TimestampFormatter.FormatSecondsGrouped,
				visible: true
			);

			public static readonly ColumnConfiguration colLastTime =
			NetBlameTableBase.DeclareColumn
			(
				"Last Time",
				"The Stream's last SEND or RECEIVE event time",
				width: 102,
				mode: AggregationMode.Min,
				align: TextAlignment.Right,
				format: TimestampFormatter.FormatSecondsGrouped,
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
			public static string IStream(StreamEntry stream) => (stream.stream_id >= 0) ? stream.stream_id.ToString() : string.Empty;

			public static QWord SessionID(StreamEntry stream) => stream.id;

			public static int SourceID(StreamEntry stream) => stream.source_id;

			public static QWord RequestID(StreamEntry stream) => stream.request_id;

			public static int PID(StreamEntry stream) => stream.pid;

			public static int TID(StreamEntry stream) => stream.tid;

			public static string Type(StreamEntry stream) => stream.type;

			public static string Domain(StreamEntry stream) => stream.domain;

			public static string UrlPath(StreamEntry stream) => stream.url;

			public static string AnonKey(StreamEntry stream) => Chromium.Util.ScrubAnonKey(stream.anon_key);

			public static string Method(StreamEntry stream) => stream.method;

			public static string Status(StreamEntry stream) => stream.status;

			public static string Error(StreamEntry stream) => stream.error;

			public static string IPAddress(StreamEntry stream) => stream.addrRemote.ToGraphable();

			public static uint WSSocket(StreamEntry stream) => stream.socket;

			public static uint Port(StreamEntry stream) => stream.port;

			public static uint Send(StreamEntry stream) => stream.cbSend;

			public static uint Recv(StreamEntry stream) => stream.cbRecv;

			public static TimestampUI FirstTime(StreamEntry stream) => stream.timeFirst;
			public static TimestampUI LastTime(StreamEntry stream) => stream.timeLast;
		} // Generators


		public override void Build(ITableBuilder tableBuilder)
		{
			// Implement your columns here.
			// Columns are implemented via Projections, which are simply functions that map a row index to a data point.
			// Create projection for each column by composing the base projection with another projection that maps to the data point as needed.

			var tableBase = this.Tables?.streamTable;

			if (tableBase == null) return;

			var streamBaseProjector = Projection.Index(tableBase);

			// int -> string: Type, Domain, URL, Key, Method, IPAddress
			var streamTypeProjector = Projection.Project(streamBaseProjector, Generators.Type);
			var streamDomainProjector = Projection.Project(streamBaseProjector, Generators.Domain);
			var streamURLProjector = Projection.Project(streamBaseProjector, Generators.UrlPath);
			var streamAnonKeyProjector = Projection.Project(streamBaseProjector, Generators.AnonKey);
			var streamMethodProjector = Projection.Project(streamBaseProjector, Generators.Method);
			var streamStatusProjector = Projection.Project(streamBaseProjector, Generators.Status);
			var streamErrorProjector = Projection.Project(streamBaseProjector, Generators.Error);
			var streamAddressProjector = Projection.Project(streamBaseProjector, Generators.IPAddress);

			// int -> int: PID, TID, stream_id
			var streamPIDProjector = Projection.Project(streamBaseProjector, Generators.PID);
			var streamTIDProjector = Projection.Project(streamBaseProjector, Generators.TID);
			var streamIDProjector = Projection.Project(streamBaseProjector, Generators.IStream);
			var streamSourceIDProjector = Projection.Project(streamBaseProjector, Generators.SourceID);
			var streamRequestIDProjector = Projection.Project(streamBaseProjector, Generators.RequestID);

			// int -> uint: Send, Recv, port, socket
			var streamSendProjector = Projection.Project(streamBaseProjector, Generators.Send);
			var streamRecvProjector = Projection.Project(streamBaseProjector, Generators.Recv);
			var streamWSSocketProjector = Projection.Project(streamBaseProjector, Generators.WSSocket);
			var streamPortProjector = Projection.Project(streamBaseProjector, Generators.Port);

			// int -> QWord
			var streamUIDProjector = Projection.Project(streamBaseProjector, Generators.SessionID);

			// int -> TimestampUI
			var streamFirstTimeProjector = Projection.Project(streamBaseProjector, Generators.FirstTime);
			var streamLastTimeProjector = Projection.Project(streamBaseProjector, Generators.LastTime);


			// Table Configurations describe how your table should be presented to the user:
			// the columns to show, what order to show them, which columns to aggregate, and which columns to graph.
			// You may provide a number of columns in your table, but only want to show a subset of them by default so as not to overwhelm the user.
			// The user can still open the table properties in WPA to turn on or off columns.
			// The table configuration class also exposes four (4) columns that WPA explicitly recognizes: Pivot Column, Graph Column, Left Freeze Column, Right Freeze Column
			// For more information about what these columns do, go to "Advanced Topics" -> "Table Configuration" in our Wiki. Link can be found in README.md

			var config = new TableConfiguration("Chromium Stream Info")
			{
				Columns = new[]
				{
					Columns.colProcessId,
					Columns.colThreadId,
					Columns.colType,
					Columns.colDomain,
					TableConfiguration.PivotColumn, /*------------*/
					Columns.colStreamID,
					Columns.colMethod,
					Columns.colAnonKey,
					Columns.colSend,
					Columns.colRecv,
					Columns.colAddr,
					Columns.colPort,
					Columns.colWSSocket,
					Columns.colSessionID,
					Columns.colRequestID,
					Columns.colSessionSourceID,
					Columns.colStatus,
					Columns.colError,
					Columns.colUrlPath, // Last, Widest
					TableConfiguration.RightFreezeColumn, /*------*/
					TableConfiguration.GraphColumn, /*------------*/
					Columns.colFirstTime,
					Columns.colLastTime
				}
			};

#if !DEBUG
/*
			When open/close timestamps are given this meta-data, zeros get eliminated.
*/
			// Advanced Settings / Graph Configuration
			config.AddColumnRole(ColumnRole.StartTime, colFirstTime.Metadata.Guid);
			config.AddColumnRole(ColumnRole.EndTime, colLastTime.Metadata.Guid);
#endif // !DEBUG
//			config.AddColumnRole(ColumnRole.Duration, colDuration.Metadata.Guid);

			//  Use the table builder to build the table.
			//  Add and set table configuration if applicable.
			//  Then set the row count and then add the columns using AddColumn.

			tableBuilder
				.AddTableConfiguration(config)
				.SetDefaultTableConfiguration(config)
				.SetRowCount(tableBase.Count)
				.AddColumn(Columns.colProcessId, streamPIDProjector)
				.AddColumn(Columns.colThreadId, streamTIDProjector)
				.AddColumn(Columns.colType, streamTypeProjector)
				.AddColumn(Columns.colDomain, streamDomainProjector)
				.AddColumn(Columns.colStreamID, streamIDProjector)
				.AddColumn(Columns.colSessionID, streamUIDProjector)
				.AddColumn(Columns.colSessionSourceID, streamSourceIDProjector)
				.AddColumn(Columns.colRequestID, streamRequestIDProjector)
				.AddColumn(Columns.colUrlPath, streamURLProjector)
				.AddColumn(Columns.colAnonKey, streamAnonKeyProjector)
				.AddColumn(Columns.colMethod, streamMethodProjector)
				.AddColumn(Columns.colStatus, streamStatusProjector)
				.AddColumn(Columns.colError, streamErrorProjector)
				.AddColumn(Columns.colWSSocket, streamWSSocketProjector)
				.AddColumn(Columns.colPort, streamPortProjector)
				.AddColumn(Columns.colAddr, streamAddressProjector)
				.AddColumn(Columns.colSend, streamSendProjector)
				.AddColumn(Columns.colRecv, streamRecvProjector)
				.AddColumn(Columns.colFirstTime, streamFirstTimeProjector)
				.AddColumn(Columns.colLastTime, streamLastTimeProjector)
				;

			// this.Sources.Release();
		} // Build
	} // NetBlameTableChromium
} // NetBlameCustomDataSource.Tables

#endif // DEBUG
#endif // AUX_TABLES

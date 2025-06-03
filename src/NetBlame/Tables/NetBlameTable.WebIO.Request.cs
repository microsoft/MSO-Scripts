#if AUX_TABLES

using System;

using Microsoft.Performance.SDK.Processing;

using NetBlameCustomDataSource.WebIO;

using QWord = System.UInt64;


namespace NetBlameCustomDataSource.Tables
{
	[Table]
	public sealed class NetBlameTableWebIO_Request : NetBlameTableBase
	{
		public NetBlameTableWebIO_Request(PendingSources sources, AllTables tables, IApplicationEnvironment environ) : base(sources, tables, environ) {}

		public static TableDescriptor TableDescriptor => new TableDescriptor(
			new Guid("A4005477-9EE7-4DB6-B992-7544BF6CC2CC"), // The GUID must be unique across all tables.
			"NetBlame WinHTTP Request Table",                 // The Table must have a name.
			"Office Network Analyzer - WebIO Request",        // The Table must have a description.
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

			public static readonly ColumnConfiguration colMethod =
			DeclareColumn
			(
				"Method",
				"HTTP Method",
				width: 58,
				visible: true
			);

			public static readonly ColumnConfiguration colServer =
			DeclareColumn
			(
				"Server",
				"Server of URL",
				width: 200,
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

			public static readonly ColumnConfiguration colRequest =
			DeclareColumn
			(
				"Request",
				"Request ID (reusable)",
				width: 126,
				format: ColumnFormats.HexFormat,
				visible: true
			);

			public static readonly ColumnConfiguration colSend =
			DeclareColumn
			(
				"Bytes Sent",
				"Count of Sent Bytes",
				width: 70,
				align: TextAlignment.Right,
				mode: AggregationMode.Sum,
				visible: true
			);

			public static readonly ColumnConfiguration colRecv =
			DeclareColumn
			(
				"Bytes Recvd",
				"Count of Received Bytes",
				width: 80,
				align: TextAlignment.Right,
				mode: AggregationMode.Sum,
				visible: true
			);

			public static readonly ColumnConfiguration colError =
			DeclareColumn
			(
				"Error",
				"Error connecting a Socket",
				width: 68,
				align: TextAlignment.Right,
				format: ColumnFormats.HexFormat,
				visible: false
			);

			public static readonly ColumnConfiguration colHandle =
			DeclareColumn
			(
				"Handle",
				"Request Handle (reusable)",
				width: 126,
				format: ColumnFormats.HexFormat,
				visible: false
			);

			public static readonly ColumnConfiguration colSession =
			DeclareColumn
			(
				"Session",
				"Session ID (reusable)",
				width: 126,
				format: ColumnFormats.HexFormat,
				visible: false
			);

			public static readonly ColumnConfiguration colHSession =
			DeclareColumn
			(
				"HSession",
				"Session Handle (reusable)",
				width: 126,
				format: ColumnFormats.HexFormat,
				visible: false
			);

			public static readonly ColumnConfiguration colHeader =
			DeclareColumn
			(
				"Header",
				"First line of last header received.",
				width: 150,
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


		// Generators for use in Projections.  See also: GeneratorBase<Request>
		// Static functions are more efficient than non-static, inline lambdas.
		// None of these should return null.
		static class Generators
		{
			// These generators are in GeneratorCommon<>:
			// ProcessData, ProcName, ProcFullName, Thread, OpenStack, OpenTime, CloseTime, Duration

			public static string Method(Request reqObj) => reqObj.strMethod ?? String.Empty;

			public static string UrlPath(Request reqObj) => reqObj.strURL ?? String.Empty;

			public static string Server(Request reqObj) => reqObj.strServer ?? String.Empty;

			public static QWord Request(Request reqObj) => reqObj.qwRequest;

			public static QWord Handle(Request reqObj) => reqObj.qwHandle;

			public static QWord Session(Request reqObj) => reqObj.qwSession;

			public static QWord HSession(Request reqObj) => reqObj.hSession;

			public static uint Send(Request reqObj)
			{
				if (reqObj.rgConnection == null) return 0;
				uint cbSend = 0;
				foreach (Connection cxn in reqObj.rgConnection) cbSend += cxn.cbSend;
				return cbSend;
			}

			public static uint Recv(Request reqObj)
			{
				if (reqObj.rgConnection == null) return 0;
				uint cbRecv = 0;
				foreach (Connection cxn in reqObj.rgConnection) cbRecv += cxn.cbRecv;
				return cbRecv;
			}

			public static uint Error(Request reqObj)
			{
				if (reqObj.rgConnection == null) return 0;
				uint error = 0;
				foreach (Connection cxn in reqObj.rgConnection)
					if (cxn.error != 0)
						error = cxn.error; // last non-zero error one wins

				return error;
			}

			public static string Header(Request reqObj)
			{
				string strHeader = String.Empty;
				if (reqObj.rgConnection == null) return strHeader;
				foreach (Connection cxn in reqObj.rgConnection)
					if (cxn.strHeader != null)
						strHeader = cxn.strHeader; // last non-null header one wins

				return strHeader; // never null
			}
		} // Generators


		public override void Build(ITableBuilder tableBuilder)
		{
			// Implement your columns here.
			// Columns are implemented via Projections, which are simply functions that map a row index to a data point.
			// Create projection for each column by composing the base projection with another projection that maps to the data point as needed.

			var tableBase = this.Tables?.webioTable?.requestTable;

			if (tableBase == null) return;

			// int -> Request
			var reqBaseProjector = Projection.Index(tableBase);

			// int -> string: URL, Server, Method
			var reqMethodProjector = Projection.Project(reqBaseProjector, Generators.Method);
			var reqPathProjector = Projection.Project(reqBaseProjector, Generators.UrlPath);
			var reqServerProjector = Projection.Project(reqBaseProjector, Generators.Server);
			var reqHeaderProjector = Projection.Project(reqBaseProjector, Generators.Header);

			// int -> uint
			var reqSendProjector = Projection.Project(reqBaseProjector, Generators.Send);
			var reqRecvProjector = Projection.Project(reqBaseProjector, Generators.Recv);
			var reqErrorProjector = Projection.Project(reqBaseProjector, Generators.Error);

			// int -> QWord
			var reqRequestProjector = Projection.Project(reqBaseProjector, Generators.Request);
			var reqHandleProjector = Projection.Project(reqBaseProjector, Generators.Handle);
			var reqSessionProjector = Projection.Project(reqBaseProjector, Generators.Session);
			var reqHSessionProjector = Projection.Project(reqBaseProjector, Generators.HSession);

			// int -> common projectors: process, thread, stack, start/end time, duration
			var commonProjectors = new ProjectorCommon<Request>(this.Sources, in reqBaseProjector, tableBase.Count);

			// Table Configurations describe how your table should be presented to the user:
			// the columns to show, what order to show them, which columns to aggregate, and which columns to graph.
			// You may provide a number of columns in your table, but only want to show a subset of them by default so as not to overwhelm the user.
			// The user can still open the table properties in WPA to turn on or off columns.
			// The table configuration class also exposes four (4) columns that WPA explicitly recognizes: Pivot Column, Graph Column, Left Freeze Column, Right Freeze Column
			// For more information about what these columns do, go to "Advanced Topics" -> "Table Configuration" in our Wiki. Link can be found in README.md

			// Common columns: colProcessName, colProcess, colStack, colDuration, colOpenTime, colCloseTime
			ColumnsCommon commonColumns = new ColumnsCommon();

			var config = new TableConfiguration("WebIO Request Info")
			{
				Columns = new[]
				{
					commonColumns.colProcessName,
					commonColumns.colProcess,
					Columns.colServer,
					commonColumns.colStack,
					TableConfiguration.PivotColumn, /*------------*/
					commonColumns.colCount,
					commonColumns.colThread,
					Columns.colMethod,
					Columns.colUrlPath,
					Columns.colSend,
					Columns.colRecv,
					Columns.colError,
					Columns.colHeader,
					Columns.colRequest,
					Columns.colHandle,
					Columns.colSession,
					Columns.colHSession,
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
				// .AddTableConfiguration(config2)
				.SetDefaultTableConfiguration(config)
				.SetRowCount(tableBase.Count)
				.AddCommonColumns(commonColumns, commonProjectors, Sources.stackAccessProvider) // Process, Thread, Stack, Duration, Open/CloseTime
				.AddColumn(Columns.colServer, reqServerProjector)
				.AddColumn(Columns.colMethod, reqMethodProjector)
				.AddColumn(Columns.colUrlPath, reqPathProjector)
				.AddColumn(Columns.colRequest, reqRequestProjector)
				.AddColumn(Columns.colHandle, reqHandleProjector)
				.AddColumn(Columns.colSession, reqSessionProjector)
				.AddColumn(Columns.colHSession, reqHSessionProjector)
				.AddColumn(Columns.colSend, reqSendProjector)
				.AddColumn(Columns.colRecv, reqRecvProjector)
				.AddColumn(Columns.colError, reqErrorProjector)
				.AddColumn(Columns.colHeader, reqHeaderProjector)
				;

			// this.Sources.Release();
		} // Build
	} // NetBlameTableWebIO_Request
} // NetBlameCustomDataSource.Tables

#endif // AUX_TABLES

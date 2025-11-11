using System;

using Microsoft.Performance.SDK.Processing;

using Microsoft.Windows.EventTracing.Symbols;

using QWord = System.UInt64;


namespace NetBlameCustomDataSource.Tables
{
	[Table]
	public sealed class NetBlameTableURL : NetBlameTableBase
	{
		public NetBlameTableURL(PendingSources sources, AllTables tables, IApplicationEnvironment environ) : base(sources, tables, environ) {}

		public static TableDescriptor TableDescriptor => new TableDescriptor(
			new Guid("846FFD66-1260-46B2-8919-E66448DE7F94"), // The GUID must be unique across all tables.
			"Master URL Table - NetBlame",                    // The Table must have a name.
			"Office Network Analyzer per URL",                // The Table must have a description.
			"Network"                                         // Optional category for grouping different types of tables in WPA UI.
		);


		// Declare columns here. You can do this using the ColumnConfiguration class.
		// It is possible to declaratively describe the table configuration as well. Please refer to our Advanced Topics Wiki page for more information.
		//
		// The Column metadata describes each column in the table.
		// Each column must have a unique GUID and a unique name. The GUID must be unique globally; the name only unique within the table.
		//
		// Note that the underlying column GUIDs are hashes of the column names.
		// Therefore changing a column name may degrade any .wpaProfile which refers to that column.

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

			public static readonly ColumnConfiguration colProtocol =
			DeclareColumn
			(
				"Protocol",
				"WinHTTP, WinINet, WinSock, LDAP",
				width: 70,
				visible: true
			);

			public static readonly ColumnConfiguration colMethod =
			DeclareColumn
			(
				"Method",
				"HTTP/WinINet Method or WinSock IPProtocol",
				width: 62,
				visible: true
			);

			public static readonly ColumnConfiguration colUrlPath =
			DeclareColumn
			(
				"URL",
				"Full URL Path",
				width: 386,
				visible: true
			);

			public static readonly ColumnConfiguration colStatus =
			DeclareColumn
			(
				"Status",
				"Last non-zero status of the transaction",
				width: 150,
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

			public static readonly ColumnConfiguration colAddrPort =
			DeclareColumn
			(
				"IPAddr:Port",
				"Remote IP Address & Port",
				width: 128,
				visible: false
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
				"80=http, 443=https, etc.",
				width: 38,
				align: TextAlignment.Center,
				visible: true
			);

			public static readonly ColumnConfiguration colRequest =
			DeclareColumn
			(
				"Request ID",
				"WebIO or WinINet Request ID, or WinSock Endpoint (reusable)",
				width: 126,
				format: ColumnFormats.HexFormat,
				visible: false
			);

			public static readonly ColumnConfiguration colConnection =
			DeclareColumn
			(
				"Connection ID",
				"WebIO or WinINet Connection ID",
				width: 126,
				format: ColumnFormats.HexFormat,
				visible: false
			);

			public static readonly ColumnConfiguration colSocket =
			DeclareColumn
			(
				"Socket ID",
				"Socket ID of TcpIp, WinSock and WinINet, or WebIO",
				width: 72,
				align: TextAlignment.Right,
				visible: false
			);

			public static readonly ColumnConfiguration colPID =
			DeclareColumn
			(
				// colProcess is: ProcessName<FriendlyName> (PID)
				// Sometimes we need just the PID.
				"PID",
				"Process ID",
				align: TextAlignment.Right,
				width: 48,
				visible: false
			);

			public static readonly ColumnConfiguration colThreadFirst =
			DeclareColumn
			(
				"First Thread",
				"Thread ID of earliest available callstack:\r\nThread which first initiated/enqueued the eventual Network Request -\r\nusually the main thread.",
				align: TextAlignment.Right,
				width: 76,
				visible: false
			);

			public static readonly ColumnConfiguration colStackFull =
			DeclareColumn
			(
				"Full Stacks",
				"All call stacks aggregated:\r\nfrom WinMain (usually), chained across thread pools, down to the network request",
				width: 220,
				visible: false // The Stack column slows rendering and hides data.
			);
			
			public static readonly ColumnConfiguration colStackFirst =
			DeclareColumn
			(
				"First Stack",
				"Earliest available callstack:\r\nCallstack which first initiated/enqueued the eventual Network Request -\r\nusually near WinMain, or as far back as can be linked.",
				width: 220,
				visible: false // The Stack column slows rendering and hides data.
			);

			public static readonly ColumnConfiguration colStackMiddle =
			DeclareColumn
			(
				"Middle Stacks",
				"Aggregated callstack which led to the Network Request -\r\nchained across threadpools, excluding the first and last stacks.",
				width: 220,
				visible: false // The Stack column slows rendering and hides data.
			);

			public static readonly ColumnConfiguration colStackLast =
			DeclareColumn
			(
				"Last Stack",
				"Callstack of the actual Network Request -\r\nusually via threadpool dispatch",
				width: 220,
				visible: false // The Stack column slows rendering and hides data.
			);

			public static readonly ColumnConfiguration colStackExport =
			DeclareColumn
			(
				// Outside of WPA the harness needs to have direct access to all stacks at once.
				"All Stacks",
				"All Callstacks for export",
				width: 220,
				visible: false
			);

			public static readonly ColumnConfiguration colSymLoadProgress =
			DeclareColumn
			(
				// Outside of WPA the harness needs to know when symbols have resolved.
				"Symbol %",
				"Percent of Symbols Loaded and Resolved",
				align: TextAlignment.Center,
				width: 66,
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


		class GeoCache
		{
			// Simple caching mechanism for reduce network calls.
			System.Net.IPAddress addrPrev;
			string strGeoLocPrev;

			public string Cache(System.Net.IPAddress addrCur)
			{
				if (!addrCur.Equals(this.addrPrev))
				{
					this.addrPrev = addrCur;
					this.strGeoLocPrev = GeoLocation.GetGeoLocation(addrCur);
				}
				return this.strGeoLocPrev;
			}
		}

		GeoCache geoCache = new GeoCache();


		// Generators for use in Projections
		// Static functions are more efficient than non-static, inline lambdas.
		// None of these should return null.
		static class Generators
		{
			// These generators are in GeneratorCommon<>:
			// ProcessData, ProcName, ProcFullName, Thread, OpenStack, OpenTime, CloseTime, Duration

			public static string Server(URL urlObj) => urlObj.strServer;

			public static string ServerAlt(URL urlObj) => urlObj.strServerAlt;

			public static string Method(URL urlObj) => urlObj.strMethod;

			public static string UrlPath(URL urlObj) => urlObj.strURL;

			public static string Status(URL urlObj) => urlObj.strStatus;

			public static string Protocol(URL urlObj) => urlObj.netType.ToString(); // pinned enum names

			public static uint Port(URL urlObj) => (uint)urlObj.ipAddrPort.PortGraphable(); // may be 0

			public static string IPAddress(URL urlObj) => urlObj.ipAddrPort.AddrGraphable(); // never null

			public static string IPAddrPort(URL urlObj) => urlObj.ipAddrPort.ToGraphable(); // never null

			public static string GeoLoc(URL urlObj, GeoCache geoCache)
			{
				// Implement a simple caching optimization to reduce network requests.
				System.Net.IPAddress addrCur = urlObj.ipAddrPort?.Address;
				if (addrCur == null) return String.Empty;
				return geoCache.Cache(addrCur);
			}

			public static uint Send(URL urlObj) => urlObj.cbSend;

			public static uint Recv(URL urlObj) => urlObj.cbRecv;

			public static QWord Request(URL urlObj) => urlObj.qwRequest;

			public static QWord Connection(URL urlObj) => urlObj.qwConnection;

			public static UInt32 Socket(URL urlObj) => urlObj.dwSocket;

			public static QWord TCB(URL urlObj) => urlObj.tcbId;

			public static string PID(URL urlObj) => StringFromInt(urlObj.Pid);

			// TID which corresponds to StackFirst, hopefully that of WinMain.
			public static string ThreadIdFirst(URL urlObj) => StringFromInt(urlObj.myStack.TidFirst);

			// This is the full aggregation of call stacks: First + Middle + Last
			// This will upcast back to MyStackSnapshot by FullStackSnapshotAccessProvider.
			public static IStackSnapshot StackFull(URL urlObj) => urlObj.myStack;

			// This is the earliest available, enqueuing callstack in the threadpool chain, hopefully near WinMain.
			// This will upcast back to MyStackSnapshot by FirstStackSnapshotAccessProvider.
			public static IStackSnapshot StackFirst(URL urlObj) => urlObj.myStack;

			// This is a collection of IStackSnapshot[] between StackFirst and StackLast.
			// This will upcast back to MyStackSnapshot by MiddleStackSnapshotAccessProvider.
			public static IStackSnapshot StackMiddle(URL urlObj) => urlObj.myStack;

			// This is the final callstack which actually invokes the network request.
			// This implementation of IStackSnapshot returns urlObj.myStack.stackLast.
			public static IStackSnapshot StackLast(URL urlObj) => urlObj.Stack;

			public static System.Collections.Generic.IReadOnlyCollection<IStackSnapshot> StackExport(URL urlObj) => urlObj.myStack.StackChainExport();

			public static int SymLoadProgress(Stack.StackSnapshotAccessProvider ssap) => ssap.SymLoadProgress;
		} // Generators

		public override void Build(ITableBuilder tableBuilder)
		{
			// Implement your columns here.
			// Columns are implemented via Projections, which are simply functions that map a row index to a data point.
			// Create projection for each column by composing the base projection with another projection that maps to the data point as needed.

			var tableBase = this.Tables?.urlTable;

			if (tableBase == null) return;

			// int -> URL
			var urlBaseProjector = Projection.Index(tableBase);

			// int -> string: URL, Server, Method, Protocol
			var urlServerProjector = Projection.Project(urlBaseProjector, Generators.Server);
			var urlServerAltProjector = Projection.Project(urlBaseProjector, Generators.ServerAlt);
			var urlMethodProjector = Projection.Project(urlBaseProjector, Generators.Method);
			var urlPathProjector = Projection.Project(urlBaseProjector, Generators.UrlPath);
			var urlProtocolProjector = Projection.Project(urlBaseProjector, Generators.Protocol);
			var urlStatusProjector = Projection.Project(urlBaseProjector, Generators.Status);

			// int -> uint: Send, Recv, Port, Socket
			var urlSendProjector = Projection.Project(urlBaseProjector, Generators.Send);
			var urlRecvProjector = Projection.Project(urlBaseProjector, Generators.Recv);
			var urlPortProjector = Projection.Project(urlBaseProjector, Generators.Port);
			var urlSocketProjector = Projection.Project(urlBaseProjector, Generators.Socket);

			// int -> numeric string
			var urlPIDProjector = Projection.Project(urlBaseProjector, Generators.PID);
			var urlThreadFirstProjector = Projection.Project(urlBaseProjector, Generators.ThreadIdFirst);

			// int -> IPAddress/string
			var urlAddressProjector = Projection.Project(urlBaseProjector, Generators.IPAddress);
			var urlAddrPortProjector = Projection.Project(urlBaseProjector, Generators.IPAddrPort);

			var urlGeoCacheProjector = Projection.Constant(this.geoCache);
			var urlGeoLocProjector = Projection.Project(urlBaseProjector, urlGeoCacheProjector, Generators.GeoLoc);
			var urlGeoLocationProjector = Projection.CacheOnFirstUse(tableBase.Count, urlGeoLocProjector);

			// int -> QWord
			var urlRequestProjector = Projection.Project(urlBaseProjector, Generators.Request);
			var urlConnectionProjector = Projection.Project(urlBaseProjector, Generators.Connection);
			var urlTCBProjector = Projection.Project(urlBaseProjector, Generators.TCB);

			// int -> IStackSnapshot
			var urlStackFullProjector = Projection.Project(urlBaseProjector, Generators.StackFull);
			var urlStackFirstProjector = Projection.Project(urlBaseProjector, Generators.StackFirst);
			var urlStackMiddleProjector = Projection.Project(urlBaseProjector, Generators.StackMiddle);
			var urlStackLastProjector = Projection.Project(urlBaseProjector, Generators.StackLast);
			var urlStackExportProjector = Projection.Project(urlBaseProjector, Generators.StackExport);

			// Expose the state of symbol resolution for when this add-in is used outside of WPA.
			var urlSymLoadProgress = Projection.Project(Projection.Constant(Sources.stackAccessProvider), Generators.SymLoadProgress);

			// int -> common projectors: process, thread, stack, start/end time, duration
			var commonProjectors = new ProjectorCommon<URL>(this.Sources, in urlBaseProjector, tableBase.Count);

			// Table Configurations describe how your table should be presented to the user:
			// the columns to show, what order to show them, which columns to aggregate, and which columns to graph.
			// You may provide a number of columns in your table, but only want to show a subset of them by default so as not to overwhelm the user.
			// The user can still open the table properties in WPA to turn on or off columns.
			// The table configuration class also exposes four (4) columns that WPA explicitly recognizes: Pivot Column, Graph Column, Left Freeze Column, Right Freeze Column
			// For more information about what these columns do, go to "Advanced Topics" -> "Table Configuration" in our Wiki. Link can be found in README.md

			// Common columns: colProcessName, colProcess, colStack, colDuration, colOpenTime, colCloseTime
			ColumnsCommon commonColumns = new ColumnsCommon();

			var config = new TableConfiguration("Master")
			{
				Columns = new[]
				{
					commonColumns.colProcessName,
					commonColumns.colProcess,
					Columns.colServer,
					Columns.colStackFull,
					Columns.colStackFirst,
					Columns.colStackMiddle,
					Columns.colStackLast,
					TableConfiguration.PivotColumn, /*------------*/
					commonColumns.colCount,
					Columns.colServerAlt,
					Columns.colProtocol,
					Columns.colMethod,
					Columns.colSend,
					Columns.colRecv,
					Columns.colUrlPath,
					Columns.colAddr,
					Columns.colPort,
#if DEBUG
					Columns.colThreadFirst,
					commonColumns.colThread,
					Columns.colRequest,
					Columns.colConnection,
					Columns.colSocket,
					Columns.colTCB,
					commonColumns.colLinkIndex,
					commonColumns.colLinkType,
#endif // DEBUG
					Columns.colStatus,
					Columns.colGeoLocation,
					TableConfiguration.RightFreezeColumn, /*------*/
					commonColumns.colDuration,
					TableConfiguration.GraphColumn, /*------------*/
					commonColumns.colOpenTime,
					commonColumns.colCloseTime,
				}
			};
#if !DEBUG
/*
			When open/close timestamps are given this meta-data, zero values get eliminated.
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
				.AddCommonColumns(commonColumns, commonProjectors, null) // Ignore the common stack column.
				.AddHierarchicalColumn(Columns.colStackFull, urlStackFullProjector, Sources.fullStackAccessProvider) // Special access to chained stacks!
				.AddHierarchicalColumn(Columns.colStackMiddle, urlStackMiddleProjector, Sources.middleStackAccessProvider) // Special access to chained stacks!
				.AddHierarchicalColumn(Columns.colStackFirst, urlStackFirstProjector, Sources.firstStackAccessProvider) // Special handling of first stack.
				.AddHierarchicalColumn(Columns.colStackLast, urlStackLastProjector, Sources.stackAccessProvider)
				.AddColumn(Columns.colServer, urlServerProjector)
				.AddColumn(Columns.colServerAlt, urlServerAltProjector)
				.AddColumn(Columns.colProtocol, urlProtocolProjector)
				.AddColumn(Columns.colMethod, urlMethodProjector)
				.AddColumn(Columns.colSend, urlSendProjector)
				.AddColumn(Columns.colRecv, urlRecvProjector)
				.AddColumn(Columns.colUrlPath, urlPathProjector)
				.AddColumn(Columns.colAddrPort, urlAddrPortProjector)
				.AddColumn(Columns.colAddr, urlAddressProjector)
				.AddColumn(Columns.colPort, urlPortProjector)
				.AddColumn(Columns.colRequest, urlRequestProjector)
				.AddColumn(Columns.colConnection, urlConnectionProjector)
				.AddColumn(Columns.colSocket, urlSocketProjector)
				.AddColumn(Columns.colTCB, urlTCBProjector)
				.AddColumn(Columns.colPID, urlPIDProjector)
				.AddColumn(Columns.colThreadFirst, urlThreadFirstProjector)
				.AddColumn(Columns.colStatus, urlStatusProjector)
				.AddColumn(Columns.colGeoLocation, urlGeoLocationProjector)
				.AddColumn(Columns.colSymLoadProgress, urlSymLoadProgress);
				;

			// If this is WPA then don't add certain columns which are meant only for export to add-ins.
			if (!this.AppEnvironment.IsInteractive)
			{
				(tableBuilder as ITableBuilderWithRowCount)
					.AddColumn(Columns.colStackExport, urlStackExportProjector)
					;
			}

			// this.Sources.Release();
		} // Build
	} // NetBlameTable
} // NetBlameCustomDataSource.Tables

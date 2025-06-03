// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

#if AUX_TABLES
#if DEBUG // most valuable for debugging

using System;

using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;

using Microsoft.Windows.EventTracing.Symbols;

using static NetBlameCustomDataSource.Util;

using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using IDVal = System.Int32; // type of Event.pid/tid / ideally: System.UInt32
using QWord = System.UInt64;


namespace NetBlameCustomDataSource.Tables
{
	[Table]
	public sealed class NetBlameTable_ThreadPool : NetBlameTableBase
	{
		public NetBlameTable_ThreadPool(PendingSources sources, AllTables tables, IApplicationEnvironment environ) : base(sources, tables, environ) {}

		public static TableDescriptor TableDescriptor => new TableDescriptor(
			new Guid("7F24622C-7DC8-47D6-AAA1-33547272EFE3"), // The GUID must be unique across all tables.
			"NetBlame ThreadPool Table",                      // The Table must have a name.
			"Office Network Analyzer - Aggregate ThreadPools",// The Table must have a description.
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
			// colProcessName, colProcess, colThread, colStack, colInvokerStack, colDuration, colOpenTime, colCloseTime

			public static readonly ColumnConfiguration colType =
			NetBlameTableBase.DeclareColumn
			(
				"Type",
				"ThreadPool Type Name",
				align: TextAlignment.Left,
				width: 92,
				visible: true
			);

			public static readonly ColumnConfiguration colSubType =
			DeclareColumn
			(
				"Subtype",
				"Type of ThreadPool Object",
				width: 114,
				visible: true
			);

			public static readonly ColumnConfiguration colStatus =
			DeclareColumn
			(
				"Status",
				"Specific Threadpool Object Final Status",
				width: 86,
				visible: false
			);

			public static readonly ColumnConfiguration colState =
			DeclareColumn
			(
				"State",
				"Generic Threadpool Object Final State",
				width: 52,
				visible: false
			);

			public static readonly ColumnConfiguration colPeriod =
			DeclareColumn
			(
				"Period (ms)",
				"Timers' Recurring Period in milliseconds",
				align: TextAlignment.Right,
				width: 76,
				visible: false
			);

			public static readonly ColumnConfiguration colIdentifier =
			DeclareColumn
			(
				"Context ID",
				"ThreadPool Object Context Identifier (reusable)",
				format: ColumnFormats.HexFormat,
				width: 126,
				visible: false
			);

			public static readonly ColumnConfiguration colRefCount =
			NetBlameTableBase.DeclareColumn
			(
				"RefCount",
				"Count of Foo",
				align: TextAlignment.Right,
				width: 64,
				visible: false
			);

			public static readonly ColumnConfiguration colIndex =
			NetBlameTableBase.DeclareColumn
			(
				"Index",
				"1-based Index of Object within its Table",
				align: TextAlignment.Right,
				width: 42,
				visible: false
			);

			public static readonly ColumnConfiguration colExecThread =
			NetBlameTableBase.DeclareColumn
			(
				"Exec Thread",
				"Thread ID of Execution",
				align: TextAlignment.Right,
				width: 76,
				visible: true
			);

			public static readonly ColumnConfiguration colInvokerThread =
			NetBlameTableBase.DeclareColumn
			(
				"Invoker Thread",
				"Thread ID which enqueued the work item which later enqueued this item",
				align: TextAlignment.Right,
				width: 92,
				visible: false
			);

			public static readonly ColumnConfiguration colExecDuration =
			NetBlameTableBase.DeclareColumn
			(
				"Exec Duration",
				"Execution Duration (ms)",
				width: 126,
				mode: AggregationMode.Sum,
				align: TextAlignment.Right,
				format: TimestampFormatter.FormatMillisecondsGrouped,
				visible: true
			);

			public static readonly ColumnConfiguration colExecStartTime =
			NetBlameTableBase.DeclareColumn
			(
				"Exec Start Time",
				"Execution Began",
				width: 120,
				mode: AggregationMode.Min,
				align: TextAlignment.Right,
				format: TimestampFormatter.FormatSecondsGrouped,
				visible: true
			);

			public static readonly ColumnConfiguration colExecStopTime =
			NetBlameTableBase.DeclareColumn
			(
				"Exec Stop Time",
				"Execution Ended",
				width: 126,
				mode: AggregationMode.Max,
				align: TextAlignment.Right,
				format: TimestampFormatter.FormatSecondsGrouped,
				visible: true
			);

			public static readonly ColumnConfiguration colStack =
			NetBlameTableBase.DeclareColumn
			(
				"Stack",
				"Enqueue stack of this work item",
				width: 200,
				visible: false
			);

			public static readonly ColumnConfiguration colInvokerStack =
			NetBlameTableBase.DeclareColumn
			(
				"Invoker Stack",
				"Enqueue stack of work item which enqueued this work item",
				width: 200,
				visible: false
			);

/*
			public static readonly ColumnConfiguration colNAME =
			NetBlameTableBase.DeclareColumn
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

			public static string Type(ThreadPoolItem tpObj) => tpObj.Type.ToString(); // enum names are pinned strings

			public static string SubType(ThreadPoolItem tpObj) => tpObj.TaskItemInfo.SubTypeName;

			public static string Status(ThreadPoolItem tpObj) => tpObj.TaskItemInfo.StatusName;

			public static string State(ThreadPoolItem tpObj) => tpObj.tpTask.state.ToString(); // enum names are pinned strings

			public static QWord Identifier(ThreadPoolItem tpObj) => tpObj.TaskItemInfo.Identifier;

			public static int Period(ThreadPoolItem tpObj) => tpObj.TaskItemInfo.Period;

			public static uint RefCount(ThreadPoolItem tpObj) => tpObj.tpTask.cRef;

			public static uint Index(ThreadPoolItem tpObj) => tpObj.IFromTask;

			public static IDVal TID(ThreadPoolItem tpObj) => tpObj.tpTask.tidExec;

			public static TimestampUI StartExecTime(ThreadPoolItem tpObj) => tpObj.tpTask.timeStartExec;

			public static TimestampUI StopExecTime(ThreadPoolItem tpObj, TimestampUI timeEnd) => tpObj.tpTask.timeEndExec.HasMaxValue() ? timeEnd : tpObj.tpTask.timeEndExec;

			public static TimestampDelta ExecDuration(ThreadPoolItem tpObj, TimestampUI timeEnd)
			{
				TimestampUI timeStop = StopExecTime(tpObj, timeEnd);
				AssertCritical(timeEnd.HasValue());

				TimestampUI timeStart = StartExecTime(tpObj);
				TimestampDelta timeDelta = timeStop - timeStart;
				AssertCritical(timeDelta.ToNanoseconds >= 0);
				return timeDelta;
			}

			public static IStackSnapshot Stack(ThreadPoolItem tpObj) => tpObj.tpTask.stack;

			public static IStackSnapshot InvokerStack(ThreadPoolItem tpObj) => tpObj.tpTask.xlink.taskLinkNext?.stack;

			public static IDVal InvokerTID(ThreadPoolItem tpObj) => tpObj.tpTask.xlink.taskLinkNext?.tidCreate ?? 0;
		} // Generators


		public override void Build(ITableBuilder tableBuilder)
		{
			// Implement your columns here.
			// Columns are implemented via Projections, which are simply functions that map a row index to a data point.
			// Create projection for each column by composing the base projection with another projection that maps to the data point as needed.

			var tableBase = this.Tables?.tpTable;

			if (tableBase == null) return;

			// int -> XLink
			var xlBaseProjector = Projection.Index(tableBase);

			// int -> string
			var xlTypeName = Projection.Project(xlBaseProjector, Generators.Type);
			var xlSubType = Projection.Project(xlBaseProjector, Generators.SubType);
			var xlStatus = Projection.Project(xlBaseProjector, Generators.Status);
			var xlState = Projection.Project(xlBaseProjector, Generators.State);

			// int -> IDVal
			var xlThreadExec = Projection.Project(xlBaseProjector, Generators.TID);
			var xlInvokerTID = Projection.Project(xlBaseProjector, Generators.InvokerTID);

			// int -> uint
			var xlRefCount = Projection.Project(xlBaseProjector, Generators.RefCount);
			var xlIndex = Projection.Project(xlBaseProjector, Generators.Index);
			var xlPeriod = Projection.Project(xlBaseProjector, Generators.Period);

			// int -> QWord
			var xlIdentifier = Projection.Project(xlBaseProjector, Generators.Identifier);

			// int -> TimestampUI
			var xlTimeEndProjector = Projection.Constant(this.Sources.traceMetadata.LastEventTime.ToGraphable());
			var xlTimeExecStart = Projection.Project(xlBaseProjector, Generators.StartExecTime);
			var xlTimeExecStop = Projection.Project(xlBaseProjector, xlTimeEndProjector, Generators.StopExecTime);

			// int -> TimestampDelta
			var xlTimeExecDuration = Projection.Project(xlBaseProjector, xlTimeEndProjector, Generators.ExecDuration);

			// int -> IStackSnapshot
			var xlStack = Projection.Project(xlBaseProjector, Generators.Stack);
			var xlInvokerStack = Projection.Project(xlBaseProjector, Generators.InvokerStack);

			// int -> common projectors: process, thread, stack, start/end time, duration
			var commonProjectors = new ProjectorCommon<ThreadPoolItem>(this.Sources, in xlBaseProjector, tableBase.Count);

			// Table Configurations describe how your table should be presented to the user:
			// the columns to show, what order to show them, which columns to aggregate, and which columns to graph.
			// You may provide a number of columns in your table, but only want to show a subset of them by default so as not to overwhelm the user.
			// The user can still open the table properties in WPA to turn on or off columns.
			// The table configuration class also exposes four (4) columns that WPA explicitly recognizes: Pivot Column, Graph Column, Left Freeze Column, Right Freeze Column
			// For more information about what these columns do, go to "Advanced Topics" -> "Table Configuration" in our Wiki. Link can be found in README.md

			// Common columns: colProcessName, colProcess, colStack, colDuration, colOpenTime, colCloseTime
			ColumnsCommon commonColumns = new ColumnsCommon();

			var config = new TableConfiguration("ThreadPools")
			{
				Columns = new[]
				{
					commonColumns.colProcessName,
					commonColumns.colProcess,
					Columns.colType,
					Columns.colSubType,
					Columns.colStack,
					Columns.colInvokerStack,
					TableConfiguration.PivotColumn, /*------------*/
					commonColumns.colCount,
					commonColumns.colThread,
					Columns.colExecThread,
					Columns.colInvokerThread,
					Columns.colIdentifier,
					Columns.colStatus,
					Columns.colState,
					Columns.colPeriod,
					Columns.colRefCount,
					Columns.colIndex,
#if DEBUG
					commonColumns.colLinkIndex,
					commonColumns.colLinkType,
#endif // DEBUG
					TableConfiguration.RightFreezeColumn, /*------*/
					commonColumns.colOpenTime,
					commonColumns.colCloseTime,
					commonColumns.colDuration,
					Columns.colExecDuration,
					TableConfiguration.GraphColumn, /*------------*/
					Columns.colExecStartTime,
					Columns.colExecStopTime
				}
			};
#if !DEBUG
/*
			When open/close timestamps are given this meta-data, zeros get eliminated.
*/
			// Advanced Settings / Graph Configuration
			config.AddColumnRole(ColumnRole.StartTime, Columns.colExecStartTime.Metadata.Guid);
			config.AddColumnRole(ColumnRole.EndTime, Columns.colExecStopTime.Metadata.Guid);
#endif // !DEBUG
			config.AddColumnRole(ColumnRole.Duration, Columns.colExecDuration.Metadata.Guid);

			//  Use the table builder to build the table.
			//  Add and set table configuration if applicable.
			//  Then set the row count and then add the columns using AddColumn.

			tableBuilder
				.AddTableConfiguration(config)
			//  .AddTableConfiguration(config2)
				.SetDefaultTableConfiguration(config)
				.SetRowCount(tableBase.Count)
				.AddCommonColumns(commonColumns, commonProjectors, null) // Process, Thread, Duration, Open/CloseTime
				.AddHierarchicalColumn(Columns.colStack, xlStack, Sources.stackAccessProvider)
				.AddHierarchicalColumn(Columns.colInvokerStack, xlInvokerStack, Sources.stackAccessProvider)
				.AddColumn(Columns.colType, xlTypeName)
				.AddColumn(Columns.colSubType, xlSubType)
				.AddColumn(Columns.colIdentifier, xlIdentifier)
				.AddColumn(Columns.colStatus, xlStatus)
				.AddColumn(Columns.colState, xlState)
				.AddColumn(Columns.colPeriod, xlPeriod)
				.AddColumn(Columns.colRefCount, xlRefCount)
				.AddColumn(Columns.colIndex, xlIndex)
				.AddColumn(Columns.colExecThread, xlThreadExec)
				.AddColumn(Columns.colInvokerThread, xlInvokerTID)
				.AddColumn(Columns.colExecStartTime, xlTimeExecStart)
				.AddColumn(Columns.colExecStopTime, xlTimeExecStop)
				.AddColumn(Columns.colExecDuration, xlTimeExecDuration)
				;

			// this.Sources.Release();
		} // Build
	} // NetBlameTable
} // NetBlameCustomDataSource.Tables

#endif // DEBUG
#endif // AUX_TABLES

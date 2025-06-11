// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

using Microsoft.Performance.SDK;
using Microsoft.Performance.SDK.Processing;

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols;

using static NetBlameCustomDataSource.Util;

using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using IDVal = System.Int32; // type of Event.pid/tid / ideally: System.UInt32


namespace NetBlameCustomDataSource.Tables
{
	// BuildTableCore does this:
	//   var table = Activator.CreateInstance(type, parms) as OfficeTaskPoolTableBase;
	// If this creates more than one table type then it helps to share a base type.

	public abstract class NetBlameTableBase
	{
		protected PendingSources Sources { get; }

		protected AllTables Tables { get; }

		protected IApplicationEnvironment AppEnvironment { get; }

		protected NetBlameTableBase(PendingSources sources, AllTables tables, IApplicationEnvironment appEnvironment)
		{
			this.Sources = sources;
			this.Tables = tables;
			this.AppEnvironment = appEnvironment;
		}

		// All tables will need some way to build themselves via the ITableBuilder interface.

		public abstract void Build(ITableBuilder tableBuilder);


		public static Guid GuidFromString(string inputString)
		{
			byte[] rgb;
			using (System.Security.Cryptography.HashAlgorithm hasher = System.Security.Cryptography.MD5.Create())
			{ rgb = hasher.ComputeHash(System.Text.Encoding.Default.GetBytes(inputString)); }

			System.Text.StringBuilder strx = new System.Text.StringBuilder(rgb.Length * 2);

			foreach (byte b in rgb) strx.AppendFormat("{0:x2}", b);

			return Guid.Parse(strx.ToString());
		}


		/*
			Simplify the otherwise messy column declaration stuff.
			Note that changing the name of the column will change its generated GUID,
			and it will no longer work with WPA configuration profiles (.wpaProfile).
		*/
		public static ColumnConfiguration DeclareColumn(string name, string desc,
			int width,
			TextAlignment align = TextAlignment.Left,
			string format = null,
			AggregationMode mode = AggregationMode.None,
			bool dynamic = false,
			bool visible = false,
			SortOrder order = SortOrder.None,
			int pri = 0)
		{
			ColumnMetadata cmd = new ColumnMetadata
			(
				GuidFromString(name), // Deterministic Guid based on the name.
				name,
				desc // does nothing?
			)
			{
				ShortDescription = desc, // tool tip
				IsDynamic = dynamic
				//	FormatProvider = CreateFormatProvider(...)
			};

			UIHints uih = new UIHints
			{
				Width = width,
				TextAlignment = align,
				CellFormat = format,
				AggregationMode = mode,
				IsVisible = visible,
				SortOrder = order,
				SortPriority = pri
			};

			return new ColumnConfiguration(cmd, uih);
		} // ColumnConfiguration
	} // NetBlameTableBase


	/*
		This non-static class generates a set of standard columns, each with its own unique GUID:
		Process, Thread, Stack, Duration, Open/Close Times
	*/
	public class ColumnsCommon
	{
		// The Column metadata describes each column in the table.
		// Each column must have a unique GUID and a unique name.
		// The GUID must be unique globally; the name only unique within the table.

		public readonly ColumnConfiguration colProcessName =
		NetBlameTableBase.DeclareColumn
		(
			"Process Name",
			"All processes with the same name",
			width: 120,
			visible: false
		);

		public readonly ColumnConfiguration colProcess =
		NetBlameTableBase.DeclareColumn
		(
			"Process",
			"Process instances with PID",
			width: 180,
			visible: true
		);

		public readonly ColumnConfiguration colCount =
		NetBlameTableBase.DeclareColumn
		(
			"Count",
			"Count of items",
			mode: AggregationMode.Sum,
			align: TextAlignment.Right,
			width: 58,
			visible: true
		);

		public readonly ColumnConfiguration colThread =
		NetBlameTableBase.DeclareColumn
		(
			"Thread",
			"Thread ID which executed the Network Request",
			align: TextAlignment.Right,
			width: 58,
			visible: false
		);

		public readonly ColumnConfiguration colLinkIndex =
		NetBlameTableBase.DeclareColumn
		(
			"Link Index",
			"1-based index of the linked threadpool item",
			align: TextAlignment.Right,
			width: 66,
			visible: false
		);

		public readonly ColumnConfiguration colLinkType =
		NetBlameTableBase.DeclareColumn
		(
			"Link Type",
			"Type of the linked threadpool item",
			width: 76,
			visible: false
		);

		public readonly ColumnConfiguration colStack =
		NetBlameTableBase.DeclareColumn
		(
			"Stack",
			"Callstack of Network Request or Connection",
			width: 220,
			visible: false // The Stack column slows rendering and hides data.
		);

		public readonly ColumnConfiguration colDuration =
		NetBlameTableBase.DeclareColumn
		(
			"Duration",
			"Time from Open to Close (ms)",
			width: 100,
			mode: AggregationMode.Max,
			align: TextAlignment.Right,
			format: TimestampFormatter.FormatMillisecondsGrouped,
			visible: true
		);

		public readonly ColumnConfiguration colOpenTime =
		NetBlameTableBase.DeclareColumn
		(
			"Open Time",
			"Time Opened",
			width: 102,
			mode: AggregationMode.Min,
			align: TextAlignment.Right,
			format: TimestampFormatter.FormatSecondsGrouped,
			visible: true
		);

		public readonly ColumnConfiguration colCloseTime =
		NetBlameTableBase.DeclareColumn
		(
			"Close Time",
			"Time Closed",
			width: 108,
			mode: AggregationMode.Max,
			align: TextAlignment.Right,
			format: TimestampFormatter.FormatSecondsGrouped,
			visible: true
		);
	} // ColumnsCommon


	// None of these should return null (unless they're intermediate).
	public static class GeneratorCommon<T> where T : IGraphableEntry
	{
		public static IProcess ProcessData(T obj, IProcessDataSource processDataSource) => obj.TimeRef.HasValue ? processDataSource.GetProcess(obj.TimeRef, obj.Pid, Proximity.Exact) : null;

		public static string ProcName(IProcess proc) => proc?.ImageName ?? strNA;

		public static string ProcFullName(IProcess proc)
		{
			var builder = new System.Text.StringBuilder(ProcName(proc));

			try
			{
				string friendlyName = proc?.Package?.FriendlyName;

				if (friendlyName != null)
					builder.AppendFormat("<{0}>", friendlyName);
			}
			catch
			{
				// TODO: race condition / concurrency problem?
			}

			builder.AppendFormat(" ({0})", proc?.Id ?? 0);

			return builder.ToString();
		}

		public static IDVal Thread(T obj) => obj.TidOpen;

		public static IStackSnapshot OpenStack(T obj) => obj.Stack;

		// intrinsically pegged to the start of the trace: 0:00.0
		public static TimestampUI OpenTime(T obj) => obj.TimeOpen;

		// pegged to end of trace for SUM and DURATION
		public static TimestampUI CloseTime(T obj, TimestampUI timeEnd) => obj.TimeClose.HasMaxValue() ? timeEnd : obj.TimeClose;

		public static TimestampDelta Duration(T obj, TimestampUI timeEnd)
		{
			TimestampUI timeClose = CloseTime(obj, timeEnd);
			// TODO: What's the check here?
			if (!timeClose.HasValue())
				return TimestampDelta.Zero;

			TimestampUI timeOpen = OpenTime(obj);
			TimestampDelta timeDelta = timeClose - timeOpen;
			AssertCritical(timeDelta.ToNanoseconds >= 0);
			return timeDelta;
		}

		public static string LinkType(T obj) => obj.LinkType.ToString();

		public static uint LinkIndex(T obj) => obj.LinkIndex;
	} // GeneratorCommon
	

	public class ProjectorCommon<T> where T: IGraphableEntry
	{
		// int -> constant
		public readonly IProjection<int, int> countProjector;

		// int -> IProcess -> string (proc name)
		public readonly IProjection<int, string> processNameProjector;

		// int -> IProcess -> cached string (proc name, friendly name, pid)
		public readonly IProjection<int, string> processCachedFullNameProjector;

		// int -> IDVal: Thread
		public readonly IProjection<int, IDVal> threadProjector;

		// int -> uint
		public readonly IProjection<int, uint> linkIndexProjector;

		// int -> string
		public readonly IProjection<int, string> linkTypeProjector;

		// int -> IStackSnapshot
		public readonly IProjection<int, IStackSnapshot> stackProjector;

		// int -> TimestampDelta: Duration
		public readonly IProjection<int, TimestampDelta> durationProjector;

		// int -> TimestampUI: Open, Close
		public readonly IProjection<int, TimestampUI> openTimeProjector;
		public readonly IProjection<int, TimestampUI> closeTimeProjector;

		public ProjectorCommon(in PendingSources sources, in IProjection<int, T> baseProjector, int cTableBase)
		{
			// int -> 1
			this.countProjector = Projection.Constant(1);

			// int -> TimestampUI (LastEventTime)
			var endTimeProjector = Projection.Constant(sources.traceMetadata.LastEventTime.ToGraphable());

			// int -> time,pid -> IProcess
			var processSourceProjector = Projection.Constant(sources.pendingProcessSource.Result);
			var processProjector = Projection.Project(baseProjector, processSourceProjector, GeneratorCommon<T>.ProcessData);

			// int -> IProcess -> string (proc name)
			this.processNameProjector = Projection.Project(processProjector, GeneratorCommon<T>.ProcName);

			// int -> IProcess -> cached string (proc name, friendly name, pid)
			var processFullNameProjector = Projection.Project(processProjector, GeneratorCommon<T>.ProcFullName);
			this.processCachedFullNameProjector = Projection.CacheOnFirstUse(cTableBase, processFullNameProjector);

			// int -> IDVal: Thread
			this.threadProjector = Projection.Project(baseProjector, GeneratorCommon<T>.Thread);

			this.linkTypeProjector = Projection.Project(baseProjector, GeneratorCommon<T>.LinkType);

			this.linkIndexProjector = Projection.Project(baseProjector, GeneratorCommon<T>.LinkIndex);

			// int -> IStackSnapshot
			this.stackProjector = Projection.Project(baseProjector, GeneratorCommon<T>.OpenStack);

			// int -> TimestampDelta: Duration
			this.durationProjector = Projection.Project(baseProjector, endTimeProjector, GeneratorCommon<T>.Duration);

			// int -> TimestampUI: Open, Close
			this.openTimeProjector = Projection.Project(baseProjector, GeneratorCommon<T>.OpenTime);
			this.closeTimeProjector = Projection.Project(baseProjector, endTimeProjector, GeneratorCommon<T>.CloseTime);
		}
	} // ProjectorCommon

	static class TableExtension
	{
		/*
			Add common columns to the TableBuilder:
			Process Name, Process, Thread ID, Stack, Duration, Open Time, Close Time

			var commonProjectors = new ProjectorCommon<RECORD_TYPE>(Sources, typeBaseProjector, tableBase.Count);
			var commonColumns = new ColumnsCommon();
			...
			tableBuilder
				...
				.SetRowCount(tableBase.Count)
				.AddCommonColumns(commonColumns, commonProjectors)
				...
		*/
		public static ITableBuilderWithRowCount AddCommonColumns<T>(
				this ITableBuilderWithRowCount tb,
				in ColumnsCommon columns,
				in ProjectorCommon<T> projectors,
				ICollectionAccessProvider<IStackSnapshot, string> stackAccessProvider)
				where T : IGraphableEntry
		{
			tb.AddColumn(columns.colProcessName, projectors.processNameProjector);
			tb.AddColumn(columns.colProcess, projectors.processCachedFullNameProjector);
			tb.AddColumn(columns.colCount, projectors.countProjector);
			tb.AddColumn(columns.colThread, projectors.threadProjector);
			tb.AddColumn(columns.colDuration, projectors.durationProjector);
			tb.AddColumn(columns.colOpenTime, projectors.openTimeProjector);
			tb.AddColumn(columns.colCloseTime, projectors.closeTimeProjector);
			if (stackAccessProvider != null)
			{
				tb.AddColumn(columns.colLinkType, projectors.linkTypeProjector);
				tb.AddColumn(columns.colLinkIndex, projectors.linkIndexProjector);
				tb.AddHierarchicalColumn(columns.colStack, projectors.stackProjector, stackAccessProvider);
			}

			return tb;
		}
	} // Extension
} // NetBlameCustomDataSource.Tables

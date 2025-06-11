// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics; // [Conditional()]
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Performance.SDK.Processing;

using Microsoft.Windows.EventTracing; // IPendingResult
using Microsoft.Windows.EventTracing.Events; // IGenericEventDataSource
using Microsoft.Windows.EventTracing.Metadata; // ITraceMetadata
using Microsoft.Windows.EventTracing.Processes; // IProcessDataSource
using Microsoft.Windows.EventTracing.Symbols; // IStackDataSource

using NetBlameCustomDataSource.Classic;
using NetBlameCustomDataSource.DNSClient;
using NetBlameCustomDataSource.OTaskPool;
using NetBlameCustomDataSource.ODispatchQ;
using NetBlameCustomDataSource.MsoIdleMan;
using NetBlameCustomDataSource.Tables;
using NetBlameCustomDataSource.TcpIp;
using NetBlameCustomDataSource.Thread;
using NetBlameCustomDataSource.WebIO;
using NetBlameCustomDataSource.WinHTTP;
using NetBlameCustomDataSource.WinINet;
using NetBlameCustomDataSource.WinsockAFD;
using NetBlameCustomDataSource.WinsockNameRes;
using NetBlameCustomDataSource.WThreadPool;

using static NetBlameCustomDataSource.Util;

/*
	TimeStamp Strategy

	The WPA UI needs TimestampUI:
		TableBuilder.AddColumn(ColumnConfiguration, timeStampUI);
	Certain methods need TimestampETW:
		processDataSource.GetProcess(TimestampETW, PID, Proxmity)
		threadDataSource.GetThread(TimestampETW, TID, Proximity)
		stackDataSource.GetStack(TimestampETW, TID)

	It is rather expensive to convert from TimestampETW to TimestampUI.
	It is apparently not possible to convert from TimestampUI to TimestampETW.
*/
using TimestampUI = Microsoft.Performance.SDK.Timestamp;
using TimestampETW = Microsoft.Windows.EventTracing.TraceTimestamp;

using IDVal = System.Int32; // Process/ThreadID (ideally UInt32)

// Event Tracing / Processing Samples:
// https://github.com/microsoft/eventtracing-processing-samples


namespace NetBlameCustomDataSource
{
	/*
		Table entries which contain an XLink and implement these properties can be graphed using GeneratorBase.
	*/
	public interface IGraphableEntry
	{
		IDVal Pid { get; }
		IDVal TidOpen { get; }
		TimestampETW TimeRef { get; }
		TimestampUI TimeOpen { get; }
		TimestampUI TimeClose { get; }
		IStackSnapshot Stack { get; }
		Link.XLinkType LinkType { get; }
		uint LinkIndex { get; }
	}


	public class Gatherable
	{
#if DEBUG
		// Regrettably, _any_ reference to DEBUG-only fields, even Assert(fGathered), must be be wrapped with #if DEBUG.
		// https://ericlippert.com/2009/09/10/whats-the-difference-between-conditional-compilation-and-the-conditional-attribute/
		public bool fGathered;
#endif // DEBUG

		[Conditional("DEBUG")]
		public void AssertGathered()
		{
#if DEBUG
			// Regrettably, _any_ reference to DEBUG-only fields must be be wrapped with #if DEBUG.
			AssertCritical(this.fGathered);
#endif // DEBUG
		}
	}


	public class SymLoadProgress : IProgress<SymbolLoadingProgress>
	{
		int pctProcessed;

		public int PctProcessed => this.pctProcessed;

		public void Report(SymbolLoadingProgress slp)
		{
			pctProcessed = slp.ImagesProcessed * 100 / slp.ImagesTotal;
		}
	}


	class MyConsoleSymbolProgress : IProgress<SymbolLoadingProgress>
	{
		int tenth = 0;

		public void Report(SymbolLoadingProgress progress)
		{
			if (this.tenth < 0)
				return;

			if (this.tenth == 0)
				Console.Write("Symbol Resolution: ");

			int c10th = (progress.ImagesProcessed * 10 - 1) / progress.ImagesTotal; // 0-9

			for (int i10th = this.tenth; i10th <= c10th; ++i10th)
				Console.Write(i10th); // 0-9

			this.tenth = c10th + 1; // next digit to write

			if (progress.ImagesProcessed == progress.ImagesTotal)
			{
				this.tenth = -1; // sentinel
				Console.WriteLine(" 100% - Successfully loaded {0} of {1}", progress.ImagesLoaded, progress.ImagesTotal);
			}
		}

		public static void Disabled() => Console.WriteLine("Symbol resolution disabled.");
		public static void Error() => Console.WriteLine("Symbol resolution error.");
		public static void Cancel() => Console.WriteLine("\nSymbol resolution canceled.");
	} // MyConsoleSymbolProgress

	public struct PendingSources
	{
		public ITraceTimestampContext traceTimestampContext;
		public ITraceMetadata traceMetadata;
		public IPendingResult<IProcessDataSource> pendingProcessSource;
		public IPendingResult<IThreadDataSource> pendingThreadSource;
		public IPendingResult<IStackDataSource> pendingStackSource;
		public IPendingResult<IGenericEventDataSource> pendingGenericEventSource;
		public IPendingResult<ISymbolDataSource> pendingSymbolSource;
		public Stack.StackSnapshotAccessProvider stackAccessProvider;
		public Stack.FirstStackSnapshotAccessProvider firstStackAccessProvider;
		public Stack.MiddleStackSnapshotAccessProvider middleStackAccessProvider;
		public Stack.FullStackSnapshotAccessProvider fullStackAccessProvider;

		public void Init(ITraceProcessor traceProcessor, Guid[] rgGuidGeneric, SymLoadProgress symLoadProgress)
		{
			this.pendingGenericEventSource = traceProcessor.UseGenericEvents(rgGuidGeneric);
			this.traceTimestampContext = traceProcessor.UseTraceTimestampContext();
			this.traceMetadata = traceProcessor.UseMetadata();
			this.pendingProcessSource = traceProcessor.UseProcesses();
			this.pendingThreadSource = traceProcessor.UseThreads();
			this.pendingStackSource = traceProcessor.UseStacks();
			this.pendingSymbolSource = traceProcessor.UseSymbols();
			this.stackAccessProvider = new Stack.StackSnapshotAccessProvider(symLoadProgress);
			this.firstStackAccessProvider = new Stack.FirstStackSnapshotAccessProvider(symLoadProgress);
			this.middleStackAccessProvider = new Stack.MiddleStackSnapshotAccessProvider(symLoadProgress);
			this.fullStackAccessProvider = new Stack.FullStackSnapshotAccessProvider(symLoadProgress);
		}

#if NOT_YET
		public void Release()
		{
			traceTimestampContext = null;
			traceMetadata = null;
			pendingProcessSource = null;
			pendingThreadSource = null;
			pendingStackSource = null;
			pendingGenericEventSource = null;
			pendingSymbolSource = null;
			stackAccessProvider = null;
		}
#endif // NOT_YET
	} // PendingSources


	public class AllTables
	{
		// DNS Events
		public DNSTable dnsTable;
		public WinsockNameResolution wsName;
		// Network Events
		public TcpTable tcpTable;
		public WinsockTable wsTable;
		public WebIOTable webioTable;
		public WinINetTable winetTable;
		// ThreadPool Events
		public WinHttpTable httpTable;
		public WThreadPoolTable wtpTable;
		public OTaskPoolTable otpTable;
		public ODispatchQTable odqTable;
		public IdleManTable idleTable;
		public ThreadTable threadTable;

		// Final Aggregated Results
		public URLTable urlTable;
#if AUX_TABLES
		// Index-pair of all IP Addresses in the DNS table
		public List<DNSIndex> dnsIndexTable;
		// References to all threadpool objects: Office TP, WinHTTP TP, Windows TP, Windows Timers
		public List<ThreadPoolItem> tpTable;
#endif // AUX_TABLES

		// Get a stack for a Classic event: stackSource?.GetStack(timestampETW, tid);
		public IStackDataSource stackSource;

		public AllTables()
		{
			// TODO: smart capacity in each of these?
			// DNS Info Tables
			this.dnsTable = new DNSTable(64);
			this.wsName = new WinsockNameResolution(this);
			// Network Event Tables
			this.wsTable = new WinsockTable(512, this);
			this.tcpTable = new TcpTable(1024, this);
			this.webioTable = new WebIOTable(512, this);
			this.winetTable = new WinINetTable(128, this);
			// Thread/TaskPool Tables
			this.httpTable = new WinHttpTable(4096, this);
			this.wtpTable = new WThreadPoolTable(1024, this);
			this.otpTable = new OTaskPoolTable(2048, this);
			this.odqTable = new ODispatchQTable(4096, this);
			this.idleTable = new IdleManTable(128, this);
			this.threadTable = new ThreadTable(1024, this);
		}


		/*
			Return the count of key network records, not DNS or ThreadPool records.
			Without at least a few of these there can be no meaningful final output.
		*/
		public int EventCount()
		{
			return this.wsTable.Count()
					+ this.tcpTable.Count()
					+ this.webioTable.sessionTable.Count()
					+ this.winetTable.Count();
		}


		/*
			Remove any remaining ThreadPool records which are not tied to a network record.
		*/
		public void GarbageCollect()
		{
			this.httpTable.GarbageCollect(true);
			this.wtpTable.wtpCallbackTable.GarbageCollect(true);
			this.wtpTable.wtpTimerTable.GarbageCollect(true);
			this.otpTable.GarbageCollect(true);
			this.odqTable.GarbageCollect(true);
			this.idleTable.GarbageCollect(true);
			this.threadTable.GarbageCollect(true);
		}


		[Conditional("DEBUG")]
		public void Validate()
		{
			// Mark the key network records which have XLink / callstacks.

			foreach (var wreq in this.winetTable)
				wreq.xlink.Mark();

			foreach (var request in this.webioTable.requestTable)
				request.xlink.Mark();

			foreach (var session in this.webioTable.sessionTable)
				session.xlink.Mark();

			foreach (var wsock in this.wsTable)
				wsock.xlink.Mark();

			// Validate key network records.

			foreach (var wreq in this.winetTable)
			{
				wreq.AssertGathered();
				wreq.xlink.ValidateMarks();
			}

			foreach (WebIO.Request request in this.webioTable.requestTable)
			{
				request.AssertGathered();
				request.xlink.ValidateMarks();
			}

			foreach (var session in this.webioTable.sessionTable)
			{
				session.xlink.ValidateMarks();
			}

			foreach (var wsock in this.wsTable)
			{
				wsock.xlink.ValidateMarks();
			}

			foreach (var tcb in this.tcpTable)
			{
				AssertCritical(tcb.fGathered);
			}

			// Further validate ThreadPool tables.

			this.httpTable.Validate();

			this.wtpTable.wtpCallbackTable.Validate();

			this.wtpTable.wtpTimerTable.Validate();

			this.otpTable.Validate();

			this.odqTable.Validate();

			this.idleTable.Validate();

			this.threadTable.Validate();
		}
	} // AllTables


	/*
		Implement the CustomDataProcessorBase abstract class.
	*/
	public sealed class NetBlameDataProcessor : CustomDataProcessor
	{
		private readonly string tracePath;
		private PendingSources sources;
		private AllTables tables;

		public NetBlameDataProcessor(
		   string tracePath,
		   ProcessorOptions options,
		   IApplicationEnvironment applicationEnvironment,
		   IProcessorEnvironment processorEnvironment)
		   : base(options, applicationEnvironment, processorEnvironment)
		{
			this.tracePath = tracePath;
		}

		/*
			The DataSourceInfo is used to tell WPA the time range of the data (if applicable) and any other relevant data for rendering / synchronizing.
		*/
		public override DataSourceInfo GetDataSourceInfo()
		{
			if (this.sources.traceMetadata == null)
				return default;

			var startTime = this.sources.traceMetadata.FirstAnalyzerDisplayedEventTime;
			var endTime = this.sources.traceMetadata.LastEventTime;
			DateTime dtFirstEvent = this.sources.traceMetadata.FirstAnalyzerDisplayedEventTime.DateTimeOffset.UtcDateTime;

			return new DataSourceInfo(startTime.Nanoseconds, endTime.Nanoseconds, dtFirstEvent);
		}


		// These providers will get processed automatically as generic events.
		readonly Guid[] rgGuidGeneric = new Guid[]
		{
			ODispatchQTable.guid,       // OfficeDispatchQueue
			OTaskPoolTable.guid,        // Microsoft-Office-ThreadPool
			IdleManTable.guid,          // Microsoft-Office-Events
			TcpTable.guid,              // Microsoft-Windows-TCPIP
			WinsockTable.guid,          // Microsoft-Windows-Winsock-AFD
			WinsockNameResolution.guid, // Microsoft-Windows-Winsock-NameResolution
			DNSTable.guid,              // Microsoft-Windows-DNS-Client
			WinINetTable.guid,          // Microsoft-Windows-WinINet
			WinHttpTable.guid,          // Microsoft-Windows-WinHttp
			WebIOTable.guid             // Microsoft-Windows-WebIO
		};

		// These providers will get processed in the ClassicEventConsumer callback.
		readonly Guid[] rgGuidClassic = new Guid[]
		{
			WThreadPoolTable.guid,      // Windows-ThreadPool
			ThreadTable.guid,           // Thread
		};

		// Processes which emit events from these network providers have interesting call stacks.
		// Descending priority.
		readonly Guid[] rgGuidStack = new Guid[]
		{
			WebIOTable.guid,            // Microsoft-Windows-WebIO
			WinINetTable.guid,          // Microsoft-Windows-WinINet
			WinsockTable.guid,          // Microsoft-Windows-Winsock-AFD
		};


		bool FSymbolsEnabled()
		{
			// If either of these are null/empty then LoadSymbolsAsync won't do anything anyway, it appears.
			// But if _NT_SYMCACHE_PATH or _NT_SYMBOL_PATH were empty then default values will appear here.
			return !String.IsNullOrWhiteSpace(SymCachePath.Automatic.Value) && !String.IsNullOrWhiteSpace(SymbolPath.Automatic.Value);
		}


		/*
			Load symbols only for the processes that we're analyzing:
			those which emitted events from the providers listed in rgGuidStack.
			Return the async Task, or null.
		*/
		Task LoadSymbolsAsync(IProgress<SymbolLoadingProgress> slpLogger)
		{
			var rgEvt = this.sources.pendingGenericEventSource.Result?.Events;
			if (rgEvt == null) return null;

			// Get the IDs of all processes which emitted events from the list of key network providers.
			var rgPID = rgGuidStack.SelectMany(guid => rgEvt.Where(evt => evt.ProviderId == guid)).Select(evtT => evtT.ProcessId).Distinct();

			var rgProc = this.sources.pendingProcessSource.Result?.Processes;
			if (rgProc == null) return null;

			// Get the process names of the given IDs, except not Idle & System.
			string[] rgProcTarget = rgPID?.SelectMany(pid => rgProc.Where(proc => proc.Id == pid && proc.Id > 4)).Select(procT => procT.ImageName).Where(name => name != null).Distinct().ToArray();
			if (rgProcTarget == null) return null;

			ISymbolDataSource symbolSource = this.sources.pendingSymbolSource.Result;
			if (!symbolSource.CanLoadSymbols) return null;

			// WPA doesn't allow -symbols or -symcacheonly with -addsearchdir
			// So simulate -symcacheonly with: !!_NT_SYMCACHE_PATH & !_NT_SYMBOL_PATH

			bool fSymCacheOnly = SymCachePath.FromEnvironment != null && SymbolPath.FromEnvironment == null;
#if DEBUG
			fSymCacheOnly = true; // fast symbols when debugging
#endif // DEBUG

			// Begin loading the symbols for the given process names.

			return symbolSource.LoadSymbolsAsync(SymCachePath.Automatic, !fSymCacheOnly ? SymbolPath.Automatic : null, slpLogger, rgProcTarget);
		} // LoadSymbolsAsync


		AllTables GenerateProviderTables(ClassicEventConsumer eventConsumer, in PendingSources sources, CancellationToken cancellationToken)
		{
			AllTables allTables = new AllTables();

			if (sources.pendingStackSource.HasResult)
				allTables.stackSource = sources.pendingStackSource.Result;

			if (sources.pendingThreadSource.HasResult)
				allTables.threadTable.ThreadSource = sources.pendingThreadSource.Result;

			allTables.threadTable.SetThreadRundown(eventConsumer.threadEventConsumer.FHaveRundown);

			var traceWThreadPool = eventConsumer.wtpEventConsumer.traceWThreadPool;
			var threadEventQueue = eventConsumer.threadEventConsumer.threadEventQueue;

			long nsThread = (threadEventQueue.Count > 0) ? threadEventQueue.Peek().timeStamp.Nanoseconds : long.MaxValue;
			long nsWTPool = (traceWThreadPool.Count > 0) ? traceWThreadPool.Peek().timeStamp.Nanoseconds : long.MaxValue;

			// Process each event, Generic or Classic, in strict time order.

			foreach (var evtGeneric in this.sources.pendingGenericEventSource.Result.Events)
			{
				var nsGeneric = evtGeneric.Timestamp.Nanoseconds;

				while (Math.Min(nsWTPool, nsThread) <= nsGeneric)
				{
					// Dispatch classic events.
					// If there are more than two types, we may want to create a single queue.

					if (nsWTPool <= nsThread)
					{
						var dq = traceWThreadPool.Dequeue();
						allTables.wtpTable.Dispatch(dq);
						nsWTPool = (traceWThreadPool.Count > 0) ? traceWThreadPool.Peek().timeStamp.Nanoseconds : long.MaxValue;
						AssertImportant(dq.timeStamp.Nanoseconds <= nsWTPool); // sorted!
					}
					else
					{
						var dq = threadEventQueue.Dequeue();
						allTables.threadTable.Dispatch(dq);
						nsThread = (threadEventQueue.Count > 0) ? threadEventQueue.Peek().timeStamp.Nanoseconds : long.MaxValue;
						AssertImportant(dq.timeStamp.Nanoseconds <= nsThread); // sorted!
					}
				}

				if (evtGeneric.ProviderId == OTaskPoolTable.guid)
				{
					allTables.otpTable.Dispatch(evtGeneric);
				}
				else if (evtGeneric.ProviderId == ODispatchQTable.guid)
				{
					allTables.odqTable.Dispatch(evtGeneric);
				}
				else if (evtGeneric.ProviderId == IdleManTable.guid)
				{
					allTables.idleTable.Dispatch(evtGeneric);
				}
				else if (evtGeneric.ProviderId == TcpTable.guid)
				{
					allTables.tcpTable.Dispatch(evtGeneric);
				}
				else if (evtGeneric.ProviderId == WinsockTable.guid)
				{
					allTables.wsTable.Dispatch(evtGeneric);
				}
				else if (evtGeneric.ProviderId == WinHttpTable.guid)
				{
					allTables.httpTable.Dispatch(evtGeneric);
				}
				else if (evtGeneric.ProviderId == WebIOTable.guid)
				{
					allTables.webioTable.Dispatch(evtGeneric);
				}
				else if (evtGeneric.ProviderId == WinINetTable.guid)
				{
					allTables.winetTable.Dispatch(evtGeneric);
				}
				else if (evtGeneric.ProviderId == WinsockNameResolution.guid)
				{
					allTables.wsName.Dispatch(evtGeneric);
				}
				else if (evtGeneric.ProviderId == DNSTable.guid)
				{
					allTables.dnsTable.Dispatch(evtGeneric);

					// Lower frequency event for polling.
					if (cancellationToken.IsCancellationRequested) return null;
				}
			} // foreach evtGeneric

			// Generic events were all dispatched, but some classic events may remain.
			// We care only about classic events which occurred _before_ generic network events.

			if (cancellationToken.IsCancellationRequested)
				return null;

			if (allTables.EventCount() == 0)
				return null;

			allTables.GarbageCollect();

			return allTables;
		} // GenerateProviderTables


		protected override Task ProcessAsyncCore(
		   IProgress<int> progress,
		   CancellationToken cancellationToken)
		{
			// Check the path of: Microsoft.Windows.EventTracing.Processing.Toolkit
			// Expected at: <AddIn_Path>\X64\wpt\perfcore.dll

			const string strToolkitFolder = "wpt";
			const string strModuleCheck = "PerfCore.dll";

			string toolkitPath1 = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
			string toolkitPath2 = Path.Combine(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), strToolkitFolder);

			// Test: AddIn_Path + "X64\wpt" + "PerfCore.dll"
			toolkitPath1 = Path.Combine(toolkitPath1, toolkitPath2);
			if (!File.Exists(Path.Combine(toolkitPath1, strModuleCheck)))
			{
				if (!this.ApplicationEnvironment.IsInteractive)
				{
					Console.Error.WriteLine("ERROR: {0} and related not found in:", strModuleCheck);
					Console.Error.WriteLine("{0}", toolkitPath1);
				}
				throw new OperationCanceledException("Windows Performance Toolkit not found: ADDIN_PATH\\" + Path.Combine(toolkitPath2, strModuleCheck));
			}

			TraceProcessorSettings settings = new TraceProcessorSettings { ToolkitPath = toolkitPath1, AllowLostEvents = true };

			ITraceProcessor traceProcessor = TraceProcessor.Create(this.tracePath, settings);
			if (traceProcessor == null) return Task.CompletedTask; // Task.FromCanceled(cancellationToken); // TODO: must be canceled first

			ClassicEventConsumer eventConsumer = new ClassicEventConsumer(rgGuidClassic);
			traceProcessor.Use(eventConsumer);

			bool fSymbols = FSymbolsEnabled();

			IProgress<SymbolLoadingProgress> symbolLoadingProgress = null;
			SymLoadProgress symLoadProgressUI = null;

			if (fSymbols)
			{
				if (this.ApplicationEnvironment.IsInteractive)
					symbolLoadingProgress = symLoadProgressUI = new SymLoadProgress();
				else
					symbolLoadingProgress = new MyConsoleSymbolProgress();
			}

			this.sources.Init(traceProcessor, rgGuidGeneric, symLoadProgressUI);

			traceProcessor.Process(/*IProgress<TraceProcessingProgress>*/);

			eventConsumer.threadEventConsumer.Complete(); // final sorting, etc.

#if DEBUG
			// Get the count of each provider for debugger viewing.
			int i = 0;
			int[] rgCount = new int[rgGuidGeneric.Length];
			foreach (Guid guid in rgGuidGeneric)
			{
				rgCount[i++] = this.sources.pendingGenericEventSource.Result.Events?.Where(evt => evt.ProviderId == guid).Count() ?? 0;
			}
#endif // DEBUG

			// Last chance to cancel before firing up the symbol resolution.
			if (cancellationToken.IsCancellationRequested)
			{
				// this.sources.Release();
				return Task.FromCanceled(cancellationToken);
			}

			Task taskSym = null;
			if (fSymbols)
				taskSym = LoadSymbolsAsync(symbolLoadingProgress);

			progress.Report(50);

			this.tables = GenerateProviderTables(eventConsumer, this.sources, cancellationToken);

			if (this.tables == null)
			{
				// this.sources.Release();

				const string strError = "There were no events of interest.";

				if (!this.ApplicationEnvironment.IsInteractive)
					Console.Error.WriteLine(strError);

				progress.Report(100);
				return Task.CompletedTask;
			}

			progress.Report(75);

			this.tables.urlTable = new URLTable(this.tables);

			this.tables.urlTable.GatherAll();

			// Validate the provider tables after GatherAll: they should all be marked as Gathered.
			this.tables.Validate();

			// If this is an add-in for a console harness then help out the symbol resolution.
			if (!this.ApplicationEnvironment.IsInteractive)
			{
				if (!fSymbols)
					MyConsoleSymbolProgress.Disabled();
				else if (taskSym == null)
					MyConsoleSymbolProgress.Error();
				else if (cancellationToken.IsCancellationRequested)
					MyConsoleSymbolProgress.Cancel();
				else
					taskSym.Wait();
			}

			// this.sources.Release(); // TODO: Selective Release

			progress.Report(100);
			return Task.CompletedTask;
		} // ProcessAsyncCore


		protected override void BuildTableCore(
			TableDescriptor tableDescriptor,
			ITableBuilder tableBuilder)
		{
			// Instantiate the table, and pass the tableBuilder to it.

			Type type = tableDescriptor.ExtendedData["Type"] as Type;
			if (type == null) return;

			var parms = new object[] { this.sources, this.tables, this.ApplicationEnvironment };
			NetBlameTableBase table = Activator.CreateInstance(type, parms) as NetBlameTableBase;
			table.Build(tableBuilder);
		}
	} // NetBlameDataProcessor
} // NetBlameCustomDataSource

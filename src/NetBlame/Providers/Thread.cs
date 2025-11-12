// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic; // Dictionary

using NetBlameCustomDataSource.Link;
using NetBlameCustomDataSource.Tasks;
using NetBlameCustomDataSource.Thread.Classic;

using static NetBlameCustomDataSource.Util;

using TimestampETW = Microsoft.Windows.EventTracing.TraceTimestamp;
using TimestampUI = Microsoft.Performance.SDK.Timestamp;
using IThreadDataSource = Microsoft.Windows.EventTracing.Processes.IThreadDataSource;

using IDVal = System.Int32; // Process/ThreadID (ideally UInt32)
using QWord = System.UInt64;
using Addr64 = System.UInt64;

/*
	Sometimes a new thread is created and used as a thread pool work item:
		Create Thread -> Create and Enqueue Work Item
		Execute ThreadProc -> Dequeue Work Item and Execute
		Exit Thread -> End Execution and Destory Work Item
*/

namespace NetBlameCustomDataSource.Thread
{
	public enum ThreadClass : byte
	{
		// Rarely, a thread can have multiple dispatchers.
		// These are arranged in increasing priority.
		None,
		OIdleMan,     // Office Idle Manager
		ODispatchQ,   // Office Dispatch Queue
		OTaskPool,    // Office TaskPool dispatcher
		WThreadPool,  // Windows ThreadPool dispatcher (Callback, Timer, or WinHTTP)
		Max
	}

	public class ThreadItem : TaskItem, ITaskItemInfo
	{
		public Addr64 addrThreadProc;
#if DEBUG
		public IDVal pidCreate;
#endif // DEBUG

		// Return a reference to the most recently executing task on this thread.
		private TaskItem _taskExec;
		public TaskItem TaskExec
		{
			get
			{
				// Could be out-of-date, if the task was requeued and dispatched on another thread.
				if (_taskExec?.tidExec != this.tidExec)
					_taskExec = null;

				return _taskExec;
			}
			set
			{
				AssertCritical(value.tidExec == this.tidExec);
				_taskExec = value;
			}
		}

		// The table which contains TaskExec
		public ITaskTableBase TableBaseExec { get; set; }

		public bool fMainOrRemote; // Creating process was different via: CreateProcess or CreateRemoteThread
		public bool fCLR; // Thread's module is: CLR.dll
		public bool fNTDLL; // Thread's module is: NTDLL.dll

		public ThreadItem(in THREAD_EVENT evt) : base(evt.ThreadEvt.ProcessId, evt.tidInitiator, evt.timeStamp.ToGraphable())
		{
			// There is no special event for the thread actually beginning execution.
			// So the CreateThread event is also the StartExec event in this case.
			this.StartExec((IDVal)evt.ThreadEvt.ThreadId/*Exec*/, this.timeCreate);
#if AUX_TABLES
			this.timeRef = evt.timeStamp;
#endif // AUX_TABLES

			// tidCreate is the ID of thread which called CreateThread. tidExec is this thread executing the ThreadProc.
			AssertCritical(this.tidCreate != this.tidExec);
		}

		// Returns true if never (yet) dispatched a thread/task pool callback.
		public bool IsThreadTask => this.TaskExec == this; // Most recent TaskItem is the ThreadItem itself!

		public bool IsRundownThread => !this.timeCreate.HasValue();

		// Implement ITaskItemInfo
		public string SubTypeName => "Create"; // Thread Create
		public string StatusName => this.state.ToString();
		public QWord Identifier => (QWord)this.tidExec;
		public int Period => 0;
	}



	/*
		This class keeps track of the current known state of all threads.
	*/
	public class ThreadOracle : Dictionary<IDVal/*tid*/, ThreadItem>
	{
		public bool fHaveRundown;

		public ThreadOracle(int capacity) : base(capacity) { }

		/*
			Here we use the ThreadProc (thread start address) to help determine the thread type.

			There should be only two ThreadProc entries for Windows Thread Pool (ntdll.dll):
				1) 32-bit TppWorkerThread
				2) 64-bit TppWorkerThread
			And these addresses should be the same across ALL processes (per the bitness).

			And there should be only these ThreadProc entries for Office Task Pool (mso20*.dll):
				1) CIOPort::ThreadProc
				2) CTpWaiterThreadManagerLegacy::WaiterThreadProc
			Also, the main thread can dispatch a certain type of Office Task Pool work items at idle (*.exe):
				3+) WinMainCRTStartup
			And there should be only one bitness for Office on a device.
			So these addresses should be the same across ALL Office processes (per module, due to ASLR).
			But OfficeClickToRun and OfficeC2RClient have their own copies of the Office Task Pool code.
			And SearchIndexer has its own copy of the Windows Thread Pool dispatcher.

			So we tentatively set this modest, soft limit for the number of thread pool ThreadProcs.
		*/
		const int cThreadProcDefault = 16;

		readonly Dictionary<Addr64, ThreadClass> mpThreadProcToClass = new Dictionary<Addr64, ThreadClass>(cThreadProcDefault);


		/*
			Determine if the given ThreadProc address is known to be associated with any of the Thread Pool classes.
		*/
		public ThreadClass GetThreadProcClass(Addr64 addrThreadProc)
		{
			return this.mpThreadProcToClass.TryGetValue(addrThreadProc, out ThreadClass tc) ? tc : ThreadClass.None;
		}

		/*
			Associate the given ThreadProc address with the given Thread/Task Pool class.
		*/
		private void SetThreadProcClass(in ThreadItem ti, ThreadClass tclass)
		{
			if (this.mpThreadProcToClass.TryGetValue(ti.addrThreadProc, out ThreadClass tclassOut))
			{
				// Rarely a thread(proc) can dispatch more than one type of threadpool. Identify by priority.

				if (tclassOut >= tclass)
					return;
			}

			this.mpThreadProcToClass[ti.addrThreadProc] = tclass;

			AssertImportant(this.mpThreadProcToClass.Count <= cThreadProcDefault); // Not critical, maybe only Info, but how?
		}

		public bool IsRundownThread(IDVal tid) => this[tid].IsRundownThread;

		public ThreadItem GetThreadItem(IDVal tid) => this.TryGetValue(tid, out ThreadItem ti) ? ti : null;

		public void AddThreadItem(in ThreadItem ti)
		{
			// Sometimes a thread gets created during rundown, then appears as both types of records.
			// And the rundown queue of threads gets prepended to the event queue, so the rundown appears first.
			AssertImportant(!this.ContainsKey(ti.tidExec) || this.IsRundownThread(ti.tidExec));

			this[ti.tidExec] = ti; // overwrites without warning
		}

		public void RemoveThread(IDVal tid)
		{
			// Should have called Finish on this ThreadItem.
			AssertImportant(GetThreadItem(tid)?.state == EState.Finished);

			this.Remove(tid);
		}

		public void SetThreadPoolType(IDVal tid, ThreadClass tclass)
		{
			ThreadItem ti = this.GetThreadItem(tid);

			// This thread may have spun up before the trace started. We do thread rundown, but not on old traces.
			AssertImportant(FImplies(ti == null, !this.fHaveRundown));
			if (ti == null) return;

			AssertCritical(ti.addrThreadProc != 0);

			// We've discovered a ThreadProc associated with a Thread/Task Pool class.
			// Make the association globally.

			this.SetThreadProcClass(ti, tclass);
		}
	}


	public class ThreadTable : TaskTable<ThreadItem>
	{
		public ThreadTable(int capacity, AllTables allTables) : base(capacity, XLinkType.Thread, allTables)
		{
			this.tOracle = new ThreadOracle(capacity);
			this.CLRThreadProc = new HashSet<Addr64>(16);
			this.NTDLLThreadProc = new HashSet<Addr64>(16);
		}

		private readonly ThreadOracle tOracle;

		private readonly HashSet<Addr64> CLRThreadProc; // known CLR threadprocs
		private readonly HashSet<Addr64> NTDLLThreadProc; // known NTDLL threadprocs

		public IThreadDataSource ThreadSource; // set externally

		public void SetThreadPoolType(IDVal tid, ThreadClass tclass) => tOracle.SetThreadPoolType(tid, tclass);

		public void SetThreadRundown(bool fRundown) => tOracle.fHaveRundown = fRundown;

		public bool IsRundownThread(IDVal tid) => tOracle.IsRundownThread(tid);

		// Determine where the ThreadProc for this thread lives.
		private void SetThreadGroup(in ThreadItem tItem, in TimestampETW timeStamp)
		{
			if (NTDLLThreadProc.Contains(tItem.addrThreadProc))
			{
				tItem.fNTDLL = true;
				return;
			}
			if (CLRThreadProc.Contains(tItem.addrThreadProc))
			{
				tItem.fCLR = true;
				return;
			}

			var thread = this.ThreadSource?.GetThread(timeStamp, tItem.tidExec);
			string nameMod = thread?.StartFrame.Image?.FileName;
			if (nameMod != null)
			{
				if (nameMod.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase))
				{
					NTDLLThreadProc.Add(tItem.addrThreadProc);
					tItem.fNTDLL = true;
				}
				else if (nameMod.Equals("clr.dll", StringComparison.OrdinalIgnoreCase))
				{
					// Because of ASLR, the CLR base module _should_ live at the same address in all processes.
					CLRThreadProc.Add(tItem.addrThreadProc);
					tItem.fCLR = true;
				}
			}
		}

		/*
			Create a new ThreadItem and add it to both the Oracle (Hash) and to the List (Array).
			The Oracle hashes on the TID (tidExec) and represents the current state of the Windows Thread Manager.
		*/
		public ThreadItem AddThread(in THREAD_EVENT evt)
		{
			ThreadItem tItem = new ThreadItem(in evt)
			{
				addrThreadProc = evt.ThreadProc,

				fMainOrRemote = (evt.ThreadEvt.ProcessId != evt.pidInitiator),

// TODO: AUX_TABLES?
#if DEBUG
				pidCreate = evt.pidInitiator,
#endif // DEBUG

			};

			this.SetThreadGroup(in tItem, in evt.timeStamp); // NTDLL or CLR?

			tOracle.AddThreadItem(in tItem);

			this.Add(tItem);

			// Remember the thread creation as a StartExec (if it's not a Thread Pool thread).
			this.StartExec(this, tItem);

			return tItem;
		}


		/*
			Return the most recent, currently active thread with the given IDs, else null.
		*/
		public ThreadItem FindThreadItem(IDVal tid)
		{
			ThreadItem ti = tOracle.GetThreadItem(tid);
			AssertCritical(FImplies(ti != null, ti?.tidExec == tid));
			AssertImportant(FImplies(ti == null, !tOracle.fHaveRundown));
			return ti;
		}

		public bool FTestThreadItem(IDVal tid)
		{
			return tOracle.ContainsKey(tid);
		}


		// Returns false if this is NOT known to be a native thread/task pool thread.
		public bool IsThreadPool(in ThreadItem tItem)
		{
			AssertImportant(tItem.tidCreate != 0 || this.IsRundownThread(tItem.tidExec)); // How?
			if (tItem.fCLR) return false;
			if (!tItem.IsThreadTask)
			{
				// A thread/task pool callback has dispatched from this thread!
				AssertImportant(tOracle.GetThreadProcClass(tItem.addrThreadProc) != ThreadClass.None);
				return true;
			}
			return tOracle.GetThreadProcClass(tItem.addrThreadProc) != ThreadClass.None;
		}

		// Returns false if this is NOT an internally-created, non-thread/task-pool, non-CLR thread.
		public bool IsInternalAdHocThread(in ThreadItem tItem)
		{
			AssertImportant(tItem.tidCreate != 0 || this.IsRundownThread(tItem.tidExec)); // How?
			if (tItem.fNTDLL) return false;
			if (tItem.fCLR) return false;
			if (tItem.fMainOrRemote) return false;
			if (tItem.IsRundownThread) return false; // Probably not a thread we want, and not useful in any case.
			if (!tItem.IsThreadTask)
			{
				// A thread/task pool callback has dispatched from this thread!
				AssertImportant(tOracle.GetThreadProcClass(tItem.addrThreadProc) != ThreadClass.None);
				return false;
			}
			return tOracle.GetThreadProcClass(tItem.addrThreadProc) == ThreadClass.None;
		}


		public void StartExec(ITaskTableBase tableBase, TaskItem task)
		{
			ThreadItem ti = tOracle.GetThreadItem(task.tidExec);
			AssertImportant(FImplies(tOracle.fHaveRundown, ti != null));
			if (ti == null) return;

			TaskItem taskExec = ti.TaskExec;
			if (taskExec != null)
			{
				AssertImportant(taskExec.timeStartExec <= task.timeStartExec);

				// There is an open task associated with creating the thread.
				// Close that task when a real task begins.
				if (ti.IsThreadTask && taskExec.timeEndExec.HasMaxValue())
				{
					AssertImportant(taskExec.state == EState.StartExec);
					taskExec.EndExec(in task.timeStartExec);
				}
			}
			ti.TaskExec = task;
			ti.TableBaseExec = tableBase;
		}

		public TaskItem GetExec(IDVal tid)
		{
			ThreadItem ti = tOracle.GetThreadItem(tid);
			AssertImportant(ti != null);
			return ti?.TaskExec;
		}


		public static readonly Guid guid = new Guid("{3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c}"); // Thread


		public void Dispatch(in THREAD_EVENT evt, bool fTarget)
		{
			ThreadItem tItem;

			switch ((TEID)evt.opEvent)
			{
			case TEID.Create:
				// Feed the Thread Oracle with every thread we can find.
			/*
				A thread can be created while the rundown is happening.
				Then there will be a Thread Create event followed by a Rundown event.
				But the Thread pre-dispatcher queues all of the Rundown events before the Create events.
				If that happens here, ignore this thread's Rundown event/record.
				Also, thread events can duplicate in a merged trace: xperf/wpr -merge ...
			*/
				if (FTestThreadItem(evt.ThreadEvt.ThreadId) && !IsRundownThread(evt.ThreadEvt.ThreadId))
					break;

				tItem = AddThread(in evt);

				// Only thread together the stacks if this process has network stalkwalks of interest.
				if (fTarget && this.IsInternalAdHocThread(in tItem))
				{
					// Who created this thread? (It wasn't an external process.)
					// We may discard (Unlink) this info later if this is determined to be a Thread/Task Pool thread.
					tItem.stack = this.allTables.stackSource.GetStack(evt.timeStamp, evt.tidInitiator);
					tItem.xlink.GetLink(tItem.tidCreate, in tItem.timeCreate, this.allTables.threadTable);
					if (tItem.xlink.HasValue)
						AssertImportant(tItem.xlink.taskLinkNext.pid == tItem.pid);
				}
				break;

			case TEID.Rundown:
				// Feed the Thread Oracle with every thread we can find.
				// (Thread events can duplicate in a merged trace: xperf/wpr -merge ...)
				AssertImportant(!FTestThreadItem(evt.ThreadEvt.ThreadId));
				AddThread(in evt);
				break;

			case TEID.Exit:
				AssertCritical(evt.pidInitiator == evt.ThreadEvt.ProcessId);
				AssertCritical(evt.tidInitiator == evt.ThreadEvt.ThreadId);
			/*
				A thread can be destroyed while the rundown is happening.
				In that case there may not be a ThreadItem to remove.
			*/
				tItem = tOracle.GetThreadItem(evt.ThreadEvt.ThreadId);
				AssertInfo(tItem != null);

				if (tItem != null)
				{
					AssertImportant(tItem.pid == evt.ThreadEvt.ProcessId);

					TimestampUI timeStamp = evt.timeStamp.ToGraphable();
					tItem.EndExec(in timeStamp);
					Finish(tItem, in timeStamp);

					tOracle.RemoveThread(evt.ThreadEvt.ThreadId);
				}
				break;
			}
		}
	}
} // NetBlameCustomDataSource.Thread

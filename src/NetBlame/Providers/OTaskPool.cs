// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

using Microsoft.Windows.EventTracing.Events;

using NetBlameCustomDataSource.Tasks;

using static NetBlameCustomDataSource.Util;

using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using IDVal = System.Int32; // type of Event.pid/tid / ideally: System.UInt32
using QWord = System.UInt64;

/*
	TIMER PATTERNS
	#1
		T1 *WorkerCreate typeTimer
		T1 *TimerSubmit
		T2 TimerFired
		T2 WorkerSubmit
		T3 WorkerStartExec
		T3 WorkerEndExec
		T3 WorkerDestroy
	#2
		T1 *WorkerCreate typeTimer
		T1 *TimerSubmit
		T2 TimerFired
		T2 WorkerSubmit
		T3 WorkerStartExec
		T3 TimerSubmit [same ID] // Resubmit, with call stack.
		T3 WorkerEndExec
	#3
		T1 *WorkerCreate typeTimer
		T1 *TimerSubmit
		T2 TimerSubmit * X
		T3 TimerCancel
		T3 WorkerCancel * Y
		T3 WorkerDestroy
	#4
		T1 *WorkerCreate typeGroupTimer
		T1 *TimerSubmit
		T2 TimerCancel
		T2 WorkerCancel
		T2 TimerSubmit
		T3 ...Repeat TimerCancel...
		
	* May occur before tracing starts! Also, these have the only useful call stacks.

	WAITER PATTERNS
		NOTE: After the original WorkerCreate/WaiterSubmit there are no useful stacks!
	#1
		T1 WorkerCreate typeWaiter // may occur before trace
		T1 WaiterSubmit            // may occur before trace
		T2 WaiterFired
		T2 WorkerSubmit
		T3 WorkerStartExec
		T3 WaiterSubmit // Resubmit
		T3 WorkerEndExec
		T2 ...Repeat - WaiterFired...
	#2
		T1 WorkerCreate typeWaiter // may occur before trace
		T1 WaiterSubmit            // may occur before trace
		T2 WaiterFired
		T2 WorkerStartExec
		T2 WaiterSubmit // Resubmit
		T2 WorkerEndExec
		T2 ...Repeat - WaiterFired...
	#3
		T1 WorkerCreate typeWaiter
		T1 WaiterSubmit
		T1 WaiterCancel
		T1 WorkerCancel
		T1 WaiterCancel
		T1 WorkerCancel
		T1 WorkerDestroy
	#4
		T1 WaiterSubmit // No WorkerCreate!
		T2 WaiterCancel
*/

namespace NetBlameCustomDataSource.OTaskPool
{
	// See eWorkerType* in ..\liblet\threadpool\inc\work.h
	public enum EWorker
	{
	    typeWorker = 1,
	    typeWaiter = 2,
	    typeTimer = 3,
	    typeGroup = 4,
	    typeGroupWorker = 5,
	    typeGroupTimer = 6,
	    typeGroupWaiter = 7,
	    typeReservation = 8,
	};


	public class OTaskPoolWorker : TaskItem, ITaskItemInfo
	{
		public QWord qwWorker;
#if DEBUG
		public QWord qwCallback;
#endif // DEBUG
#if AUX_TABLES
		public int cmsPeriod; // recurring if > 0
#endif // AUX_TABLES

		public EWorker type;

		public OTaskPoolWorker(QWord qwWorker, EWorker type, IDVal pid, IDVal tid, in TimestampUI timeStamp)
				: base(pid, tid, in timeStamp)
		{
			this.qwWorker = qwWorker;
			this.type = type;
		}

		// Implement ITaskItemInfo
		public string SubTypeName => this.type.ToString();
		public string StatusName => this.state.ToString();
		public QWord Identifier => this.qwWorker;
		public int Period => 0;
	} // OTaskPoolWorker


	public class OTaskPoolTable : TaskTable<OTaskPoolWorker>
	{
		public OTaskPoolTable(int capacity, in AllTables _allTables) : base(capacity, Link.XLinkType.OTaskPool, _allTables) {}

		public static readonly Guid guid = new Guid("{a019725f-cff1-47e8-8c9e-8fe2635b6388}"); // Microsoft-Office-ThreadPool

		// However, it is not uncommon that the manifest for this provider is missing or out-of-date.
		// See OfficeVSO/4572384
		private bool fCancelAll;


		/*
			Return the most recent worker object with the given object ID and time range.
			Allow the caller to specify the object state.
		*/
		OTaskPoolWorker FindWorker(QWord qwWorker, in TimestampUI timeStamp, EState state = EState.None)
		{
			for (int iWorker = this.Count-1; iWorker >= 0; --iWorker)
			{
				OTaskPoolWorker worker = this[iWorker];
				if (worker.qwWorker == qwWorker)
				{
					if (state != EState.None && state != worker.state)
						continue;

					if (timeStamp.Between(in worker.timeCreate, in worker.timeDestroy))
						return worker;
				}
			}
			return null;
		}

		// From etwtp.man
		public enum OTP
		{
			// tidWorkerTypeInfo //
			WorkObjectCreate = 32,
			// tidWorkerInfo //
			WorkObjectDestroy = 33,
			WorkerSubmit = 34,
			WorkerCancel = 37,
			WaiterCancel = 38,
			TimerCancel = 39,
			TimerFired = 52,
			// tidWaiterSubmitInfo //
			WaiterSubmit = 35,
			// tidWorkerStartExec //
			WorkerStartExec = 40,
			// tidWorkerEndExec //
			WorkerEndExec = 41,
			// tidWaiterFired //
			WaiterFired = 51,
			// tidBoolInfo //
			IdleSubmitWorker = 57,
			IdleStartProcessItem = 58,
			// tidTimerSubmitInfo //
			TimerSubmit = 62,
			// tidNoArgs //
			IdleEndProcessItem = 100,
		}

// TODO: Do we need to pre-parse the old versions of WorkObjectCreate, etc.?  See: OTaskPool.Classic.cs

		public void Dispatch(in IGenericEvent evt)
		{
			QWord qwWorker;
			OTaskPoolWorker otpWorker;
			TimestampUI timeStamp;

			if (this.fCancelAll) return;

			try
			{
				switch ((OTP)evt.Id)
				{
				case OTP.WorkObjectCreate:
					timeStamp = evt.Timestamp.ToGraphable();
					qwWorker = evt.GetAddrValue("worker");
					EWorker type = (EWorker)evt.GetUInt32("workerType");
					AssertImportant(FindWorker(qwWorker, timeStamp) == null);
					otpWorker = new OTaskPoolWorker(qwWorker, type, evt.ProcessId, evt.ThreadId, in timeStamp);
					GetXLink(otpWorker);
					otpWorker.stack = evt.Stack;
#if AUX_TABLES
					otpWorker.timeRef = evt.Timestamp;
#endif // AUX_TABLES
#if DEBUG
					otpWorker.qwCallback = evt.GetAddrValue("callback");
#endif // DEBUG
					Add(otpWorker);
					break;

				case OTP.WorkObjectDestroy:
					timeStamp = evt.Timestamp.ToGraphable();
					qwWorker = evt.GetAddrValue("pointer");
					otpWorker = FindWorker(qwWorker, in timeStamp);
					if (otpWorker != null)
						Finish(otpWorker, in timeStamp);
					break;

				case OTP.TimerSubmit:
					timeStamp = evt.Timestamp.ToGraphable();
					qwWorker = evt.GetAddrValue("pointer");
					otpWorker = FindWorker(qwWorker, timeStamp);
					if (otpWorker == null)
					{
						// The Create event must have happened before the trace started.
						// But this call stack is still valuable, so make a pseudo-create record.
						otpWorker = new OTaskPoolWorker(qwWorker, EWorker.typeTimer, evt.ProcessId, evt.ThreadId, in timeStamp);
						this.Add(otpWorker);
					}

					if (otpWorker.stack == null && evt.Stack != null)
					{
						otpWorker.stack = evt.Stack;
						ReGetXLink(otpWorker);
					}
#if AUX_TABLES
					if (!otpWorker.timeRef.HasValue)
						otpWorker.timeRef = evt.Timestamp;

					otpWorker.cmsPeriod = (int)evt.GetUInt32("period");
#endif // AUX_TABLES
					break;

				case OTP.WorkerStartExec:
					// Ensure that this thread is marked as OTaskPool.
					this.allTables.threadTable.SetThreadPoolType(evt.ThreadId, Thread.ThreadClass.OTaskPool);

					timeStamp = evt.Timestamp.ToGraphable();
					qwWorker = evt.GetAddrValue("worker");
					otpWorker = FindWorker(qwWorker, in timeStamp);
					AssertInfo(otpWorker != null);
					if (otpWorker != null)
					{
						// This Worker may have been resubmitted: StartExec -> EndExec -> StartExec -> EndExec
						AssertCritical(otpWorker.state == EState.Created || otpWorker.state == EState.EndExec);

						otpWorker.StartExec(evt.ThreadId, in timeStamp);

						// Remember the most recent StartExec on this thread.
						this.allTables.threadTable.StartExec(this, otpWorker);
					}
					break;

				case OTP.WorkerEndExec:
					timeStamp = evt.Timestamp.ToGraphable();
					qwWorker = evt.GetAddrValue("worker");
					// Timer/Waiter Pattern #2: skip over a resubmitted timer/waiter.
					otpWorker = FindWorker(qwWorker, in timeStamp, EState.StartExec);
					AssertInfo(otpWorker != null);
					if (otpWorker != null)
					{
						AssertCritical(otpWorker.state == EState.StartExec);
						AssertImportant(otpWorker.tidExec == evt.ThreadId);

						otpWorker.EndExec(in timeStamp);
					}
					break;
				}
			}
			catch
			{
				// Drop it.
				// This may throw (when invoking evt.Fields) if collected on a machine with an older Office16 (<=1803) or no/wrong registration for Microsoft-Office-ThreadPool.

				this.fCancelAll = true;
			}
		} // Dispatch
	} // OTaskPoolTable
} // NetBlameCustomDataSource.OTaskPool
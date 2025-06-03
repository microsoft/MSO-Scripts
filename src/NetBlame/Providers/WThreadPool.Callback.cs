// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using NetBlameCustomDataSource.Tasks;
using NetBlameCustomDataSource.WThreadPool.Classic;

using static NetBlameCustomDataSource.Util; // Assert*

using Addr32 = System.UInt32;
using Addr64 = System.UInt64;
using IDVal = System.Int32; // Process/ThreadID (ideally UInt32)
using QWord = System.UInt64;


/*
	The usual pattern is this: Enqueue->Dequeue->Start->Stop, where only the Enqueue may be on a different thread.
		Time1, Callback_Enqueue, Thread1, PoolId1, TaskId1, Context1, CallStack Available
		Time2, Callback_Dequeue, Thread2, PoolId1, TaskId1, Context1
		Time3, Callback_Start,   Thread2, PoolId1, TaskId1, Context1
		// Interesting work-related events happen here.
		Time4, Callback_Stop,    Thread2, PoolId1, TaskId1, Context1

	But it can get really squirrely, with the "same" task executing on multiple threads!
		Time1, Callback_Start, Thread1, PoolId1, TaskId1, Context1
		Time2, Callback_Start, Thread2, PoolId1, TaskId1, Context1
		Time3, Callback_Stop,  Thread1, PoolId1, TaskId1, Context1
		Time4, Callback_Stop,  Thread2, PoolId1, TaskId1, Context1

	Or this, where one thread enqueues the same task multiple times:
		Time1, Callback_Enqueue, Thread1, PoolId1, TaskId1, Context1, CallStack Available
		Time2, Callback_Dequeue, Thread2, PoolId1, TaskId1, Context1
		Time3, Callback_Start,   Thread2, PoolId1, TaskId1, Context1
		Time4, Callback_Enqueue, Thread1, PoolId1, TaskId1, Context1, CallStack Available
		Time5, Callback_Dequeue, Thread3, PoolId1, TaskId1, Context1
		Time6, Callback_Start,   Thread3, PoolId1, TaskId1, Context1
		Time7, Callback_Stop,    Thread3, PoolId1, TaskId1, Context1
		Time8, Callback_Stop,    Thread2, PoolId1, TaskId1, Context1

	We even see this, where nested callbacks happen on the same thread (different pools):
		Time1, Callback_Enqueue, Thread1, PoolId1, TaskId1, Context1, CallStack Available
		Time2, Callback_Dequeue, Thread1, PoolId1, TaskId1, Context1
		Time3, Callback_Start,	 Thread1, PoolId1, TaskId1, Context1
		Time4, Callback_Start,	 Thread1, PoolId2, TaskId2, Context2 // PoolId2==0 - Kernel event
		Time5, Callback_Stop,	 Thread1, PoolId2, TaskId2, Context2
		Time6, Callback_Stop,	 Thread1, PoolId1, TaskId1, Context1

	And sometimes there's this double-dequeue:
		Time1, Callback_Enqueue, Thread1, PoolId1, TaskId1, Context1, CallStack Available
		Time2, Callback_Dequeue, Thread2, PoolId1, TaskId1, Context1
		Time3, Callback_Dequeue, Thread2, PoolId1, TaskId1, Context1 // PoolId1==0 - Kernel event
		Time4, Callback_Start,	 Thread2, PoolId1, TaskId1, Context1
		Time5, Callback_Stop,	 Thread2, PoolId1, TaskId1, Context1
	This is because RtlpTpWorkCallback may call RtlTpETWCallbackDequeue and sometimes RtlpTpWorkUnposted (Dequeue).

	Or early re-enqueue (see TppWorkpExecuteCallback):
		Time1, Callback_Enqueue, Thread1, PoolId1, TaskId1, Context1, CallStack Available
		Time2, Callback_Dequeue, Thread2, PoolId1, TaskId1, Context1
		Time3, Callback_Enqueue, Thread2, PoolId1, TaskId1, Context1, CallStack Available
		Time4, Callback_Start,	 Thread2, PoolId1, TaskId1, Context1
		Time5, Callback_Stop,	 Thread2, PoolId1, TaskId1, Context1

	Or orphaned dequeue (see TppWorkUnposted):
		Time1, Callback_Enqueue, Thread1, PoolId1, TaskId1, Context1, CallStack Available
		Time2, Callback_Dequeue, Thread1, PoolId1, TaskId1, Context1
		Time4, Callback_Start,	 Thread1, PoolId1, TaskId2, Context1
		Time5, Callback_Stop,	 Thread1, PoolId1, TaskId2, Context1

	The Callback_Dequeue/Start/Stop ETW events are logged from some or all of these routines:
		*TppWorkpExecuteCallback     CreateSetThreadpoolWork TP_WORK (base)
		*TppTimerpExecuteCallback    Create/SetThreadpoolTimer TP_TIMER (wait for a timer)
		*TppSimplepExecuteCallback   WinsockThreadpool_SubmitWork (internal)
		*TppExecuteWaitTimerCallback From the internal timer's work item to execute a timeout callback.
		-TppAlpcpExecuteCallback     TppAllocAlpcCompletion (internal)
		-TppJobpExecuteCallback      SetInformationJobObject TP_JOB, TP_DIRECT completion object
		-TppIopExecuteCallback       Create/SetThreadpoolIo TP_IO
		-TppWaitCompletion           When the wait handle of a TP_WAIT object becomes signalled (internal)
		~TppWorkUnposted             TP_WORK's TP_POOL is freed while the TP_WORK is in the pool's queue

	* These routines do: Callback_Dequeue, Callback_Start, Callback_Stop
	- These routines do: Callback_Start, Callback_Stop
	~ These routines do: Callback_Dequeue

	Unfortunately the callbacks don't give a direct indication of what kind of work item it is.
	But we get an idea how the squirreliness happens by imagining the behavior of some of the items:

	(eg. ALPC - Async Local Procedure Call, IO Completion Port signaling, Job Object Notifications, ...).

	+ Here are the routines which enqueue a callback:
		TppWorkPost // Posts a TP_WORK object.
		TppWorkCallbackPrologRelease // if (fReinsert) ...

	~ Here are the routines that cancel a callback:
		TppWorkCancelPendingCallbacks via TppWorkCallbackProlog, TppWorkCallbackPrologRelease
		TppIopCancelPendingCallbacks
		TpWaitForIoCompletion

	* Here is a standard *ExecuteCallback routine:

		TppWorkpExecuteCallback(...)
		{
			if (IS_THREAD_POOL_LOGGING_ENABLED())
				TppETWCallbackDequeue(...); // *** Callback_Dequeue ***

			if (!TppWorkCallbackProlog(...)) // INLINE
				{
				...
				if (fCancelCallbacks)
					TppWorkCancelPendingCallbacks(...); *** Callback_Cancel ***

				if (fReinsert)
					TppEtwCallbackEnqueue(...); *** Callback_Enqueue ***
				...
				return;
				}

			if (IS_THREAD_POOL_LOGGING_ENABLED())
				TppETWCallbackStart(...); // *** Callback_Start ***

			TppStartThreadData(...); // thread data stats collection (internal interfaces)

			Work->WorkCallback(...); // *** QUEUED WORK HAPPENS HERE ***

			if (IS_THREAD_POOL_LOGGING_ENABLED())
				TppETWCallbackStop(...); // *** Callback_Stop ***

			TppCompleteThreadData(...);
		}

	Strangely, there's another set of ThreadPool functions (for "the original NT threadpool") that uses the same ETW instrumentation,
	and RtlpTpWorkCallback is the only one that follows the pattern : Dequeue->Start->Stop.
		RtlpTpETWCallbackEnqueue // v2 - RtlQueueWorkItem
		RtlTpETWCallbackDequeue  // v3 - RtlpTpWorkCallback, RtlTpWorkUnposted
		RtlTpETWCallbackStart	 // v2 - RtlpTpWorkCallback, RtlpTpIoCallback, RtlpTpTimerCallback, RtlpTpWaitCallback
		RtlTpETWCallbackStop	 // v3 - RtlpTpWorkCallback, RtlpTpIoCallback, RtlpTpTimerCallback, RtlpTpWaitCallback
*/

namespace NetBlameCustomDataSource.WThreadPool.Callback
{
	public class WTPCallback : TaskItem, ITaskItemInfo
	{
		public readonly QWord idPool;
		public readonly QWord idTask;

#if DEBUG
		// Extra validation that these match when they should.
		public QWord qwFunction;
		public QWord qwContext;
#endif // DEBUG

		public IDVal tidDQ;

		public bool fStopHere; // TODO: Is this optimization still useful?

		public WTP status; // TODO: Use base.state ?


		public WTPCallback(in AltClassicEvent evt)
				: base(evt.idProcess, evt.idThread, evt.timeStamp.ToGraphable())
		{
			// TODO: Is this required?  Leave them at Zero.
			//	this.timeStartExec.SetMaxValue();
			//	this.timeEndExec.SetMaxValue();
			if (evt.F32Bit)
			{
				var evt32 = (THREAD_POOL_EVENT<Addr32>)evt;
				this.idPool = evt32.ThreadPoolEvt.PoolId;
				this.idTask = evt32.ThreadPoolEvt.TaskId;
#if DEBUG
				this.qwFunction = evt32.ThreadPoolEvt.CallbackFunction;
				this.qwContext = evt32.ThreadPoolEvt.CallbackContext;
#endif // DEBUG
			}
			else
			{
				var evt64 = (THREAD_POOL_EVENT<Addr64>)evt;
				this.idPool = evt64.ThreadPoolEvt.PoolId;
				this.idTask = evt64.ThreadPoolEvt.TaskId;
#if DEBUG
				this.qwFunction = evt64.ThreadPoolEvt.CallbackFunction;
				this.qwContext = evt64.ThreadPoolEvt.CallbackContext;
#endif // DEBUG
			}
#if AUX_TABLES
			this.timeRef = evt.timeStamp;
#endif // AUX_TABLES
		} // .ctor

		// Implement ITaskItemInfo
		public string SubTypeName => "Worker";
		public string StatusName => this.status.ToString();
		public QWord Identifier => this.idTask;
		public int Period => 0;
	} // WTPCallback


	public class WTPCallbackTable : TaskTable<WTPCallback>
	{
		public WTPCallbackTable(int capacity, in AllTables _allTables) : base(capacity, Link.XLinkType.WThreadPool, _allTables) {}


		WTPCallback FindCallbackByTask(QWord idPool, QWord idTask, IDVal tid, WTP status)
		{
#if DEBUG
			bool fCanExitEarly = false;
#endif // DEBUG
			WTPCallback wcbFound = null;
			for (int iWcb = this.Count-1; iWcb >= 0; --iWcb)
			{
				WTPCallback wcb = this[iWcb];

				if (wcb.status == status)
				{
					if (wcb.idTask == idTask && wcb.idPool == idPool)
					{
						// The current status of this record is what we're looking for.
#if DEBUG
						AssertCritical(!fCanExitEarly); // Would have missed this by taking the early exit!
#endif // DEBUG
						if (status == WTP.CallbackEnqueue)
						{
							// Looking for a callback record in the Enqueue state.
							// That means that this must be a for a Dequeue event.
							// If it happens to be on the same thread (not necessary) then take it.

							if (wcb.tidCreate == tid)
							{
								wcbFound = wcb;
								break;
							}

							// If it's not on the same thread then take the LAST matching record.
							// NOTE: If this ever fires then there were multiple enqueues of this record that happened on a different thread.
							// NOTE: That's not disallowed, I imagine; just make sure that we handle it properly.
							// NOTE: But if this NEVER fires then the thread check (above) is unnecessary, so always break immediately.
							AssertImportant(wcbFound == null);

							wcbFound = wcb;
							continue;
						}
						else
						{
							// Looking for a callback record in the Dequeue or Start state.
							// The Dequeue, Start, Stop events should all happen on the same thread.

							if (wcb.tidDQ == tid)
							{
								wcbFound = wcb;
								break;
							}
						}
					}
					else
					{
						// The current status of this record is not what we're looking for.
						// This is normal...within certain bounds.
					}
				}
				else if (wcb.status >= WTP.CallbackStop) // or Cancel
				{
					if (wcb.fStopHere)
						break;

					// Optimization: Determine if all subsequent records are status==STOP.
					if (iWcb == 0 || this[iWcb-1].fStopHere)
					{
						wcb.fStopHere = true;
						break;
					}

					// A closed record with the same ID on the same thread? No more to search.
					if (wcb.idTask == idTask && wcb.idPool == idPool && wcb.tidDQ == tid)
					{
#if DEBUG
						fCanExitEarly = true;
#else // !DEBUG
						break;
#endif // !DEBUG
					}
				}
				else if (wcb.tidDQ == tid)
				{
					AssertImportant(wcb.status == WTP.CallbackDequeue || wcb.status == WTP.CallbackStart);

					if (idPool != 0 && wcb.status == WTP.CallbackDequeue)
					{
						// Orphaned dequeue:
						wcb.status = WTP.CallbackCancel;
					}
					else
					{
						AssertInfo(idPool == 0);
					}

					// This thread either started doing something, didn't finish, and now is doing something else (!?).
					// Or there's a strange double-dequeue event.
					// Or it's doing work which caused an event from another threadpool to fire on this thread.
					// How is that possible, you ask!?	It could be a non-enqueuing event such as: ALPC, Completion Port, Job Object, ...
					// This weirdness generally happens when idPool == 0 (kernel threadpool?).
				}
			} // for iWcb

			return wcbFound;
		}

		WTPCallback FindCallbackByTask(in AltClassicEvent evt, in EventPayload<Addr32> payload, WTP status)
		{
			WTPCallback wcb = FindCallbackByTask(payload.PoolId, payload.TaskId, evt.idThread, status);
			if (wcb == null) return null;
#if DEBUG
			AssertImportant(wcb.qwFunction == payload.CallbackFunction);
			AssertImportant(wcb.qwContext == payload.CallbackContext);
#endif // DEBUG
			return wcb;
		}

		WTPCallback FindCallbackByTask(in AltClassicEvent evt, in EventPayload<Addr64> payload, WTP status)
		{
			WTPCallback wcb = FindCallbackByTask(payload.PoolId, payload.TaskId, evt.idThread, status);
			if (wcb == null) return null;
#if DEBUG
			AssertImportant(wcb.qwFunction == payload.CallbackFunction);
			AssertImportant(wcb.qwContext == payload.CallbackContext);
#endif // DEBUG
			return wcb;
		}

		/*
			For Dequeue, StartExec, EndExec, which cast to: THREAD_POOL_EVENT<>
		*/
		WTPCallback FindCallbackByTask(in AltClassicEvent evt, WTP status)
		{
			if (evt.F32Bit)
				return FindCallbackByTask(in evt, in ((THREAD_POOL_EVENT<Addr32>)evt).ThreadPoolEvt, status);
			else
				return FindCallbackByTask(in evt, in ((THREAD_POOL_EVENT<Addr64>)evt).ThreadPoolEvt, status);
		}

		WTPCallback FindCallbackByTask2(in AltClassicEvent evt, WTP status)
		{
			if (evt.F32Bit)
				return FindCallbackByTask(in evt, in ((THREAD_POOL_CANCEL<Addr32>)evt).ThreadPoolCancel.ThreadPoolEvt, status);
			else
				return FindCallbackByTask(in evt, in ((THREAD_POOL_CANCEL<Addr64>)evt).ThreadPoolCancel.ThreadPoolEvt, status);
		}

		public void Enqueue(in AltClassicEvent evt)
		{
			WTPCallback wcb = new WTPCallback(in evt)
			{
				status = WTP.CallbackEnqueue,

				// TODO: It's likely this object will be GC'd, and this stack lookup is wasted.
				// Do this in the Gather phase, and stash the TimestampETW?
				// Else: wcb.GetStack(evt.idThread, evt.timeStamp)
				stack = this.allTables.stackSource?.GetStack(evt.timeStamp, evt.idThread)
			};
			GetXLink(wcb);
			Add(wcb);
		}

		public void Dequeue(in AltClassicEvent evt)
		{
			WTPCallback wcb = FindCallbackByTask(in evt, WTP.CallbackEnqueue);
			if (wcb == null) return;

			// TODO: wcb.Fire(...)? where state.Fire==state.Dequeue
			wcb.tidDQ = evt.idThread;
			wcb.status = WTP.CallbackDequeue;
		}

		public void StartExec(in AltClassicEvent evt)
		{
			WTPCallback wcb = FindCallbackByTask(in evt, WTP.CallbackDequeue);
			if (wcb == null) return;

			wcb.status = WTP.CallbackStart;
			wcb.StartExec(evt.idThread, evt.timeStamp.ToGraphable());

			// Remember the most recent StartExec on this thread.
			this.allTables.threadTable.StartExec(this, wcb);
		}

		public void EndExec(in AltClassicEvent evt)
		{
			WTPCallback wcb = FindCallbackByTask(in evt, WTP.CallbackStart);
			if (wcb == null) return;

			wcb.status = WTP.CallbackStop;

			wcb.EndExec(evt.timeStamp.ToGraphable());

			// Done now. There's no other Destroy event.
			Finish(wcb);
		}

		public void Cancel(in AltClassicEvent evt)
		{
			// A common pattern is: Enqueue, Cancel, Dequeue.
			// And once it's started, it can't be canceled.

			AssertImportant(FindCallbackByTask2(in evt, WTP.CallbackStart) == null);

			WTPCallback wcb = FindCallbackByTask2(in evt, WTP.CallbackEnqueue);

			wcb ??= FindCallbackByTask2(in evt, WTP.CallbackDequeue);

			if (wcb == null)
				return;

			wcb.status = WTP.CallbackCancel; // TODO: redundant?

			Finish(wcb);
		}
	} // WTPCallback
} // NetBlameCustomDataSource.WThreadPool.Callback
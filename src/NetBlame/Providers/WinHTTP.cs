// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

using Microsoft.Windows.EventTracing.Events;

using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using NetBlameCustomDataSource.Tasks;
using static NetBlameCustomDataSource.Util; // Assert*

using QWord = System.UInt64;
using IDVal = System.Int32; // type of Event.pid/tid / ideally: System.UInt32
using System.Collections.Generic;


namespace NetBlameCustomDataSource.WinHTTP
{
/**************************************************************************************
	These events originate from two different providers, which have their unique patterns:
		Microsoft-Windows-WebIO and Microsoft-Windows-WinHttp

	We should NEVER dispatch:
		Microsoft-Windows-WebIO.Timer
		Microsoft-Windows-WinHttp.OverlappedIO

	Also, all -WinHttp.Timer events are Cancel events.

	The usual patterns are these:

	1)	Thread1	ThreadAction_Queue Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread2	ThreadAction_Start Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread2 ThreadAction_Stop  Ctx1 ActId1 [WorkItem | WaitCallback]

	2)	Thread1 ThreadAction_Queue  Ctx1 ActId1 WaitCallback
		Thread2 ThreadAction_Cancel Ctx1 ActId1 Timer        // Canceled

	3)	Thread1 ThreadAction_Queue  Ctx1 ActId1 WaitCallback
		Thread2 ThreadAction_Start  Ctx1 ActId1 WaitCallback
		Thread2 ThreadAction_Cancel Ctx1 ActId1 Timer        // NOT Canceled, just Ended
		Thread2 ThreadAction_Stop   Ctx1 ActId1 WaitCallback

	Apparently a WorkItem or WaitCallback can get requeued by the callback.
	This can create long chains of requeues.

	4)	Thread1 ThreadAction_Queue Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread2 ThreadAction_Start Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread2 ThreadAction_Queue Ctx1 ActId1 [WorkItem | WaitCallback] // Requeue? or Reuse
		Thread2 ThreadAction_Stop  Ctx1 ActId1 [WorkItem | WaitCallback]

	4a)	Thread1 ThreadAction_Queue Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread2 ThreadAction_Start Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread2 ThreadAction_Queue Ctx1 ActId1 [WorkItem | WaitCallback] // Requeue? or Reuse
		Thread2 ThreadAction_Stop  Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread3 ThreadAction_Start Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread3 ThreadAction_Stop  Ctx1 ActId1 [WorkItem | WaitCallback]

	4b)	Thread1 ThreadAction_Queue Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread2 ThreadAction_Start Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread2 ThreadAction_Queue Ctx1 ActId1 [WorkItem | WaitCallback] // Requeue? or Reuse
		Thread3 ThreadAction_Start Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread2 ThreadAction_Stop  Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread3 ThreadAction_Stop  Ctx1 ActId1 [WorkItem | WaitCallback]

	4c)	Thread1 ThreadAction_Queue Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread2 ThreadAction_Start Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread2 ThreadAction_Queue Ctx1 ActId1 [WorkItem | WaitCallback] // Requeue? or Reuse
		Thread3 ThreadAction_Start Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread3 ThreadAction_Stop  Ctx1 ActId1 [WorkItem | WaitCallback]
		Thread2 ThreadAction_Queue Ctx1 ActId1 [WorkItem | WaitCallback] // Enqueue!
		Thread2 ThreadAction_Stop  Ctx1 ActId1 [WorkItem | WaitCallback]

	4d) etc.

	But the context (Ctx1) is released and may be reused (by another thread) before logging the Stop event.

	5)	Thread1	ThreadAction_Queue Ctx1 ActId1 WorkItem
		Thread2	ThreadAction_Start Ctx1 ActId1 WorkItem
		Thread3	ThreadAction_Queue Ctx1 ActId1 WorkItem // Ctx1 is already released and being reused!
		Thread2 ThreadAction_Stop  Ctx1 ActId1 WorkItem

	And the context (Ctx1) is enqueued and may be dispatched before logging the Queue event.

	6a)	Thread2 ThreadAction_Start Ctx1 ActId1 [WorkItem | Overlapped IO] // Enqueue of Ctx1 already happened on Thread1
		Thread1 ThreadAction_Queue Ctx1 ActId1 [WorkItem | Overlapped IO] // Thread1 switches back in and logs to ETW!
		Thread2 ThreadAction_Stop  Ctx1 ActId1 [WorkItem | Overlapped IO]

		[Start of Trace!?]
	6b)	Thread2 ThreadAction_Start Ctx1 ActId1 [WorkItem | Overlapped IO] // Enqueue of Ctx1 already happened on Thread1
		Thread2 ThreadAction_Stop  Ctx1 ActId1 [WorkItem | Overlapped IO]
		Thread1 ThreadAction_Queue Ctx1 ActId1 [WorkItem | Overlapped IO] // Thread1 switches back in and finally logs to ETW!

	This case (6) is PROBLEMATIC and surprisingly common!

 *************************************************************************************/

	enum ThreadAction
	{
		// Level 5:
		Cancel = 59995,
		Queue = 59996,
		Stop = 59997,
		Start = 59998,
		//
		First = Cancel,
		Last = Start
	};

	public enum ActionType : byte
	{
		actionNone = 0,
		Timer = 1,
		WorkItem = 2,
		OverlappedIO = 3,
		WaitCallback = 4,
		actionMax
	};


	public class CallbackEvent : TaskItem, ITaskItemInfo
	{
		public QWord qwContext;
		public uint iNext; // 1-based index to next CallbackEvent instance with the same qwContext
		public int actHash;
		public ActionType actType;

		public CallbackEvent(QWord qwContext, int actHash, ActionType action, IDVal pid, IDVal tid, TimestampUI timeStamp)
				: base(pid, tid, timeStamp)
		{
			this.qwContext = qwContext;
			this.actHash = actHash;
			this.actType = action;
			this.state = EState.Created;
		}

		public bool FQueued => this.tidCreate != 0;
		public bool FStarted => this.tidExec != 0;
		public bool FOnlyCreated => this.state == EState.Created;
		public bool FOnlyStarted => this.state == EState.StartExec;
		public bool FStopped => this.state == EState.EndExec;
		public bool FCanceled => this.state == EState.Canceled;
		public bool FInverted => this.timeCreate == this.timeStartExec; // case 6

		// Implement ITaskItemInfo
		public string SubTypeName => this.actType.ToString();
		public string StatusName => this.state.ToString();
		public QWord Identifier => this.qwContext;
		public int Period => 0;
	}

	public class WinHttpTable : TaskTable<CallbackEvent>
	{
		// 1-based index to head (most recent) of a chain of CallbackEvent elements with that same Context (linked by index).
		private Dictionary<QWord, uint> hashContext;

		public WinHttpTable(int capacity, AllTables _allTables) : base(capacity, Link.XLinkType.WinHTTP, _allTables)
		{
			this.hashContext = new Dictionary<QWord, uint>(capacity / 4); // empirical

			// The hash table and linked lists (by Context) are indexed, so disable GC.
			this.FEnableGC = false;
		}

		private uint IFromContext(QWord qwContext) => (this.hashContext.TryGetValue(qwContext, out uint iNextHash)) ? iNextHash : 0;

		private CallbackEvent EventFromI(uint index) => (index != 0) ? this[(int)index-1] : null;

		private void Add(in CallbackEvent cbt) // override
		{
			AssertCritical(cbt.iNext == 0); // nil
			AssertCritical(cbt.qwContext != 0);
			cbt.iNext = IFromContext(cbt.qwContext);
			base.Add(cbt);
			hashContext[cbt.qwContext] = (uint)this.Count; // 1-based index, single-threaded
		}

#if DEBUG
		int cTableCount;
#endif // DEBUG

		/*
			Return the most recent worker object with the given context and time range.
		*/
		CallbackEvent FindCallback(QWord qwContext, int actHash, IDVal tid, ThreadAction action)
		{
			CallbackEvent cbe;

			for (uint i = IFromContext(qwContext); i > 0; i = cbe.iNext)
			{
				if (i == 0) break;

				cbe = EventFromI(i);
				AssertCritical(cbe.qwContext == qwContext);

				switch (action)
				{
				case ThreadAction.Queue:
					if (cbe.actHash != actHash) return null;
					if (cbe.FQueued) return null; // cases 4 & 5
					if (cbe.FCanceled) return null;

					// This looks like case 6a or 6b: inversion of the Queue event.
					AssertCritical(cbe.FStarted);
					AssertImportant(cbe.FInverted);

					// Except that it can't be on the same thread, and no intervening events on this thread.
					if (cbe.tidExec == tid) return null;
					TaskItem task = this.allTables.threadTable.GetExec(tid);
					if (task != null && task.timeStartExec > cbe.timeStartExec) return null;

					return cbe;

				case ThreadAction.Start:
#if DEBUG
					if (cbe.actHash != actHash) break;
					if (cbe.FStarted) break;
					if (cbe.FCanceled) break;
					// We expect to find a match ONLY in the first iteration.
					AssertImportant(i == IFromContext(qwContext));
#else // !DEBUG
					// Really, no need for subsequent iterations.
					if (cbe.actHash != actHash) return null;
					if (cbe.FStarted) return null;
					if (cbe.FCanceled) return null;
#endif // !DEBUG
					// Not yet started.
					return cbe;

				case ThreadAction.Stop:
					if (cbe.tidExec != tid) break;
					AssertImportant(cbe.actHash == actHash);
					if (cbe.actHash != actHash) break;
					AssertImportant(cbe.FOnlyStarted);
					if (!cbe.FOnlyStarted) break;
					// Started on same thread, not yet Stopped.
					return cbe;

				case ThreadAction.Cancel:
					// If queued only (case 2) then use this one.
					if (!cbe.FStarted) return cbe;
					// If started executing on same thread (case 3) then use this one.
					if (cbe.tidExec == tid && cbe.FOnlyStarted) return cbe;
					// Else keep looking, unexpectedly.
					AssertImportant(false);
					break;
				}
			}
			return null;
		}


		public static readonly Guid guid = new Guid("{7d44233d-3055-4b9c-ba64-0d47ca40a232}"); // Microsoft-Windows-WinHTTP

		public void DoDispatchEvent(in IGenericEvent evt, ActionType action)
		{
			QWord qwContext = evt.GetAddrValue("Context");
			int actHash = evt.ActivityId.GetHashCode();

			AssertCritical(ActionType.actionNone < action && action < ActionType.actionMax);
			AssertCritical((action == ActionType.Timer) == ((ThreadAction)evt.Id == ThreadAction.Cancel));

			CallbackEvent cbe = FindCallback(qwContext, actHash, evt.ThreadId, (ThreadAction)evt.Id);

			if (cbe != null)
			{
				AssertCritical(!cbe.FCanceled);
				AssertImportant(cbe.pid == evt.ProcessId);
				AssertCritical(cbe.actType == action || (ThreadAction)evt.Id == ThreadAction.Cancel);
				AssertCritical(cbe.actHash == actHash);
			}

			TimestampUI timeStamp = evt.Timestamp.ToGraphable();

			switch ((ThreadAction)evt.Id)
			{
			case ThreadAction.Queue:
				if (cbe == null)
				{
					cbe = new CallbackEvent(qwContext, actHash, action, evt.ProcessId, evt.ThreadId, timeStamp);
					Add(cbe);
				}
				else
				{
					// case 6a or 6b
					AssertImportant(cbe.timeCreate.HasValue());
					AssertImportant(!cbe.FQueued);
					AssertImportant(!cbe.FOnlyCreated);
					AssertImportant(cbe.FInverted);
					// This is a thread inversion / race condition. There should be no long delays.
					AssertImportant(timeStamp.ToMilliseconds - cbe.timeCreate.ToMilliseconds < 100);

					cbe.tidCreate = evt.ThreadId; // cbe.FQueued = true
				}

				GetXLink(cbe);
				cbe.stack = evt.Stack;
#if AUX_TABLES
				cbe.timeRef = evt.Timestamp;
#endif // AUX_TABLES
				break;

			case ThreadAction.Start:
				if (cbe == null)
				{
					// Case 6!?  Or the Enqueue event happened before the start of the trace.
					cbe = new CallbackEvent(qwContext, actHash, action, evt.ProcessId, 0/*tidCreate*/, timeStamp);
					Add(cbe);

					AssertImportant(!cbe.FQueued);
				}
				else
				{
					AssertImportant(cbe.FQueued);
				}
				AssertImportant(cbe.FOnlyCreated);
				AssertImportant(cbe.timeCreate.HasValue());
				AssertImportant(!cbe.timeStartExec.HasValue());
				AssertImportant(!cbe.timeEndExec.HasValue());
				AssertImportant(cbe.timeDestroy.HasMaxValue());
				AssertImportant(cbe.cRef == 0);

				cbe.StartExec(evt.ThreadId, timeStamp);

				// Remember the most recent StartExec on this thread.
				this.allTables.threadTable.StartExec(this, cbe);

				break;

			case ThreadAction.Stop:
				AssertInfo(cbe != null);
				if (cbe == null) break;

				AssertInfo(cbe.FQueued); // case 6
				AssertInfo(cbe.timeCreate.HasValue()); // case 6
				AssertImportant(cbe.timeStartExec.HasValue());
				AssertImportant(cbe.timeEndExec.HasMaxValue());
				AssertImportant(cbe.timeDestroy.HasMaxValue());
				AssertImportant(cbe.tidExec == evt.ThreadId);
				AssertImportant(cbe.FOnlyStarted);

				cbe.EndExec(timeStamp);

				// We must disable garbage collection:
				// We're indexing into the table with the per-Context lists hashed and linked.
				Finish(cbe, false /*fGC*/);
	
				break;

			case ThreadAction.Cancel:
				AssertInfo(cbe != null);
				if (cbe == null) break;

				AssertImportant(cbe.FQueued);
				AssertImportant(cbe.timeCreate.HasValue());
				AssertImportant(cbe.timeDestroy.HasMaxValue());

				if (cbe.FOnlyCreated)
					{
					// case 2 - Canceling
					AssertImportant(!cbe.timeStartExec.HasValue());
					AssertImportant(!cbe.timeEndExec.HasValue());
					AssertImportant(!cbe.FStarted);

					Finish(cbe, false /*fGC*/);
					cbe.state = EState.Canceled;
					}
				else // FOnlyStarted
					{
					// case 3 - Ignore this event.
					AssertImportant(cbe.FOnlyStarted);
					AssertImportant(cbe.timeStartExec.HasValue());
					AssertImportant(cbe.timeEndExec.HasMaxValue());
					AssertImportant(cbe.tidExec == evt.ThreadId);
					}
				break;
			} // switch

#if DEBUG
			// No garbage collection for this table.
			AssertCritical(this.Count >= cTableCount);
			cTableCount = this.Count;
#endif // DEBUG
		} // DoDispatchEvent

		public void Dispatch(IGenericEvent evt)
		{
			// WinHTTP.OverlappedIO records are useless.
			//   WebIO.OverlappedIO records are useful.

			if (evt.Id < (int)ThreadAction.First || evt.Id > (int)ThreadAction.Last)
				return;

			ActionType actionType = (ActionType)evt.GetUInt32("EtwQueueActionType");

			if (actionType != ActionType.OverlappedIO)
				DoDispatchEvent(evt, actionType);
		}
	}
}
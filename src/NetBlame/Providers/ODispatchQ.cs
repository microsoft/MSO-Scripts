// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;

using Microsoft.Windows.EventTracing.Events;

using NetBlameCustomDataSource.Tasks;

using static NetBlameCustomDataSource.Util;

using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using IDVal = System.Int32; // type of Event.pid/tid / ideally: System.UInt32
using QWord = System.UInt64;


/*
GENERIC PATTERN:
	DQPost queue=Q1 callback=C1
	DQPost queue=Q1 callback=C2
	__________
	DQCallbackContextInvoke queue=X0 callback=Y0 // maybe
	DQInvokeStart queue=Q1
	<Trace might begin here.>
	  DQCallbackContextInvoke queue=Q1 callback=C1
	    DQCallbackContextInvoke queue=X1 callback=Y1 // maybe
	      CALLBACK
	  DQCallbackContextInvoke queue=Q1 callback=C2
	    DQCallbackContextInvoke queue=X2 callback=Y2 // maybe
	      CALLBACK
	DQInvokeStop queue=Q1

PATTERN A1: ConcurrentQueue
	DQConcurrentQueuePostIdle queue=Q1 callback=C1
	DQConcurrentQueuePostIdle queue=Q1 callback=C2
	--------
	TPWorkerStartExec
	  DQCallbackContext queue=0 // IGNORE
	  DQConcurrentQueueInvokeStart queue=Q1 Thread=T1
	    DQCallbackContext queue=Q1 callback=C1 Thread=T1
	      CALLBACK
	    DQCallbackContext queue=Q1 callback=C2 Thread=T1
	      CALLBACK
	  DQConcurrentQueueInvokeEnd queue=Q1 Thread=T1
	TPWorkerEndExec

PATTERN A2: ConcurrentQueue
	DQConcurrentQueuePost queue=Q1 callback=C1 // IGNORE (Redundant)
	TPWorkerCreate
	TPWorkerSubmit
	----------
	TPWorkerStartExec
	  DQCallbackContextInvoke queue=0 callback=C1 // IGNORE (Redundant)
	    CALLBACK
	TPWorkerEndExec

PATTERN B: SequentialQueue
	DQUIQueuePost queue=Q1 callback=C1
	DQSequentialQueueRunAsync queue=Q1 // IGNORE?
	DQUIQueuePost queue=Q1 callback=C2
	DQSequentialQueueRunAsync queue=Q1 // IGNORE?
	--------
	TPWorkerStartExec
	  DQCallbackContext queue=0 // IGNORE
	  DQSequentialQueueInvokeStart queue=Q1 Thread=T1
	    DQDequeueSize queue=Q1 size=N // IGNORE
	    DQCallbackContextInvoke queue=Q1 callback=C1 Thread=T1
	      CALLBACK
	    DQCallbackContextInvoke queue=Q1 callback=C2 Thread=T1
	      CALLBACK
	    DQUIQueueShouldYield // IGNORE
	    DQDequeueIdleNoThrottleSize // IGNORE
	    DQDequeueIdleSize // IGNORE
	  DQSequentialQueueInvokeEnd queue=Q1 Thread=T1
	TPWorkerEndExec

PATTERN C: LooperQueue
	DQUIQueuePost queue=Q1 callback=C1
	DQUIQueuePost queue=Q1 callback=C2
	----------
	TPWorkerStartExec
	  DQCallbackContext queue=0 // IGNORE
	  DQLooperQueueInvokeStart queue=Q1 Thread=T1
	    DQCallbackContextInvoke queue=Q1 callback=C1 Thread=T1
	      CALLBACK
	    DQCallbackContextInvoke queue=Q1 callback=C2 Thread=T1
	      CALLBACK
	  DQLooperQueueInvokeEnd queue=Q1 Thread=T1
	  DQLooperQueueInvokeStart queue=Q1 Thread=T1
	    ...
	  DQLooperQueueInvokeEnd queue=Q1 Thread=T1
	TPWorkerEndExec

PATTERN D:
	DQUIQueuePost queue=Q1 callback=C1
	DQUIQueuePostIdle queue=Q1 callback=C2
	----------
  MAIN THREAD
	DQUIQueueInvokeStart queue=Q1
	    DQCallbackContextInvoke queue=Q1 callback=C1
	      DQCallbackContextInvoke queue=QX callback=CX
	        CALLBACK
	    DQCallbackContextInvoke queue=Q1 callback=C2
	      DQCallbackContextInvoke queue=QY callback=CY
	        CALLBACK
	DQUIQueueInvokeStop queue=Q1

PATTERN E: LimitedConcurrentQueue
	DQLimitedConcurrentQueuePost queue=Q1 callback=C1
	DQLimitedConcurrentQueuePost queue=Q1 callback=C2
	----------
	TPWorkerStartExec
	  DQCallbackContext queue=0 // IGNORE
	  DQLimitedConcurrentQueueInvokeStart queue=Q1
	    DQCallbackContextInvoke queue=Q1 callback=C1
	      CALLBACK
	    DQCallbackContextInvoke queue=Q1 callback=C2
	      CALLBACK
	  DQLimitedConcurrentQueueInvokeEnd queue=Q1
	TPWorkerEndExec
*/


namespace NetBlameCustomDataSource.ODispatchQ
{
	public enum ODQType
	{
		Unknown = 0,
		ConcurrentQueue = 1,
		SequentialQueue = 2,
		LooperQueue = 3,
		UIQueue = 4,
		LimitedConcurrentQueue = 5
	}

	public class ODispatchQPost : TaskItem, ITaskItemInfo
	{
		public QWord queue;
		public QWord callback;
		public ODQType type;

		public ODispatchQPost(QWord queue, QWord callback, ODQType type, IDVal pid, IDVal tid, in TimestampUI timeStamp)
				: base(pid, tid, in timeStamp)
		{
			this.queue = queue;
			this.callback = callback;
			this.type = type;
		}

		// Implement ITaskItemInfo
		public string SubTypeName => this.type.ToString();
		public string StatusName => this.state.ToString();
		public QWord Identifier => this.callback;
		public int Period => 0;
	}

	public class ODispatchQTable : TaskTable<ODispatchQPost>
	{
		public ODispatchQTable(int capacity, in AllTables _allTables) : base(capacity, Link.XLinkType.ODispatchQ, _allTables) {}

		public static readonly Guid guid = new Guid("{559A5658-8100-4D84-B756-0A47A476280C}"); // OfficeDispatchQueue

#if DEBUG
		// With this hash we verify our use of: this.allTables.threadTable.Start/GetExec
		// If there is ever a queue mismatch between InvokeStart/End then there may be a problem.
		Dictionary<IDVal/*tid*/, QWord/*queue*/> ThreadQueueHashDB = new Dictionary<IDVal, QWord>(64);
#endif // DEBUG

		[Conditional("DEBUG")]
		void TestQueueIntegrity(IDVal tid, QWord queue, QWord queueNext)
		{
#if DEBUG
			QWord queueStart = 0;
			this.ThreadQueueHashDB.TryGetValue(tid, out queueStart);
			AssertCritical(FImplies(queueStart != 0, queue == queueStart));
			this.ThreadQueueHashDB[tid] = queueNext;
#endif // DEBUG
		}
		[Conditional("DEBUG")]
		void TestQueueIntegrity(IDVal tid, QWord queue) => TestQueueIntegrity(tid, queue, queue);

#if DEBUG
		// Find the most recently dispatched post on this thread.
		ODispatchQPost FindStartExecPost(IDVal pid, IDVal tid)
		{
			return this.FindLast(r => r.tidExec == tid && r.pid == pid && r.state == EState.StartExec);
		}
#endif // DEBUG

		// Find the most recently dispatched post from this process with the given parameters.
		ODispatchQPost FindPost(QWord queue, QWord callback, IDVal pid)
		{
			return this.FindLast(r => r.queue == queue && r.callback == callback && r.pid == pid);
		}


		void Post(in IGenericEvent evt, ODQType type)
		{
			QWord queue = evt.GetAddrValue("queue");
			QWord callback = evt.GetAddrValue("callback");
			TimestampUI timeStamp = evt.Timestamp.ToGraphable();
			ODispatchQPost odqPost = new ODispatchQPost(queue, callback, type, evt.ProcessId, evt.ThreadId, timeStamp);
			odqPost.stack = evt.Stack;
			GetXLink(odqPost);
			Add(odqPost);
		}

		// Create a dummy post, only to attach to the threadTable,
		// to temporarily identify the queue and dispatch context currently on this thread.
		void InvokeStart(in IGenericEvent evt, ODQType type)
		{
			TimestampUI timeStamp = evt.Timestamp.ToGraphable();
			QWord queue = evt.GetAddrValue("queue");
			ODispatchQPost odqPost = new ODispatchQPost(queue, 0, type, evt.ProcessId, evt.ThreadId, timeStamp);
			odqPost.StartExec(evt.ThreadId, timeStamp);
			TestQueueIntegrity(evt.ThreadId, queue);
			this.allTables.threadTable.StartExec(this, odqPost);

			// This is probably redundant, since ODispatchQueue < OTaskPool, and OTaskPool is the wrapper implementation for ODispatchQueue.
			this.allTables.threadTable.SetThreadPoolType(evt.ThreadId, Thread.ThreadClass.ODispatchQ);
		}

		void InvokeEnd(in IGenericEvent evt, ODQType type)
		{
#if DEBUG
			QWord queue = evt.GetAddrValue("queue");
			TestQueueIntegrity(evt.ThreadId, queue, 0);
#endif // DEBUG

			ODispatchQPost odqPost = this.allTables.threadTable.GetExec(evt.ThreadId) as ODispatchQPost;

			if (odqPost != null && (odqPost.callback == 0 || odqPost.state != EState.StartExec))
				odqPost = null;
#if DEBUG
			AssertImportant(odqPost == FindStartExecPost(evt.ProcessId, evt.ThreadId));
			AssertImportant(odqPost == null || odqPost.queue == queue);
#endif // DEBUG
			if (odqPost != null)
			{
				AssertImportant(type == odqPost.type || ODQType.UIQueue == odqPost.type);
				TimestampUI timeStamp = evt.Timestamp.ToGraphable();
				odqPost.EndExec(timeStamp);
			}
		}

		void DoInvoke(in IGenericEvent evt)
		{
			QWord queue = evt.GetAddrValue("queue");
			if (queue == 0) return; // Pattern A2

			// Get the queue and dispatch context for this thread.

			ODispatchQPost odqPostPrev = this.allTables.threadTable.GetExec(evt.ThreadId) as ODispatchQPost;

			TestQueueIntegrity(evt.ThreadId, odqPostPrev?.queue ?? 0);

			if (odqPostPrev != null)
			{
				// Skip the nested DQCallbackContextInvoke, such as in PATTERN D.
				if (queue != odqPostPrev.queue)
					return;

				// Skip the dummy post from InvokeStart, or a post already ended.

				if (odqPostPrev.callback == 0 || odqPostPrev.state != EState.StartExec)
					odqPostPrev = null;
			}
#if DEBUG
			AssertImportant(odqPostPrev == FindStartExecPost(evt.ProcessId, evt.ThreadId));
#endif // DEBUG
			QWord callback = evt.GetAddrValue("callback");

			ODispatchQPost odqPost = FindPost(queue, callback, evt.ProcessId);
			if (odqPost != null && odqPost.state == EState.Created)
			{
				// There is no explicit "End Dispatch" event, so here it's implied.

				TimestampUI timeStamp = evt.Timestamp.ToGraphable();

				if (odqPostPrev != null)
					odqPostPrev.EndExec(timeStamp);

				odqPost.StartExec(evt.ThreadId, in timeStamp);

				this.allTables.threadTable.StartExec(this, odqPost); // For XLINK.GetLink
			}
		} // DoInvoke


		// From ETWDispatchQueue.man
		public enum ODQ
		{ // Keyword Task = ID,
			/*0x02*/ DoExitIdleDisabled = 36, // ignore
			/*0x02*/ DoExitIdleDisabledAndAllowIdleProcessing = 37, // ignore
			/*0x02*/ DoEnterIdleDisabled = 38, // ignore
			/*0x02*/ DoEnterIdleDisabledAndDisableIdleProcessing = 39, // ignore
			/*0x10*/ CallbackContextInvoke = 56,
			/*0x04*/ UIQueueCreate = 64, // ignore
			/*0x04*/ UIQueuePost = 65,
			/*0x04*/ UIQueueInvokeStart = 66,
			/*0x04*/ UIQueueInvokeEnd = 67,
			/*0x04*/ UIQueueIdleInvokeStart = 68, // unused
			/*0x04*/ UIQueueIdleInvokeEnd = 69, // unused
			/*0x04*/ UIQueueRunIdleAsync = 71, // ignore
			/*0x02*/ UIQueueShouldYield = 72, // ignore
			/*0x02*/ IdleMixinInvokeStart = 85, // unused
			/*0x02*/ IdleMixinInvokeEnd = 86, // unused
			/*0x04*/ NotifyShutdown = 96, // ignore
			/*0x04*/ ConcurrentQueueCreate = 97, // ignore
			/*0x04*/ ConcurrentQueuePost = 98, // ignore
			/*0x04*/ ConcurrentQueuePostIdle = 99,
			/*0x04*/ ConcurrentQueueShouldYield = 100, // ignore
			/*0x04*/ ConcurrentQueueInvokeStart = 101,
			/*0x04*/ ConcurrentQueueInvokeEnd = 102,
			/*0x04*/ LimitedConcurrentQueueCreate = 103, // ignore
			/*0x04*/ LimitedConcurrentQueuePost = 104,
			/*0x04*/ LimitedConcurrentQueueInvokeStart = 113,
			/*0x04*/ LimitedConcurrentQueueInvokeEnd = 114,
			/*0x04*/ LooperQueuePost = 116, // unused
			/*0x04*/ LooperQueuePostIdle = 117, // unused
			/*0x04*/ LooperQueueInvokeStart = 119,
			/*0x04*/ LooperQueueInvokeEnd = 120,
			/*0x04*/ UIQueuePostIdle = 121,
			/*0x40*/ SequentialQueuePost = 129, // unused
			/*0x40*/ SequentialQueueInvokeStart = 130,
			/*0x40*/ SequentialQueueInvokeEnd = 131,
			/*0x40*/ SequentialQueueRunAsync = 132, // Ignore
			/*0x40*/ SequentialQueuePostIdle = 133,
			/*0x04*/ DequeueSize = 136, // ignore
			/*0x04*/ DequeueIdleSize = 144, // ignore
			/*0x04*/ DequeueIdleNoThrottleSize = 145, // ignore
		}

		public void Dispatch(in IGenericEvent evt)
		{
			switch ((ODQ)evt.Id)
			{
			case ODQ.CallbackContextInvoke:
				DoInvoke(in evt);
				break;

			// A: ConcurrentQueue
			case ODQ.ConcurrentQueuePostIdle:
				Post(in evt, ODQType.ConcurrentQueue);
				break;
			case ODQ.ConcurrentQueueInvokeStart:
				InvokeStart(in evt, ODQType.ConcurrentQueue);
				break;
			case ODQ.ConcurrentQueueInvokeEnd:
				InvokeEnd(in evt, ODQType.ConcurrentQueue);
				break;

			// B: SequentialQueue
			case ODQ.SequentialQueuePostIdle:
				Post(in evt, ODQType.SequentialQueue);
				break;
			case ODQ.SequentialQueueInvokeStart:
				InvokeStart(in evt, ODQType.SequentialQueue);
				break;
			case ODQ.SequentialQueueInvokeEnd:
				InvokeEnd(in evt, ODQType.SequentialQueue);
				break;

			// C: LooperQueue
			case ODQ.LooperQueueInvokeStart:
				InvokeStart(in evt, ODQType.LooperQueue);
				break;
			case ODQ.LooperQueueInvokeEnd:
				InvokeEnd(in evt, ODQType.LooperQueue);
				break;

			// D: UIQueue
			case ODQ.UIQueuePost:
			case ODQ.UIQueuePostIdle:
				Post(in evt, ODQType.UIQueue);
				break;
			case ODQ.UIQueueInvokeStart:
				InvokeStart(in evt, ODQType.UIQueue);
				break;
			case ODQ.UIQueueInvokeEnd:
				InvokeEnd(in evt, ODQType.UIQueue);
				break;

			// E: LimitedConcurrentQueue
			case ODQ.LimitedConcurrentQueuePost:
				Post(in evt, ODQType.LimitedConcurrentQueue);
				break;
			case ODQ.LimitedConcurrentQueueInvokeStart:
				InvokeStart(in evt, ODQType.LimitedConcurrentQueue);
				break;
			case ODQ.LimitedConcurrentQueueInvokeEnd:
				InvokeEnd(in evt, ODQType.LimitedConcurrentQueue);
				break;

#if DEBUG
			// Intentionally Ignored
			case ODQ.DoExitIdleDisabled:
			case ODQ.DoExitIdleDisabledAndAllowIdleProcessing:
			case ODQ.DoEnterIdleDisabled:
			case ODQ.DoEnterIdleDisabledAndDisableIdleProcessing:
			case ODQ.UIQueueCreate:
			case ODQ.UIQueueRunIdleAsync:
			case ODQ.UIQueueShouldYield:
			case ODQ.NotifyShutdown:
			case ODQ.ConcurrentQueueCreate:
			case ODQ.ConcurrentQueuePost:
			case ODQ.SequentialQueueRunAsync:
			case ODQ.ConcurrentQueueShouldYield:
			case ODQ.LimitedConcurrentQueueCreate:
			case ODQ.DequeueSize:
			case ODQ.DequeueIdleSize:
			case ODQ.DequeueIdleNoThrottleSize:
				break;

			// Expected Unused
			case ODQ.UIQueueIdleInvokeStart:
			case ODQ.UIQueueIdleInvokeEnd:
			case ODQ.IdleMixinInvokeStart:
			case ODQ.IdleMixinInvokeEnd:
			case ODQ.LooperQueuePost:
			case ODQ.LooperQueuePostIdle:
			case ODQ.SequentialQueuePost:
				AssertImportant(false);
				break;

			// Unexpected
			default:
				AssertImportant(false);
				break;
#endif // DEBUG
			}
		}
	}
}
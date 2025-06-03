// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

using System.Collections.Generic; // Dictionary

using Microsoft.Windows.EventTracing.Events;

using NetBlameCustomDataSource.Tasks;

using static NetBlameCustomDataSource.Util;

using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using IDVal = System.Int32; // type of Event.pid/tid / ideally: System.UInt32
using QWord = System.UInt64;


/*
	STANDARD PATTERNS
		IdleUpdateQueued (#1) (1st Enqueue: any thread via MsoIdleMgr::HrAddTaskToIdleQueue)
		IdleUpdateQueued (#2) (We usually use this call stack as the source of the event.)
		IdleUpdateQueued (#3)
		...
		IdleRegisterTask #1 (2nd Enqueue: main thread, any idle processing stack via MsoIdleMgr::EnqueueTask)
		IdleRegisterTask #2 (We usually ignore this call stack, unless the IdleUpdateQueued is missing.)
		IdleRegisterTask #3
		...
		IdleStartExecution (main thread)
		A)	IdleExecuteTask #1
			IdleDeregisterTask #1
			...
		B)	IdleExecuteTask #2
			[Silently requeue for later execute or deregister]
			...
		C)	[No Execute?]
			IdleDeregisterTask #2 or #3
		IdleEndExecution
*/


namespace NetBlameCustomDataSource.MsoIdleMan
{
	public class IdleManTask : TaskItem, ITaskItemInfo
	{
		public uint dwID;
		public int cookie; // cookie<0 => "external component"
		public bool fRequeued;

		public IdleManTask(uint dwID, int cookie, IDVal pid, IDVal tid, in TimestampUI timeStamp)
				: base(pid, tid, in timeStamp)
		{
			this.dwID = dwID;
			this.cookie = cookie;
		}


		// Implement ITaskItemInfo
		public string SubTypeName => "IdleTask";
		public string StatusName => this.state.ToString();
		public QWord Identifier => ((QWord)this.cookie << 32) | this.dwID;
		public int Period => 0;
	}

	public class IdleManTable : TaskTable<IdleManTask>
	{
		public IdleManTable(int capacity, in AllTables _allTables) : base(capacity, Link.XLinkType.MsoIdlePool, _allTables) {}

	/*
		Regrettably, in the case of the record IdleUpdateQueued.ChangeAddTask, the cookie field is 0.
		But each Office process's idle manager has an interlocked cookie counter that we can simulate.
		Caveats:
		1) We have to trace the app from launch. (We eventually see: RegisterTask.cookie == 1)
		2) The order that these events show up in the trace might not be exactly the same as real-time execution.
		3) When dwID==s_idleManagerExternalComponentTag, that external task has its own counter: cookie<0
		TODO: How to handle idle tasks for external tasks, and how they disrupt our assumption of sequential cookies?
	*/
		private class CookieDispenser
		{
			private class CookieManager
			{
				public bool fValidUpdateQueued; // IdleUpdateQueued records can be used.
				public int cookie;

				public CookieManager(int _start)
				{
					cookie = _start;
				}
			}

			private Dictionary<IDVal, CookieManager> ProcessIdleMgr = new Dictionary<IDVal, CookieManager>(8); // Potentially one per Office process.

			public bool GetValidUpdateQueued(IDVal pid)
			{
				if (!ProcessIdleMgr.ContainsKey(pid))
					return false;

				return ProcessIdleMgr[pid].fValidUpdateQueued;
			}

			public void SetValidUpdateQueued(IDVal pid)
			{
				if (!ProcessIdleMgr.ContainsKey(pid))
					ProcessIdleMgr.Add(pid, new CookieManager(0));

				ProcessIdleMgr[pid].fValidUpdateQueued = true;
			}

			public int GetNextCookie(IDVal pid)
			{
				if (ProcessIdleMgr.ContainsKey(pid))
					return ++(ProcessIdleMgr[pid].cookie);

				ProcessIdleMgr.Add(pid, new CookieManager(1));
				return 1;
			}

			public int GetPrevCookie(IDVal pid)
			{
				if (ProcessIdleMgr.ContainsKey(pid))
					return ProcessIdleMgr[pid].cookie;

				return 0;
			}
		}

		private CookieDispenser cookieDispenser = new CookieDispenser();

		public static readonly Guid guid = new Guid("{8736922D-E8B2-47eb-8564-23E77E728CF3}"); // Microsoft-Office-Events


		IdleManTask FindTask(uint dwID, int cookie, IDVal pid)
		{
			return this.FindLast(r => r.dwID == dwID && r.cookie == cookie && r.pid == pid);
		}

		/*
			When a task is requeued there is no direct indication that its execution has ended.
			But we can infer, because another task has begun execution, or the execution period has ended.
			Find that most recently executed (probably queued) task.
		*/
		IdleManTask FindRecentExecTask(IDVal pid, IDVal tid)
		{
			return this.FindLast(r => r.state == EState.StartExec && r.pid == pid && r.tidExec == tid);
		}


		private enum TaskID
		{
			IdleRegisterTask = 36,
			IdleDeregisterTask = 37,
			IdleStartExecution = 39,
			IdleEndExecution = 40,
			IdleExecuteTask = 44,
			IdleUpdateQueued = 50,
			IdleSubmitWorker = 57,
			IdleStartProcessItem = 58,
			IdleEndProcessItem = 100
		}

		private enum IdleFlag
		{
			ChangeAddTask        = 0x0001,   // INTERNAL USE ONLY
			ChangeDeleteTask     = 0x0002,   // INTERNAL USE ONLY
			ChangePriority       = 0x0004,   // change in priority
			ChangeScheduler      = 0x0008,   // change in scheduler
			ChangeTaskFlags      = 0x0010,   // change in task flags
			ChangeDelay          = 0x0020,   // change in release delay
		}

		private const uint s_idleManagerExternalComponentTag = 0x017D8402;


		public void Dispatch(in IGenericEvent evt)
		{
			TimestampUI timeStamp;
			IdleManTask imTask;
			int cookie;
			uint dwID, grfType;

			switch ((TaskID)evt.Id)
			{
			case TaskID.IdleStartExecution:
				// Ensure that this thread (main thread) is marked as dispatching Office Idle Manager.
				this.allTables.threadTable.SetThreadPoolType(evt.ThreadId, Thread.ThreadClass.OIdleMan);

				break;

			case TaskID.IdleEndExecution:
				// If the last task was requeued, rather than deregistered, then we need to end it now.
				imTask = FindRecentExecTask(evt.ProcessId, evt.ThreadId);
				if (imTask != null && imTask.state == EState.StartExec)
				{
					imTask.fRequeued = true;
					timeStamp = evt.Timestamp.ToGraphable();
					imTask.EndExec(timeStamp);
				}
				break;

			case TaskID.IdleRegisterTask:
				dwID = evt.GetUInt32("dwID");
				cookie = (int)evt.GetUInt64("cookie");

				// If we see cookie==1 here then we almost surely have all of the IdleUpdateQueued records from app launch.
				if (cookie == 1)
					this.cookieDispenser.SetValidUpdateQueued(evt.ProcessId);

				// If we have a corresponding record from IdleUpdateQueue, then this is a continuation of that.
				// Else create a new record.

				imTask = null;

				if (dwID != s_idleManagerExternalComponentTag)
				{
					AssertImportant(cookie >= 0);
					if (cookie == 0)
					{
						// The most likely reason for this is that the task was already deregistered. (!?)
						// FWIW, the true value of the cookie is _probably_ GetPrevCookie(evt.ProcessId).
						// Nothing to do here.
						break;
					}
					if (this.cookieDispenser.GetValidUpdateQueued(evt.ProcessId))
						imTask = FindTask(0, cookie, evt.ProcessId);
				}
				else
				{
					AssertImportant(cookie < 0);
					// TODO: What? GetNextCookie at/near this point is probably disrupted.
					AssertImportant(false);
				}

				if (imTask?.stack == null)
				{
					timeStamp = evt.Timestamp.ToGraphable();

					imTask = new IdleManTask(dwID, cookie, evt.ProcessId, evt.ThreadId, timeStamp)
					{
#if AUX_TABLES
						timeRef = evt.Timestamp,
#endif // AUX_TABLES
						stack = evt.Stack
					};
					// No XLink: This stack is a dead end back to idle dispatch on the main thread.
					Add(imTask);
				}
				else
				{
					AssertImportant(imTask.state == EState.Created);
					AssertImportant(imTask.dwID == 0);
					imTask.dwID = dwID;
				}
				break;

			case TaskID.IdleDeregisterTask:
				dwID = evt.GetUInt32("dwID");
				cookie = (int)evt.GetUInt64("cookie");
// TODO: Sometimes the dwID is invalid here.
				imTask = FindTask(dwID, cookie, evt.ProcessId);
				if (imTask != null)
				{
					timeStamp = evt.Timestamp.ToGraphable();
					if (imTask.state == EState.StartExec)
						imTask.EndExec(timeStamp);

					this.Finish(imTask, in timeStamp);
				}
				break;

			case TaskID.IdleExecuteTask:
				imTask = FindRecentExecTask(evt.ProcessId, evt.ThreadId);
				timeStamp = evt.Timestamp.ToGraphable();
				if (imTask != null)
				{
					imTask.fRequeued = true;
					imTask.EndExec(timeStamp);
				}

				dwID = evt.GetUInt32("dwID");
				cookie = (int)evt.GetUInt64("cookie");
				imTask = FindTask(dwID, cookie, evt.ProcessId);
				if (imTask != null)
				{
					AssertImportant(imTask.state != EState.StartExec);
					imTask.StartExec(evt.ThreadId, timeStamp);

					// Remember the most recent StartExec on this thread.
					this.allTables.threadTable.StartExec(this, imTask);
				}
				break;

			case TaskID.IdleUpdateQueued:
				cookie = (int)evt.GetUInt64("cookie");
				grfType = evt.GetUInt32("grfUpdateType");
				switch ((IdleFlag)grfType)
				{
				case IdleFlag.ChangeAddTask:
					// Regrettably, the cookie field is 0 in this case.
					AssertImportant(cookie == 0);
					cookie = this.cookieDispenser.GetNextCookie(evt.ProcessId);
					timeStamp = evt.Timestamp.ToGraphable();
					imTask = new IdleManTask(0, cookie, evt.ProcessId, evt.ThreadId, timeStamp)
					{
#if AUX_TABLES
						timeRef = evt.Timestamp,
#endif // AUX_TABLES
						stack = evt.Stack
					};
					GetXLink(imTask);
					Add(imTask);
					break;
#if DEBUG
				case IdleFlag.ChangeDeleteTask:
					// This could fail when the trace missed the app launch.
					int cookiePrev = this.cookieDispenser.GetPrevCookie(evt.ProcessId);
					AssertImportant(cookiePrev >= cookie || cookiePrev == 0);
					break;
#endif // DEBUG
				}
				break;

			case TaskID.IdleSubmitWorker:
			case TaskID.IdleStartProcessItem:
			case TaskID.IdleEndProcessItem:
				AssertImportant(false); // NYI!?
				break;
			} // switch
		} // Dispatch
	} // IdleManTable
} // NetBlameCustomDataSource.MsoIdleMan

// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

/*
	Common classes for implementing a TaskPool / ThreadPool
*/
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq; // Where().Count

using Microsoft.Windows.EventTracing.Symbols;

using static NetBlameCustomDataSource.Util;

using NetBlameCustomDataSource.Link;

using TimestampETW = Microsoft.Windows.EventTracing.TraceTimestamp;
using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using IDVal = System.Int32; // pid/tid
using QWord = System.UInt64;


namespace NetBlameCustomDataSource.Tasks
{
	public enum EState : byte
	{
		None,
		Created,
		Fired,
		StartExec,
		EndExec,
		Canceled,
		Finished // Can be removed/backed out if ref-count==0.
	};

	public interface ITaskTableBase
	{
		public XLinkType PoolType { get; }

		public uint IFromTask(in TaskItem item); // 1-based

		public bool BumpGC();

		public bool GarbageCollect(bool fFinal);
	}

	public interface ITaskItemInfo
	{
		public string SubTypeName { get; }
		public string StatusName { get; }
		public QWord Identifier { get; }
		public int Period { get; }
	}

	public class TaskItem
	{
		public TimestampUI timeCreate;
		public TimestampUI timeStartExec;
		public TimestampUI timeEndExec;
		public TimestampUI timeDestroy;
#if AUX_TABLES
		public TimestampETW timeRef; // required for pid -> Process
#endif // AUX_TABLES

		// The thread of the stack must be tidCreate.
		public IStackSnapshot stack;
		public XLink xlink;

		public readonly IDVal pid;
		public IDVal tidCreate; // enqueue
		public IDVal tidExec;

		public uint cRef; // Count of other events that happened between Start/EndExec
		public EState state;

		const IDVal tidUnknown = -1;

		public TaskItem(IDVal pid, IDVal tid, in TimestampUI timeStamp)
		{
			this.pid = pid;
			this.tidCreate = tid;
			this.tidExec = tidUnknown;
			this.timeCreate = timeStamp;
			this.timeDestroy.SetMaxValue();
			this.state = EState.Created;
		}

		public void StartExec(IDVal tid, in TimestampUI timeStamp)
		{
			this.state = EState.StartExec;
			this.tidExec = tid;
			this.timeStartExec = timeStamp;
			this.timeEndExec.SetMaxValue();
		}

		public void EndExec(in TimestampUI timeStamp)
		{
			this.state = EState.EndExec;
			this.timeEndExec = timeStamp;
		}


		public void AddRef()
		{
			AssertImportant(this.cRef >= 0);
			this.cRef++;
		}

		/*
			Return true when cRef is successfully decremented.
		*/
		public bool SubRef()
		{
			AssertImportant(this.cRef > 0);
			if (this.cRef <= 0)
				return false;

			--this.cRef;
			return  true;
		}

		public bool ReadyForGC { get => this.cRef == 0 && this.state == EState.Finished; }

		public ITaskItemInfo Info => (Tasks.ITaskItemInfo)this;

	} // TaskItem

/*
	NOTE: The TaskTable shifts around: elements are added and removed.
	Do not hold onto an index into a TaskTable. (References to ref-counted items are fine.)
	To change this behavior: table.FEnableGC = false;
*/
	public class TaskTable<T> : List<T>, ITaskTableBase where T: TaskItem
	{
		public readonly AllTables allTables;

		public readonly XLinkType poolType;

		public TaskTable(int capacity, XLinkType _poolType, in AllTables _allTables) : base(capacity) { this.poolType = _poolType;  this.allTables = _allTables; }

		public XLinkType PoolType { get => poolType; }

		public bool FEnableGC { get; set; } = true;

		/*
			Return the 1-based index of the item: 0=None
			NOTE! This index can change with garbage collection.
		*/
		public uint IFromTask(in TaskItem task)
		{
			return (uint)(this.LastIndexOf((T)task) + 1);
		}


		public void GetXLink(T item)
		{
			item.xlink.GetLink(item.tidCreate, in item.timeCreate, in this.allTables.threadTable);
		}

		/*
			Reset a (possibly) previously set XLink.
			Like XLink.ReGetLink, but use this for anything which implements ITaskTableBase (Thread/TaskPool objects).
		*/
		public void ReGetXLink(T item)
		{
			item.xlink.Unlink(true, this);
			item.xlink.GetLink(item.stack.ThreadId, in item.timeCreate, in allTables.threadTable);
		}

		public bool Finish(T task, in TimestampUI timeStamp)
		{
			task.timeDestroy = timeStamp;
			return Finish(task);
		}

		/*
			Finish / close out a ThreadPool task, and release all unreferenced tasks in the chain.
			Returns true if a GC of this table was done (fGC) or should be done (!fGC).
		*/
		public bool Finish(T task, bool fGC = true)
		{
			AssertImportant(this.LastIndexOf(task) >= 0);
			AssertImportant(!task.ReadyForGC);
			task.state = EState.Finished;

			if (task.cRef > 0)
				return false;

			AssertCritical(task.ReadyForGC);

			return this.Unlink(task, fGC);
		}

		bool Unlink(TaskItem task, bool fGC)
		{
			bool fGCThis = this.BumpGC();

			task.xlink.Unlink(fGC, this);

			if (fGC && fGCThis)
				return this.GarbageCollect(false);

			return fGCThis;
		}


		int cCollectPending;
#if DEBUG
		int cPeak, cCollected;
		bool fInGC;
		const int cGCThreshold = 2; // shake the table
#else // !DEBUG
		const int cGCThreshold = 64;
#endif // !DEBUG


		/*
			Keep an accurate count of GC-ready items in this list:
			Call this whenever an item in this list has become ready for GC.
			Return true if a garbage collection should be triggered.
		*/
		public bool BumpGC()
		{
#if DEBUG_LATER
			int cReady = this.Where(taskT => taskT.ReadyForGC).Count();
			AssertCritical(cReady == this.cCollectPending + 1);
#endif // DEBUG

			return ++this.cCollectPending >= cGCThreshold && this.FEnableGC;
		}

		/*
			Remove all elements in this table which have a zero reference count.
			If fFinal then assume that everything is implicitly closed.
			Returns true if any elements were removed.
			This may change the number of elements in the table!
		*/
		public bool GarbageCollect(bool fFinal)
		{
			if (fFinal)
				this.cCollectPending = this.Count;

			AssertImportant(this.cCollectPending >= 0);
			if (this.cCollectPending <= 0)
				return false;
#if DEBUG
			if (this.cPeak < this.Count)
				this.cPeak = this.Count;

			AssertCritical(!this.fInGC);
			this.fInGC = true;
#endif // DEBUG

			for (int iTask = this.Count-1; iTask >= 0; --iTask)
			{
				TaskItem task = this[iTask];

				if (task.cRef != 0)
					continue;

				if (!fFinal && task.state != EState.Finished)
					continue;

				// This task/worker was destroyed/finished, has no references, and can be removed.

				AssertCritical(fFinal ? task.xlink.CRef<=1 : !task.xlink.HasValue);

				this.RemoveAt(iTask);
#if DEBUG
				++this.cCollected;
				--this.cCollectPending; // Shouldn't go negative, else assert below.
#else // !DEBUG
				if (--this.cCollectPending <= 0) // optimization
					break;
#endif // !DEBUG
			}

#if DEBUG
			if (!fFinal)
			{
				// Verify that everything got counted and collected.
				int cReadyForGC = this.Where(task => task.ReadyForGC).Count();
			//	AssertImportant(this.cCollectPending == cReadyForGC);
			//	AssertImportant(this.cCollectPending == 0);
			}
			this.fInGC = false;
#endif // DEBUG

			return true;
		} // GarbageCollect


		[Conditional("DEBUG")]
		public void Validate()
		{
			// TODO: Check consistency, including overlaps & orphans.
		} // Validate
	} // TaskTable
} // NetBlameCustomDataSource
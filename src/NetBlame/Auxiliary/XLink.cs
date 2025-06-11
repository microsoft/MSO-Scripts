// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

/*
	An XLink connects various threadpool items to each other and to network items.
		NetworkItem.xlink -> ThreadPoolItem1.xlink -> ThreadPoolItem2.xlink -> ...
	Interpretation: NetworkItem was created by ThreadPoolItem1 which was created by ThreadPoolItem2 ...
*/
using System.Diagnostics;

using NetBlameCustomDataSource.Tasks;

using static NetBlameCustomDataSource.Util;

using TimestampUI = Microsoft.Performance.SDK.Timestamp; // struct

using IDVal = System.Int32; // PID/TID (ideally UInt32)


namespace NetBlameCustomDataSource.Link
{
	/*
		Link events together, like this (ideally):
		Network Event -> ThreadPool Task -> ThreadPool Task -> ... : Final Callstack = Main Thread / WinMain
	*/

	// These are the four ThreadPool types.
	// GetLink iterates over these in this order, descending priority.
	public enum XLinkType
	{
		None,
		ODispatchQ,
		OTaskPool,
		MsoIdlePool,
		WThreadPool,
		WTimer,
		WinHTTP,
		Thread, // lowest priority
		Max
	};


	public struct XLink
	{
		public TaskItem taskLinkNext; // Base item for various threadpool types. (Can't use an index because of how GC works.)
		public ITaskTableBase taskTableBase; // Table (base interface) which contains taskLinkNext.

		public uint depth;
		public XLinkType typeNext; // Threadpool type/table of taskLinkNext.

#if DEBUG
		// Validation:
		// (this cMark) == (this container's cRef)
		// Sum of (first ancestors' cAddRef-cSubRef) == (this container's cRef)

		// Number of times that the container item was visited in the Validate enumeration.
		uint cMark;
		// Number of times Add/SubRef was called on taskLink.
		uint cAddRef;
		uint cSubRef;

		public int CRef => (int)cAddRef - (int)cSubRef;
#else // !DEBUG
		public int CRef => 0;
#endif // !DEBUG


		public bool HasValue { get => this.typeNext != XLinkType.None; }


		/*
			Reset a (possibly) previously set XLink.
			Like TaskTable.ReGetXLink, but use this for Network objects, not Thread/TaskPool objects.
		*/
		public void ReGetLink(IDVal tid, in TimestampUI timeOrigin, in Thread.ThreadTable threadTable)
		{
			this.Unlink(false);
			this.GetLink(tid, in timeOrigin, in threadTable);
		}

		/*
			Find the ThreadPool item which is part of a chain that extends as far into the past as possible.
			Use the Create timeStamp if this is for a ThreadPool-like event.
		*/
		public void GetLink(IDVal tidExec, in TimestampUI timeOrigin, in Thread.ThreadTable threadTable)
		{
			ITaskTableBase taskTableBase = null;
			TaskItem taskLink = null;

			AssertCritical(!this.HasValue);
			if (this.HasValue) return; // else there will be loops

			Thread.ThreadItem ti = threadTable.FindThreadItem(tidExec);
			if (ti != null)
			{
				if (ti.TableBaseExec != threadTable)
				{
					// There is an active task callback executing on this thread.
					taskLink = ti.TaskExec;
					taskTableBase = ti.TableBaseExec;
				}
				else if (threadTable.IsInternalAdHocThread(in ti))
				{
					AssertCritical(!threadTable.IsThreadPool(in ti));

					// Perhaps the execution dispatcher is the (non-thread-pool) thread itself, created for this purpose?
					taskLink = ti;
					taskTableBase = threadTable;
				}
			}

			if (taskLink != null)
			{
				AssertCritical(taskLink.tidExec == tidExec);
				AssertCritical(taskLink.timeStartExec <= timeOrigin);

				// If the task has not yet been closed (EndExec) then we must have found the callback dispatcher.
				if (!taskLink.timeEndExec.HasMaxValue())
					taskLink = null;
				else if (taskLink.timeStartExec > timeOrigin)
					taskLink = null;
			}

			if (taskLink != null)
			{
				AssertCritical(taskTableBase.IFromTask(taskLink) > 0);
				this.Link(in taskLink, in taskTableBase);
				this.AddRefLink();
			}
			else
			{
				this.Reset();
			}
		} // GetLink


		public void AddRefLink()
		{
			// Everybody in this chain gets an AddRef because we also aggressively garbage collect and validate consistency.
			for (TaskItem taskT = this.taskLinkNext; taskT != null; taskT = taskT.xlink.taskLinkNext)
			{
				taskT.AddRef();
#if DEBUG
				if (taskT.xlink.HasValue)
					++taskT.xlink.cAddRef;
#endif // DEBUG
			}
		}


		/*
			Copy (by value) xlink2 into this, and do AddRef bookkeeping.
			This is for copying across providers, not into URL.xlink, for example.
		*/
		public void Copy(in XLink xlink2)
		{
			AssertCritical(!this.HasValue);
			if (xlink2.HasValue)
			{
				this = xlink2;
				this.AddRefLink();
			}
			else
			{
				AssertCritical(xlink2.depth == 0);
			}
		}

		public void Link(in TaskItem task, in ITaskTableBase taskTableBase)
		{
			this.taskLinkNext = task;
			this.taskTableBase = taskTableBase;
			this.typeNext = taskTableBase.PoolType;
			this.depth = task.xlink.depth + 1;
#if DEBUG
			this.cAddRef = 1;
			this.cSubRef = 0;
			this.cMark = 0;
#endif // DEBUG
		}

		public void Reset()
		{
			this = default;
		}


		/*
			Unlink an XLink, and release all unreferenced tasks in the chain.
		*/
		public void Unlink(bool fGC = true, in ITaskTableBase tableBaseRef = null)
		{
			if (!this.HasValue) return;

			// No other Network or ThreadPool item has linked to this.
			// It's ready for garbage collection.
			// Likewise release any ThreadPool item to which this one is linked, and so on.

#if DEBUG
			++this.cSubRef;
			if (tableBaseRef == null)
			{
				// This XLink is from a network event, not a threadpool task.
				AssertImportant(this.cAddRef == 1);
				AssertImportant(this.cSubRef == 1);
			}
#endif // DEBUG

			bool fReady = true;
			ITaskTableBase tableBaseGC = null;
			ITaskTableBase tableBase = this.taskTableBase;
			for (TaskItem taskLink = this.taskLinkNext; taskLink != null; taskLink = taskLink.xlink.taskLinkNext)
			{
				if (!taskLink.SubRef()) break; // was already zero!?
#if DEBUG
				if (taskLink.xlink.HasValue)
					++taskLink.xlink.cSubRef;
#endif // DEBUG
				if (fReady)
				{
					if (taskLink.ReadyForGC)
					{
						// Everyone is ready for GC so far.
						if (fGC && tableBase.BumpGC() && tableBase != tableBaseRef)
							tableBaseGC = tableBase;

						tableBase = taskLink.xlink.taskTableBase;
						taskLink.xlink.Reset();
					}
					else
					{
						// No more GC. Just SubRef.
						fReady = false;
					}
				}
				else
				{
					// There should be no unreferenced tasks in this chain.
					AssertCritical(taskLink.cRef > 0);
				}
			} // for taskLink

			this.Reset();

			// Garbage collect one other ThreadPool table from the chain of unlinked items.
			if (fGC && tableBaseGC != null)
				tableBaseGC.GarbageCollect(false);
		} // Unlink

		public uint IFromNextLink => this.taskTableBase?.IFromTask(this.taskLinkNext) ?? 0;


		[Conditional("DEBUG")]
		public void Mark()
		{
#if DEBUG
			++this.cMark;
			AssertImportant(this.cMark == 1);

			for (TaskItem task = this.taskLinkNext; task != null; task = task.xlink.taskLinkNext)
			{
				++task.xlink.cMark;
				AssertImportant(task.xlink.cMark <= task.cRef) ;
			}
#endif // DEBUG
		}

		[Conditional("DEBUG")]
		public void ValidateMarks()
		{
#if DEBUG_TODO
			// This XLink is for a network event.
			AssertImportant(this.cMark == 1);
			AssertImportant(this.cAddRef == (this.HasValue ? 1 : 0));
			AssertImportant(this.cSubRef == 0);
			AssertImportant(this.cAddRef >= this.cSubRef);

			// These XLinks are for threadpool tasks.
			for (TaskItem task = this.taskLinkNext; task != null; task = task.xlink.taskLinkNext)
			{
				AssertImportant(!task.ReadyForGC); // Everything should have already been garbage collected.
				AssertImportant(task.xlink.cMark == task.cRef);
			}
#endif // DEBUG
		}
	} // XLink
} // NetBlameCustomDataSource.Link

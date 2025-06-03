// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using NetBlameCustomDataSource.Tasks;
using NetBlameCustomDataSource.WThreadPool.Classic;

using static NetBlameCustomDataSource.Util; // Assert*

using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using Addr32 = System.UInt32;
using Addr64 = System.UInt64;
using IDVal = System.Int32; // Process/ThreadID (ideally UInt32)
using QWord = System.UInt64;


/*
	A timer is either one-shot or periodic based on its Period value being zero or non-zero respectively.
	Relevant events look like this:
		T1 Timer_Set - Sub_Queue, Timer, Period
		...
		T2 Timer_Canceled - Sub_Queue, Timer
	OR
		T1 Timer_Set - Sub_Queue, Timer, Period
		...
		T2 Timer_Expiration_Begin - Sub_Queue
		T2  Timer_Expiration - Sub_Queue, Timer, Period
		T2  Callback_Enqueue - ...
		    ... // multiple expirations
		T2 Timer_Expiration_End - Sub_Queue

	The call stack for the Expiration events looks like this:
		TppWorkerThread
			TppTimerQueueExpiration
				TppSingleTimerExpiration
					TppWorkPost

	The XPerf stackwalk flags for the Timer Set events are: TimerSetPeriodic and TimerSetOneShot

	TPTimerSetNTTimer (opcode=0x2C)
	This event often follows TPTimerSet or TPTimerExpiration or TPTimerExpirationBegin
	It contains these fields:
		SubQueue
		Due Time (ms)
		Tolerable Delay (ms)

	Meanwhile, the DueTime field in the TPTimerSet & TPTimerExipration is some useless address value.
*/

namespace NetBlameCustomDataSource.WThreadPool.Timer
{
	public class WTPTimer : TaskItem, ITaskItemInfo
	{
// TODO: Confirm that we're handling recurrence correctly. Start/StopExec should reflect only the current recurrence.
		public readonly QWord qwSubQueue;
		public readonly QWord qwTimer;
		public readonly int cmsPeriod; // milliseconds - recurring if positive

		byte status; // Set, Cancel, Expire

		public WTP Status { get => (WTP)status; set { status = (byte)value; } }

		// TODO: Are these 8 functions needed?
		public TimestampUI TimeSet { get => timeCreate; set { timeCreate = value; } }
		public TimestampUI TimeExpire { get => timeStartExec; set { timeStartExec = value; } }
		public IDVal TidSet { get => tidCreate; set { tidCreate = value; } }
		public IDVal TidExpire { get => tidExec; set { tidExec = value; } }

		public WTPTimer(/*in*/ TIMER_1<Addr32> evt)
				: base(evt.idProcess, evt.idThread/*Set*/, /*timeSet*/evt.timeStamp.ToGraphable())
		{
			this.qwSubQueue = evt.Timer1.SubQueue;
			this.qwTimer = evt.Timer1.Timer;
			this.cmsPeriod = evt.Timer1.Period;
		} // ctor32

		public WTPTimer(TIMER_1<Addr64> evt)
		: base(evt.idProcess, evt.idThread/*Set*/, /*timeSet*/evt.timeStamp.ToGraphable())
		{
			this.qwSubQueue = evt.Timer1.SubQueue;
			this.qwTimer = evt.Timer1.Timer;
			this.cmsPeriod = evt.Timer1.Period;
		} // ctor64

		public bool Recurring { get => cmsPeriod > 0; }

		// Implement ITaskItemInfo
		public string SubTypeName => "Timer";
		public string StatusName => this.Status.ToString();
		public QWord Identifier => this.qwTimer;
		public int Period => this.cmsPeriod;
	} // WTPTimer


	public class WTPTimerTable : TaskTable<WTPTimer>
	{
		public WTPTimerTable(int capacity, in AllTables _allTables) : base(capacity, Link.XLinkType.WTimer, in _allTables) {}

		public WTPTimer FindTimer(QWord qwSubQueue, QWord qwTimer)
		{
			for (int iTimer = this.Count-1; iTimer >= 0; --iTimer)
			{
				WTPTimer timer = this[iTimer];
				if (timer.qwTimer == qwTimer && timer.qwSubQueue == qwSubQueue)
				{
					if (timer.Status == WTP.TimerSet)
						return timer;

					break;
				}
			}
			return null;
		}

		/*
			Return true if any timers were expired.
		*/
		public bool FEndExpiration(QWord qwSubQueue, IDVal tid)
		{
			bool fDoGC = false;
			bool fRet = false;
			for (int iTimer = this.Count-1; iTimer >= 0; --iTimer)
			{
				WTPTimer timer = this[iTimer];
				if (timer.Status == WTP.TimerExpiration && timer.TidExpire == tid && timer.qwSubQueue == qwSubQueue)
				{
					if (timer.Recurring)
					{
						// TODO: How does this work with accumulating execution time, capturing references, etc?
						timer.TidExpire = 0;
						timer.Status = WTP.TimerSet;
					}
					else
					{
						timer.Status = WTP.TimerCancel;

						if (timer.state == EState.StartExec)
							timer.EndExec(timer.timeStartExec); // No execution time.

						// Don't trigger a GC while traversing the table.
						if (Finish(timer, false))
							fDoGC = true;
					}
					fRet = true;
				}
			}

			if (fDoGC)
				this.GarbageCollect(false);

			return fRet;
		}


		public void Set(in AltClassicEvent evt)
		{
			WTPTimer timer;

			if (evt.F32Bit)
				timer = new WTPTimer((TIMER_1<Addr32>)evt);
			else
				timer = new WTPTimer((TIMER_1<Addr64>)evt);

			timer.Status = WTP.TimerSet;
			GetXLink(timer);
			// TODO: It's likely this object will be GC'd, and this stack lookup is wasted.
			// Do this in the Gather phase, and stash the TimestampETW?
			// Else: timer.GetStack(evt.idThread, evt.timeStamp)
			timer.stack = this.allTables.stackSource?.GetStack(evt.timeStamp, evt.idThread);
#if AUX_TABLES
			timer.timeRef = evt.timeStamp;
#endif // AUX_TABLES
			Add(timer);
		}

		public void Expiration(in AltClassicEvent evt)
		{
			WTPTimer timer;

			if (evt.F32Bit)
			{
				TIMER_1<Addr32> evt32 = (TIMER_1<Addr32>)evt;
				timer = FindTimer(evt32.Timer1.SubQueue, evt32.Timer1.Timer);
				if (timer == null) return;
				AssertImportant(timer.cmsPeriod == evt32.Timer1.Period);
			}
			else
			{
				TIMER_1<Addr64> evt64 = (TIMER_1<Addr64>)evt;
				timer = FindTimer(evt64.Timer1.SubQueue, evt64.Timer1.Timer);
				if (timer == null) return;
				AssertImportant(timer.cmsPeriod == evt64.Timer1.Period);
			}

			timer.Status = WTP.TimerExpiration;
			timer.StartExec(evt.idThread, evt.timeStamp.ToGraphable());

			// Remember the most recent StartExec on this thread.
			this.allTables.threadTable.StartExec(this, timer);
		}

		public void ExpireBegin(in AltClassicEvent evt)
		{
#if DEBUG
			if (evt.F32Bit)
			{
				TIMER_3<Addr32> evt32 = (TIMER_3<Addr32>)evt;
				AssertImportant(!FEndExpiration(evt32.Timer3.SubQueue, evt.idThread));
			}
			else
			{
				TIMER_3<Addr64> evt64 = (TIMER_3<Addr64>)evt;
				AssertImportant(!FEndExpiration(evt64.Timer3.SubQueue, evt.idThread));
			}
#endif // DEBUG
		}

		public void ExpireEnd(in AltClassicEvent evt)
		{
			bool fEnd;

			if (evt.F32Bit)
			{
				TIMER_3<Addr32> evt32 = (TIMER_3<Addr32>)evt;
				fEnd = FEndExpiration(evt32.Timer3.SubQueue, evt.idThread);
			}
			else
			{
				TIMER_3<Addr64> evt64 = (TIMER_3<Addr64>)evt;
				fEnd = FEndExpiration(evt64.Timer3.SubQueue, evt.idThread);
			}

			AssertInfo(fEnd);
		}

		public void Cancel(in AltClassicEvent evt)
		{
			WTPTimer timer;

			if (evt.F32Bit)
			{
				TIMER_2<Addr32> evt32 = (TIMER_2<Addr32>)evt;
				timer = FindTimer(evt32.Timer2.SubQueue, evt32.Timer2.Timer);
			}
			else
			{
				TIMER_2<Addr64> evt64 = (TIMER_2<Addr64>)evt;
				timer = FindTimer(evt64.Timer2.SubQueue, evt64.Timer2.Timer);
			}
			AssertInfo(timer != null);

			if (timer == null) return;

			timer.Status = WTP.TimerCancel;
			Finish(timer);
		}
	}
} // NetBlameCustomDataSource.WThreadPool.Timer
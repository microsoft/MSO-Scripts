// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices; // StructLayout

using Microsoft.Windows.EventTracing; // ClassicEvent

using static NetBlameCustomDataSource.Util;

using TimestampETW = Microsoft.Windows.EventTracing.TraceTimestamp;

using DWId = System.Int32; // Process/ThreadID (ideally UInt32)

using Addr32 = System.UInt32;
using Addr64 = System.UInt64;


namespace NetBlameCustomDataSource.Thread.Classic
{
#pragma warning disable 649 // StructFromBytes initializes the fields.

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	public class ThreadClassicEvent
	{
		public readonly TimestampETW timeStamp;
		public readonly DWId pidInitiator;
		public DWId tidInitiator;
		public readonly byte opEvent;

		/*
			Initialize a sequential struct from payload bytes.
		*/
		public static void StructFromBytes<T>(in ReadOnlySpan<byte> data, out T payload) where T : struct
		{
			bool fSuccess = MemoryMarshal.TryRead<T>(data, out payload);
			AssertCritical(fSuccess);
		}

		public ThreadClassicEvent(in ClassicEvent evt)
		{
			this.pidInitiator = (DWId)evt.ProcessId;
			this.tidInitiator = (DWId)evt.ThreadId;
			this.opEvent = (byte)evt.Id;

			this.timeStamp = evt.Timestamp;

			if ((TEID)evt.Id != TEID.Rundown) return;

			// This is apparently what WPA does for rundown events: sets the (struct) timestamp to 0.
			this.timeStamp = evt.Timestamp.Zero();

			AssertCritical(this.timeStamp.TotalMicroseconds == 0);
		}
	} // ThreadClassicEvent

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	public readonly struct ThreadEventPayload
	{
		public readonly DWId ProcessId;
		public readonly DWId ThreadId;
	}

	public class THREAD_EVENT : ThreadClassicEvent
	{
		public readonly ThreadEventPayload ThreadEvt;
		public readonly Addr64 ThreadProc;

		const DWId tidUnknown = -1;

		private Addr32 GetThreadProc32(in ClassicEvent evt)
		{
			if (evt.Data.Length < 2*sizeof(UInt32) + 6*sizeof(UInt32))
				return 0;

			// Layout (wmicore.mof): PID (DW), TID (DW), (PTR)[5], ThreadProc (PTR)
			StructFromBytes(evt.Data.Slice(2*sizeof(UInt32) + 5*sizeof(UInt32), sizeof(UInt32)), out UInt32 threadProc);
			return threadProc;
		}

		private Addr64 GetThreadProc64(in ClassicEvent evt)
		{
			if (evt.Data.Length < 2*sizeof(UInt32) + 6*sizeof(UInt64))
				return 0;

			// Layout (wmicore.mof): PID (DW), TID (DW), (PTR)[5], ThreadProc (PTR)
			StructFromBytes(evt.Data.Slice(2*sizeof(UInt32) + 5*sizeof(UInt64), sizeof(UInt64)), out UInt64 threadProc);
			return threadProc;
		}

		public THREAD_EVENT(in ClassicEvent evt) : base(in evt)
		{
			// There are multiple versions of this event class.
			// In any case, just get the first two DWORDs.
			StructFromBytes(evt.Data.Slice(0, 2*sizeof(UInt32)), out this.ThreadEvt);

			// If the thread "created itself" then the creator/initiator is unknown.
			if (this.tidInitiator == this.ThreadEvt.ThreadId && (TEID)evt.Id != TEID.Exit)
				this.tidInitiator = tidUnknown;

			// Get the ThreadProc address if available. (See wmicore.mof: "Thread Create/Exit Event")
			if (evt.Version > 0)
			{
				if (evt.Is32Bit)
					this.ThreadProc = GetThreadProc32(evt);
				else
					this.ThreadProc = GetThreadProc64(evt);
			}
		}
	} // THREAD_EVENT


	// Thread Event ID
	// cf. wmicore.mof
	enum TEID : byte
	{
		Create = 1, // EVENT_TRACE_TYPE_START
		Exit = 2,   // EVENT_TRACE_TYPE_END
		Rundown = 3,// EVENT_TRACE_TYPE_DC_START
	}


	/*
		Parse classic events as they are pre-processed via: traceProcessor.Process()
	*/
	class ThreadEventConsumer
	{
		public Queue<THREAD_EVENT> threadEventQueue;
		public Queue<THREAD_EVENT> threadRundownQueue;
#if DEBUG
		public bool FHaveRundown { get; set; }
#else
		public bool FHaveRundown { get => true; set {} }
#endif // DEBUG

		public ThreadEventConsumer()
		{
			const int capacity = 2048; // TODO: Intelligent initial capacity?
			threadEventQueue = new Queue<THREAD_EVENT>(capacity);
			threadRundownQueue = new Queue<THREAD_EVENT>(capacity/2);
		}


		/*
			By separating the rundown events (time=0) from the others (time>0),
			we join them together here, and thus the events are sorted by time.
		*/
		public void Complete()
		{
			this.FHaveRundown = this.threadRundownQueue.Count > 0;

			while (this.threadEventQueue.Count > 0)
				this.threadRundownQueue.Enqueue(this.threadEventQueue.Dequeue());

			this.threadEventQueue = this.threadRundownQueue;
			this.threadRundownQueue = null;
		}


		public void Process(in ClassicEvent evt)
		{
			THREAD_EVENT te;

			AssertCritical(evt.ProviderId == ThreadTable.guid);

			switch ((TEID)evt.Id)
			{
			case TEID.Create:
			case TEID.Exit:
				te = new THREAD_EVENT(evt);

				if (te.ThreadEvt.ThreadId != 0 && te.ThreadEvt.ProcessId > TcpIp.TcbRecord.pidSystem/*4*/)
					threadEventQueue.Enqueue(te);

				break;

			case TEID.Rundown:
				te = new THREAD_EVENT(evt);

				if (te.ThreadEvt.ThreadId != 0 && te.ThreadEvt.ProcessId > TcpIp.TcbRecord.pidSystem/*4*/)
					threadRundownQueue.Enqueue(te);

				break;
			}
		} // Process
	} // ThreadEventConsumer
} // namespace NetBlameCustomDataSource.Thread.Classic
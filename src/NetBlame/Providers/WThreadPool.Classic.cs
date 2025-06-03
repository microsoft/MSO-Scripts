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


namespace NetBlameCustomDataSource.WThreadPool.Classic
{
#pragma warning disable 649 // StructFromBytes initializes the fields.

	[StructLayout(LayoutKind.Sequential,Pack=4)]
	public class AltClassicEvent
	{
		public readonly TimestampETW timeStamp;
		public readonly DWId idProcess;
		public readonly DWId idThread;
		readonly byte idEvent;
		readonly byte f32Bit;

		public WTP IdEvent { get => (WTP)this.idEvent; }
		public bool F32Bit { get => f32Bit != 0; }

		/*
			Initialize a sequential struct from payload bytes.
		*/
		public static void StructFromBytes<T>(in ReadOnlySpan<byte> data, out T payload) where T : struct
		{
			bool fSuccess = MemoryMarshal.TryRead<T>(data, out payload);
			AssertCritical(fSuccess);
		}

		public AltClassicEvent(in ClassicEvent evt)
		{
			this.timeStamp = evt.Timestamp;
			this.idProcess = (DWId)evt.ProcessId;
			this.idThread = (DWId)evt.ThreadId;
			this.idEvent = (byte)evt.Id;
			this.f32Bit = evt.Is32Bit ? (byte)1 : (byte)0;
		}
	}

	// ClassicEvent.Data can be problematic in some configurations:
	// https://github.com/dotnet/runtime/issues/25543
	// https://github.com/dotnet/runtime/issues/25854

	[StructLayout(LayoutKind.Sequential,Pack=4)]
	public readonly struct EventPayload<A>
	{
		public readonly A PoolId;
		public readonly A TaskId;
		public readonly A CallbackFunction;
		public readonly A CallbackContext;
	}

	enum EventPayloadFieldIndex
	{
		iPoolId,
		iTaskId,
		iCallbackFunction,
		iCallbackContext
	}
	
	// CallbackEnqueue
	// CallbackDequeue
	// CallbackStart
	// CallbackStop
	class THREAD_POOL_EVENT<A> : AltClassicEvent where A : unmanaged // Addr32/64
	{
		public EventPayload<A> ThreadPoolEvt;

		public THREAD_POOL_EVENT(in ClassicEvent evt) : base(in evt) { StructFromBytes(evt.Data, out this.ThreadPoolEvt); }
	}

	// CallbackCancel
	class THREAD_POOL_CANCEL<A> : AltClassicEvent where A : unmanaged // Addr32/64
	{
		[StructLayout(LayoutKind.Sequential,Pack=4)]
		public struct Payload2
		{
			public EventPayload<A> ThreadPoolEvt;
			readonly A SubProcessTag;
			readonly UInt32 CancelCount;
		};

		public Payload2 ThreadPoolCancel;

		public THREAD_POOL_CANCEL(in ClassicEvent evt) : base(in evt) { StructFromBytes(evt.Data, out this.ThreadPoolCancel); }
	}

/*
	TimerSetNTTimer (opcode=0x2C)
	This event often follows TimerSet or TimerExpiration or TimerExpirationBegin
	It contains these fields:
		SubQueue
		Due Time (ms)
		Tolerable Delay (ms)

	Meanwhile, the DueTime field in the TimerSet & TimerExipration is some useless address value.
*/

	// TimerSet
	// TimerExpiration
	[StructLayout(LayoutKind.Sequential,Pack=4)]
	public class TIMER_1<A> : AltClassicEvent where A : unmanaged // Addr32/64
	{
		[StructLayout(LayoutKind.Sequential,Pack=4)]
		public readonly struct Payload
		{
			public readonly UInt64 DueTime; // meaningless value
			public readonly A SubQueue;
			public readonly A Timer;
			public readonly Int32 Period; // milliseconds - recurring if positive
		}

		public readonly Payload Timer1;

		public TIMER_1(in ClassicEvent evt) : base(in evt) { StructFromBytes(evt.Data, out this.Timer1); }
	}

	// TimerCancel
	[StructLayout(LayoutKind.Sequential,Pack=4)]
	class TIMER_2<A> : AltClassicEvent where A : unmanaged // Addr32/64
	{
		[StructLayout(LayoutKind.Sequential,Pack=4)]
		public readonly struct Payload
		{
			public readonly A SubQueue;
			public readonly A Timer;
		}

		public readonly Payload Timer2;

		public TIMER_2(in ClassicEvent evt) : base(in evt) { StructFromBytes(evt.Data, out this.Timer2); }
	}

	// TimerExpireBegin
	// TimerExpireEnd
	[StructLayout(LayoutKind.Sequential,Pack=4)]
	class TIMER_3<A> : AltClassicEvent where A : unmanaged // Addr32/64
	{
		public readonly struct Payload
		{
			public readonly A SubQueue;
		}

		public readonly Payload Timer3;

		public TIMER_3(in ClassicEvent evt) : base(in evt) { StructFromBytes(evt.Data, out this.Timer3); }
	}


	/*
		Parse classic events as they are pre-processed via: traceProcessor.Process()
	*/
	class WThreadPoolEventConsumer
	{
		public Queue<AltClassicEvent> traceWThreadPool;

		public WThreadPoolEventConsumer()
		{
			traceWThreadPool = new Queue<AltClassicEvent>(32768); // TODO: Intelligent initial capacity?
		}

		/*
			We only care about Callbacks which have an Enqueue event, because that's the callstack we're tracking.
			So there are many ThreadPool Callback Start/Stop events that we do not care about, maybe 2/3 of all Callback events.
			These are typically IO Completion Callbacks.

			The Callbacks' TaskId, PoolId, and Context values can all be reused across a trace.
			But the CallbackFunction value is unique in its meaning.  Each represents its own specific flavor of Callback.
			So we will track all CallbackFunction values which occur in the Enqueue events.  There should be less than ~100 unique values.

			This speeds up immensely: WTPCallbackTable.FindCallbackByTask
		*/

		private readonly HashSet<Addr64> CallbackHash = new HashSet<Addr64>(64);

		static ulong CallbackFromEvent64(in ClassicEvent evt)
		{
			ReadOnlySpan<ulong> span = MemoryMarshal.Cast<byte, ulong>(evt.Data);
			return span[(int)EventPayloadFieldIndex.iCallbackFunction];
		}

		static uint CallbackFromEvent32(in ClassicEvent evt)
		{
			ReadOnlySpan<uint> span = MemoryMarshal.Cast<byte, uint>(evt.Data);
			return span[(int)EventPayloadFieldIndex.iCallbackFunction];
		}

		static Addr64 CallbackFromEvent<A>(in ClassicEvent evt)
		{
			AssertCritical(default(A) is Addr32 || default(A) is Addr64);

			if (default(A) is Addr32)
				return (Addr64)CallbackFromEvent32(in evt);
			else
				return CallbackFromEvent64(in evt);
		}

		void Register<A>(in ClassicEvent evt) where A : unmanaged
		{
			CallbackHash.Add(CallbackFromEvent<A>(in evt));
		}

		bool IsRegistered<A>(in ClassicEvent evt) where A : unmanaged
		{
			return CallbackHash.Contains(CallbackFromEvent<A>(in evt));
		}


		/*
			Queue Windows-ThreadPool events for later dispatch.
		*/
		public void WThreadPoolEvent<A>(in ClassicEvent evt) where A : unmanaged // Addr32/64
		{
			AltClassicEvent ace = null; // base class

			AssertCritical(evt.ProviderId == WThreadPoolTable.guid);

			switch ((WTP)evt.Id)
			{
			// ThreadPool Items
			case WTP.CallbackEnqueue:
			case WTP.CallbackDequeue:
				this.Register<A>(in evt);
				ace = new THREAD_POOL_EVENT<A>(in evt);
				break;
			case WTP.CallbackStart: // High Traffic!
			case WTP.CallbackStop:  // High Traffic!
				if (!this.IsRegistered<A>(in evt)) break;
				ace = new THREAD_POOL_EVENT<A>(in evt);
				break;
			case WTP.CallbackCancel:
				if (!this.IsRegistered<A>(in evt)) break;
				ace = new THREAD_POOL_CANCEL<A>(in evt);
				break;

			// Timer Items
			case WTP.TimerSet:
				ace = new TIMER_1<A>(in evt);
				break;
			case WTP.TimerCancel:
				ace = new TIMER_2<A>(in evt);
				break;
			case WTP.TimerExpiration:
				ace = new TIMER_1<A>(in evt);
				break;
			case WTP.TimerExpireBegin: // TODO: DEBUG?
				ace = new TIMER_3<A>(in evt);
				break;
			case WTP.TimerExpireEnd:
				ace = new TIMER_3<A>(in evt);
				break;
			}

			if (ace != null)
				traceWThreadPool.Enqueue(ace);
		} // WThreadPoolEvent
	} // ClassicEventConsumer
} // NetBlameCustomDataSource.WThreadPool.Classic
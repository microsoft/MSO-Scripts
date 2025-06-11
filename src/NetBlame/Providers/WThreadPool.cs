// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

using NetBlameCustomDataSource.WThreadPool.Callback;
using NetBlameCustomDataSource.WThreadPool.Classic;
using NetBlameCustomDataSource.WThreadPool.Timer;


namespace NetBlameCustomDataSource.WThreadPool
{
	public enum WTP : byte
	{
		// These are opcodes, not record IDs.
		// See ntwmi.w "Event Types for ThreadPool"

		CallbackEnqueue = 0x20, // 32
		CallbackDequeue = 0x21, // 33
		CallbackStart = 0x22, // 34
		CallbackStop = 0x23, // 35
		CallbackCancel = 0x24, // 36
/*
		PoolCreate = 0x25, // 37 // PoolId:PTR
		PoolClose = 0x26, // 38 // PoolId:PTR
*/
		TimerSet = 0x2A, // 42
		TimerCancel = 0x2B, // 43
		TimerSetNTTimer = 0x2C, // 44 // unused
		TimerExpireBegin = 0x2E, // 46
		TimerExpireEnd = 0x2F, // 47
		TimerExpiration = 0x30, // 48
	}


	public class WThreadPoolTable
	{
		public readonly WTPCallbackTable wtpCallbackTable;
		public readonly WTPTimerTable wtpTimerTable;

		public WThreadPoolTable(int capacity, in AllTables _allTables)
		{
			wtpCallbackTable = new WTPCallbackTable(capacity, in _allTables);
			wtpTimerTable = new WTPTimerTable(capacity, in _allTables);
		}


		public static readonly Guid guid = new Guid("{c861d0e2-a2c1-4d36-9f9c-970bab943a12}"); // Windows-ThreadPool

		public void Dispatch(in AltClassicEvent evtClassic)
		{
			switch (evtClassic.IdEvent)
			{
			// ThreadPool Callback Events

			case WTP.CallbackEnqueue:
				wtpCallbackTable.Enqueue(in evtClassic);
				break;

			case WTP.CallbackDequeue:
				// Ensure that this thread is marked as WThreadPool.
				wtpCallbackTable.allTables.threadTable.SetThreadPoolType(evtClassic.idThread, Thread.ThreadClass.WThreadPool);

				wtpCallbackTable.Dequeue(in evtClassic);
				break;

			case WTP.CallbackStart: // High Traffic?
				wtpCallbackTable.StartExec(in evtClassic);
				break;

			case WTP.CallbackStop:  // High Traffic?
				wtpCallbackTable.EndExec(in evtClassic);
				break;

			case WTP.CallbackCancel: // TODO: Test this!
				wtpCallbackTable.Cancel(in evtClassic);
				break;

			// Timer Events

			case WTP.TimerSet:
				wtpTimerTable.Set(in evtClassic);
				break;

			case WTP.TimerExpiration:
				// Ensure that this thread is marked as WThreadPool.
				wtpTimerTable.allTables.threadTable.SetThreadPoolType(evtClassic.idThread, Thread.ThreadClass.WThreadPool);

				wtpTimerTable.Expiration(in evtClassic);
				break;

			case WTP.TimerExpireBegin: // TODO: DEBUG
				wtpTimerTable.ExpireBegin(in evtClassic);
				break;

			case WTP.TimerExpireEnd:
				wtpTimerTable.ExpireEnd(in evtClassic);
				break;

			case WTP.TimerCancel:
				wtpTimerTable.Cancel(in evtClassic);
				break;
			} // switch evtClassic.IdEvent
		} // Dispatch
	} // WThreadPool
} // WThreadPoolNS
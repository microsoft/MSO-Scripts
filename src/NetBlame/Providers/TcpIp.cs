// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic; // List<>
using System.Net;

using Microsoft.Windows.EventTracing.Events;

using static NetBlameCustomDataSource.Util;

using TimestampETW = Microsoft.Windows.EventTracing.TraceTimestamp;
using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using IDVal = System.Int32; // Process/ThreadID : -1 = unknown
using QWord = System.UInt64;


namespace NetBlameCustomDataSource.TcpIp
{
	public class TcbRecord : IGraphableEntry
	{
		public const IDVal pidUnknown = -1; // process
		public const IDVal tidUnknown = -1; // thread
		public const IDVal pidIdle = 0;
		public const IDVal pidSystem = 4;

		public enum MyStatus : byte
		{
			/* Ordered by sequential operation:
				{ Connect_Request, Connect_Complete } OR Rundown OR Inferred
				{ Shutdown, Connect_Fail, Close } OR Abandon
			*/
			Null,
			// Get the OPEN time stamp from the first of these:
			Connect_Request,
			Connect_Complete,
			Inferred, // Not in the rundown, not created, but referenced/inferred. (Not sure how this happens.)
			Rundown,
			// Get the CLOSE time stamp from the last of these:
			Shutdown,
			Connect_Fail,
			Close,
			Abandon // ie. lost the Shutdown, Fail or Close
		};

		// Separate out values which should not show up as strings.
		public enum MyStatus_Meta : byte
		{
			OpenFirst = MyStatus.Connect_Request,
			OpenLast = MyStatus.Rundown,
			CloseFirst = MyStatus.Shutdown,
			CloseLast = MyStatus.Abandon
		}

		public bool FClosed => !timeClose.HasMaxValue();
		public bool FShutdown => !timeShutdown.HasMaxValue();

		public TimestampETW timeOpen; // Reference Time
		public TimestampUI timeShutdown; // May occur before or after timeClose.
		public TimestampUI timeClose;

		public QWord tcb; // transmission control block

		public IDVal tid; // thread id
		public IDVal pid; // process id
		public IDVal pidAlt; // alternate PID if pid==pidUnknown
		public IDVal tidRecvCache; // thread of most recent UDP Receive event

		public uint cbPost;
		public uint cbSend;
		public uint cbRecv;
		public uint cbRecvCache; // data size of most recent UDP Receive event

		public uint iNext; // UDP records with the same open endpoint and different addresses

		public bool fUDP; // fUDP => tcb = endpoint identifier
		public bool fPidSure;
		public bool fGathered; // for GatherTcpIp
		public bool fCorrelatedSendRecv; // optimization for CorrelateSendRecv

#if AUX_TABLES
		public uint iDNS;
#endif // AUX_TABLES

		public IPEndPoint addrRemote;

		public MyStatus status;
		public byte grbitProtocol; // Protocol

		public ushort socket; // for correlation with WinINet and Winsock

		public TcbRecord(QWord tcb)
		{
			this.tcb = tcb;
			this.pid = pidUnknown;
			this.pidAlt = pidUnknown;
			this.tid = tidUnknown;
			this.tidRecvCache = tidUnknown;
			this.timeShutdown.SetMaxValue();
			this.timeClose.SetMaxValue();
		}

		public TcbRecord Clone(IPEndPoint ipAddrRemote)
		{
			TcbRecord tcbr = (TcbRecord)this.MemberwiseClone();
			tcbr.addrRemote = ipAddrRemote;
			tcbr.iNext = 0;
			tcbr.grbitProtocol = 0;
			tcbr.cbPost = tcbr.cbSend = tcbr.cbRecv = tcbr.cbRecvCache = 0;
			tcbr.tidRecvCache = tidUnknown;
			return tcbr;
		}

		/*
			Confirm that the UDP Endpoint of the most recent UDP Receive event is the expected one.
		*/
		public bool FMatch(IDVal pid, ushort socket, in IPEndPoint address)
		{
			AssertImportant(this.socket != 0);
			if (!this.fUDP) return false;
			if (!FImplies(pid != pidUnknown, this.pid == pid)) return false;
			if (!FImplies(socket != 0, this.socket == socket)) return false;
			if (this.addrRemote.Empty() || !this.addrRemote.Equals(address)) return false;
			return true;
		}

		public bool FMatchCb(IDVal pid, uint cb, ushort socket, in IPEndPoint address)
		{
			if (this.cbRecvCache != cb) return false;
			return FMatch(pid, socket, in address);
		}


		public bool CheckType(Protocol bitf)
		{
			return (this.grbitProtocol & (byte)bitf) != 0;
		}

		public void SetType(Protocol bitf)
		{
			this.grbitProtocol |= (byte)bitf;
		}


		/*
			pidSure is the PID from the event's ProcessId/PID field via GetProcessId().
		*/
		public void EnsurePID(IDVal pidSure)
		{
			AssertCritical(this != null);
			if (pidSure != pidUnknown)
			{
				AssertCritical(FImplies(this.fPidSure, this.pid == pidSure));
				AssertCritical(FImplies(this.pid != pidUnknown, this.pid == pidSure));
				AssertImportant(FImplies(this.pid == pidUnknown && this.pidAlt != pidUnknown, this.pidAlt == pidSure));
				this.pid = pidSure;
				this.fPidSure = true;
			}
		}


		public void HandleOpenRecord(MyStatus status, IDVal pid, IDVal tid, in TimestampETW timeStamp, in SocketAddress addrLocal, in SocketAddress addrRemote)
		{
			// All these are important.
			AssertCritical(pid != pidIdle);
			AssertImportant((MyStatus)TcbRecord.MyStatus_Meta.OpenFirst <= status && status <= (MyStatus)TcbRecord.MyStatus_Meta.OpenLast);
			AssertImportant(timeStamp.HasValue);

			AssertImportant(!this.FClosed);
#if DEBUG
			switch (status)
			{
			case TcbRecord.MyStatus.Connect_Request:
			case TcbRecord.MyStatus.Rundown:
			case TcbRecord.MyStatus.Inferred:
				// First logged record of a TCB.
				AssertInfo(this.status == TcbRecord.MyStatus.Null || this.status == TcbRecord.MyStatus.Inferred);
				AssertInfo(!this.timeOpen.HasValue);
				// There may be duplicated rundown records.
				AssertImportant(FImplies(status != TcbRecord.MyStatus.Rundown, this.addrRemote.Empty()));
				break;
			default:
				// Subsequent logged records of a TCB.
				AssertImportant(this.status < status || this.status == MyStatus.Rundown);
				AssertImportant(this.timeOpen.HasValue);
				AssertImportant(!this.addrRemote.Empty());
				break;
			}
#endif // DEBUG
			if (!this.timeOpen.HasValue)
				this.timeOpen = timeStamp;

			this.status = status;

			if (pid != pidUnknown)
			{
				if (this.pid == pidUnknown)
				{
					this.pid = pid;
				}
				else if (this.pid != pid)
				{
					// Somehow we got an inconsistent ProcessID.
					AssertImportant(this.pid == pidSystem); // OK, don't trust pidSystem
					AssertImportant(tid == tidUnknown); // Got 'pid' from the ProcessId field.
					AssertImportant(!this.fPidSure); // Inconsistency not unexpected?
					if (!this.fPidSure)
						this.pid = pid;
				}

				if (this.tid == tidUnknown)
					this.tid = tid;
			}

			// The "socket" is the port of the local address.
			if (!addrLocal.Empty())
			{
				if (this.socket == 0)
					this.socket = addrLocal.Port();
				else
					AssertImportant(this.socket == addrLocal.Port());
			}

			if (!addrRemote.Empty())
			{
				if (this.addrRemote.Empty())
				{
					this.addrRemote = NewEndPoint(addrRemote);
				}
#if DEBUG
				else
				{
					IPEndPoint addrT = NewEndPoint(addrRemote);
					AssertImportant(addrT.Address.Equals(this.addrRemote.Address));
				}
#endif // DEBUG
			}
		} // HandleOpenRecord

		public void HandleCloseRecord(MyStatus status, IDVal pid, in TimestampUI timeStamp, in SocketAddress addrRemote)
		{
			AssertCritical(pid != pidIdle);
			AssertCritical(this.pid != pidIdle);

			// All these are important.
			AssertImportant((MyStatus)MyStatus_Meta.CloseFirst <= status && status <= (MyStatus)MyStatus_Meta.CloseLast);
			AssertImportant(timeStamp.HasValue() && !timeStamp.HasMaxValue());

			// Shutdown and Close can happen in either order. Either one should set timeClose.
			AssertImportant(this.status != TcbRecord.MyStatus.Null && this.status < TcbRecord.MyStatus.Abandon);

			if (!addrRemote.Empty())
			{
				if (this.addrRemote.Empty())
					this.addrRemote = NewEndPoint(addrRemote);
#if DEBUG
				else
				{
					IPEndPoint addrT = NewEndPoint(addrRemote);
					if (!addrT.Empty() && this.status < TcbRecord.MyStatus.Shutdown)
						AssertImportant(addrT.Address.Equals(this.addrRemote.Address));
				}
#endif // DEBUG
			}

			if (this.pid == pidUnknown)
			{
				this.pid = pid;

				if (this.pid == pidUnknown)
					this.pid = this.pidAlt;
			}

			if (this.status < status)
				this.status = status;

			// Shutdown may occur before or after Close.
			// All other send/receive activity should end after Close, not necessarily after Shutdown.
			// cf. FindTcbRecord

			if (status == MyStatus.Shutdown)
			{
				// The placement of the Shutdown timeStamp is rather erratic.
				AssertCritical(!this.FShutdown);
				this.timeShutdown = timeStamp;
			}
			else
			{
				// The placement of the Close / Connect_Fail / Abandon timeStamps are more stable.
				AssertCritical(!this.FClosed);
				this.timeClose = timeStamp;
			}
		} // HandleCloseRecord

		// Implement IGraphableEntry
		public IDVal Pid => this.pid;
		public IDVal TidOpen => this.tid;
		public TimestampETW TimeRef => this.timeOpen;
		public TimestampUI TimeOpen => this.timeOpen.ToGraphable();
		public TimestampUI TimeClose => this.timeClose;
		public Microsoft.Windows.EventTracing.Symbols.IStackSnapshot Stack => default; // no stack
		public uint LinkIndex => 0; // no XLink
		public Link.XLinkType LinkType => Link.XLinkType.None; // no XLink
	} // TcbRecord


	public class TcpTable : List<TcbRecord>
	{
		readonly AllTables allTables;

		public TcpTable(int capacity, in AllTables _allTables) : base(capacity) { this.allTables = _allTables; }

		const IDVal pidUnknown = TcbRecord.pidUnknown; // process
		const IDVal pidIdle = TcbRecord.pidIdle;
		const IDVal tidUnknown = TcbRecord.tidUnknown; // thread

		public TcbRecord TcbrFromI(uint iTcb) => (iTcb != 0) ? this[(int)iTcb-1] : null;

		public uint IFromTcbr(TcbRecord tcbR) => (uint)(this.LastIndexOf(tcbR) + 1);


		private static readonly TcbRecord tcbREmpty = new TcbRecord(QWord.MaxValue);

		private TcbRecord tcbRCache = tcbREmpty; // never null

		struct UDPEvent
		{
			public TcbRecord tcbr;
			IDVal pid;
			uint  cb;

			public void Set(IDVal pid, uint cb, TcbRecord tcbr)
			{
				this.pid = pid;
				this.cb = cb;
				this.tcbr = tcbr;
			}

			public void Reset()
			{
				this.pid = pidUnknown;
				this.cb = 0;
				this.tcbr = null;
			}

			public bool FMatch(IPEndPoint address, IDVal pid, uint cb) => this.pid == pid && this.cb == cb && this.tcbr?.addrRemote != null && this.tcbr.addrRemote.Equals(address);
		}

		// For correlating with adjacent Winsock events.
		UDPEvent udpRecvCache;

		new void Add(TcbRecord tcbr)
		{
			base.Add(tcbr);
			tcbRCache = tcbr;
		}

		new void Remove(TcbRecord tcbr)
		{
			throw(new Exception("Remove not allowed. Elements are reference by index."));
		}

		/*
			Return the most recent, matching, open TCB record.
			Shutdown might occur before the close and other events, so track it separately.
			'pid' is either reliable (GetProcessId(evt)), or it is pidUnknown.
			If 'pid' is reliable, it might modify: TcbRecord.pid
			HIGH TRAFFIC FUNCTION!
		*/
		TcbRecord FindTcbRecord(QWord tcb, IDVal pid)
		{
			AssertCritical(pid != pidIdle);

			TcbRecord tcbR = tcbRCache;

			if (tcbR.tcb != tcb)
				tcbR = this.FindLast(t => t.tcb == tcb);

			if (tcbR == null || tcbR.FClosed) return null; // already closed?

			tcbR.EnsurePID(pid);

			tcbRCache = tcbR;

			return tcbR;
		}

		/*
			When a TCB Record is closed, we may still need to access it for Shutdown.
			Or there may be lingering Receive events.
			'pid' is either reliable (GetProcessId(evt)), or it is pidUnknown.
			If 'pid' is reliable, it might modify: TcbRecord.pid
		*/
		TcbRecord FindTcbRecordClosed(QWord tcb, IDVal pid)
		{
			AssertCritical(pid != pidIdle);

			TcbRecord tcbR = tcbRCache;

			if (tcbR.tcb != tcb)
			{
				if (pid != pidUnknown)
					tcbR = this.FindLast(t => t.tcb == tcb && FImplies(t.fPidSure, t.pid == pid));
				else
					tcbR = this.FindLast(t => t.tcb == tcb);
			}

			if (tcbR == null || tcbR.FShutdown)
				return null; // already shutdown?

			tcbR.EnsurePID(pid);

			tcbRCache = tcbR;

			return tcbR;
		}


		/*
			Do all the work (to close an outstanding TcbRecord and) to open a new TcbRecord.
			pid is assumed to be trusted (via GetProcessId()) iff tid == tidUnknown
		*/
		TcbRecord AddTcbRecord(QWord tcb, TcbRecord.MyStatus status, in TimestampETW timeStamp, IDVal pid, IDVal tid, in SocketAddress addrLocal, in SocketAddress addrRemote)
		{
			TcbRecord tcbR = FindTcbRecord(tcb, (tid==tidUnknown) ? pid : pidUnknown);
			if (FImplies(status == TcbRecord.MyStatus.Rundown, tcbR == null))
			{
				AssertImportant(tcbR == null);
				AssertCritical(pid != pidIdle);

				// RARE: TCB slots are reused, and the previous one apparently didn't get a CLOSE notification.
				tcbR?.HandleCloseRecord(TcbRecord.MyStatus.Abandon, pid, new TimestampUI(timeStamp.Nanoseconds-1), addrRemote);

				// Require time-sorted order.
				AssertImportant(this.Count == 0 || TcbrFromI((uint)this.Count).timeOpen <= timeStamp);

				tcbR = new TcbRecord(tcb);
				this.Add(tcbR);
			}
			else
			{
				// It is rare but it can happen that the Rundown event gets logged _after_ a key 'live' event.
				// This is because Rundown events for all processes are generated via the WPR process.
				// Meanwhile, events from other processes may already be collected.
			}

			// Set the protocol to "Rundown," else it will be "Unknown."
			if (status == TcbRecord.MyStatus.Rundown) // Leave MyStatus.Inferred as type Protocol.Unknown.
				tcbR.SetType(Protocol.Rundown);

			tcbR.HandleOpenRecord(status, pid, tid, in timeStamp, addrLocal, addrRemote);

			return tcbR;
		}


		/*
			Sometimes we find a TCB, apparently with no setup events: TcpRequestConnect, TcpConnectTcbProceeding, TcpConnectTcbComplete
			Likely, that setup happened before the trace began.  And strangely, this was not part of the rundown.
		*/
		TcbRecord RestoreTcbRecord(QWord tcb, TcbRecord.MyStatus status, in TimestampETW timeStamp, IDVal pidAlt, in SocketAddress addrLocal, in SocketAddress addrRemote)
		{
			AssertCritical(pidAlt != pidIdle);

			TcbRecord tcbR = new TcbRecord(tcb);
			this.Add(tcbR);

			tcbR.pidAlt = pidAlt;

			tcbR.HandleOpenRecord(status, pidUnknown, tidUnknown, in timeStamp, addrLocal, addrRemote);

			return tcbR;
		}


		/*
			Return the 1-based index if the most recent, UDP receive event's TcbRecord with the given IP Address, cb, etc.
			Mark it as correlated with Winsock.
		*/
		public uint CorrelateUDPRecvEvent(IDVal pid, IDVal tid, uint cb, ushort socket, IPEndPoint ipAddr)
		{
			TcbRecord tcbr = null;

			if (udpRecvCache.FMatch(ipAddr, pid, cb))
			{
				if (FImplies(tid != tidUnknown, udpRecvCache.tcbr.tidRecvCache == tid) && FImplies(socket != 0, udpRecvCache.tcbr.socket == socket))
				{
					tcbr = udpRecvCache.tcbr;
					AssertImportant(tcbr.cbRecvCache == cb);

					tcbr.tidRecvCache = tidUnknown;
					tcbr.cbRecvCache = 0;

					udpRecvCache.Reset();
				}
			}

			int iTCB;

			// Search by most recent event.

			if (tcbr != null)
			{
				iTCB = this.LastIndexOf(tcbr);
				AssertCritical(iTCB >= 0);
				if (iTCB < 0) return 0;
			}
			else
			{
				if (tid != tidUnknown)
					iTCB = this.FindLastIndex(t => t.tidRecvCache == tid && t.FMatchCb(pid, cb, socket, in ipAddr));
				else
					iTCB = this.FindLastIndex(t => t.FMatchCb(pid, cb, socket, in ipAddr));

				if (iTCB >= 0)
				{
					tcbr = this[iTCB];
					tcbr.cbRecvCache = 0; // match by size only once
				}
				else
				{
					// It may still be that other UDP.EndpointReceiveMessage events were emitted after the one that matches this event. 
					// But if the PID, socket, and address match, then we can still trust it.
					AssertImportant(pid != pidUnknown);
					AssertInfo(socket != 0); // zero near the start of a trace

					if (tid != tidUnknown)
						iTCB = this.FindLastIndex(t => t.tidRecvCache == tid && t.FMatch(pid, socket, in ipAddr));
					else
						iTCB = this.FindLastIndex(t => t.FMatch(pid, socket, in ipAddr));

					if (iTCB < 0) return 0;

					tcbr = this[iTCB];
				}
			}

			AssertImportant(FImplies(socket != 0, tcbr.socket == socket));
			AssertImportant(tcbr.cbRecv >= cb);

			tcbr.SetType(Protocol.Winsock);

			return (uint)(iTCB + 1); // 1-based
		}


		/*
			Correlate a TCP with WebIO, Winsock.
			For WebIO/Winsock we typically have this sequence on the same thread:
				WebIO:   ConnectionSocketConnect.Start - IP:Port, ProcessId, ThreadId, Connection
				Winsock: AfdConnectExWithAddress       - IP:Port, ProcessId, ThreadId
				TcpIp:   TcpRequestConnect             - IP:Port, ProcessId, ThreadId, Tcb
				TcpIp:   TcpConnectTcbProceeding       - IP:Port, ProcessId, ThreadId, Tcb
				TcpIp:   TcpConnectTcbComplete         - IP:Port, Target_ProcessId, Tcb // OFTEN VERY DELAYED!
				WebIO:   ConnectionSocketConnect.Stop  - IP:Port, ProcessId, ThreadId?, Connection
			We can correlate these records to fairly confidently attach a Tcb to a Connection.
			WinINet correlates in: Request.CloseRequest
		*/
		void CorrelateConnection(TcbRecord tcbR, in SocketAddress addrLocal, in SocketAddress addrRemote, IDVal pid, IDVal tid, in TimestampETW timeStamp)
		{
			AssertCritical(pid != pidIdle);

			if (addrRemote == null) return;

			ushort socketId = addrLocal?.Port() ?? (ushort)0;

			AssertImportant(tcbR != null);

			if (tcbR == null) return;

			AssertImportant(tcbR.addrRemote.Equals(NewEndPoint(addrRemote)));
			AssertImportant(tcbR.socket == socketId);

			uint iTCB = IFromTcbr(tcbR);

			// WinINet is not here because this is too early, with too little info to correlate.
			// WinINet correlates (in the opposite direction) via: TcpIp.CorrelateByAddress

			WinsockAFD.Connection cxn = this.allTables.wsTable.CorrelateByAddress(tcbR.addrRemote, iTCB, socketId, pid, tid);

			WebIO.Socket wioSocket = null;

			// Try to correlate a WebIO Socket to the TCB via Winsock Connection

			if (cxn != null)
				wioSocket = this.allTables.webioTable.requestTable.CorrelateByTimeThread(iTCB, pid, cxn.tidConnect, cxn.timeCreate, cxn.timeConnect);

			if (wioSocket == null)
			{
				// Try to correlate a WebIO socket to the TCB via thread, DNS, etc. This is a bit more complex and less certain.

				uint iDNS = this.allTables.dnsTable.IFindDNSEntryByIPAddress1(tcbR.addrRemote.Address, out uint iAddr);
				wioSocket = this.allTables.webioTable.requestTable.CorrelateByAddress(iTCB, iDNS, iAddr, pid, tid);
			}

			if (cxn == null && wioSocket == null) return;

			if (cxn != null)
			{
				AssertImportant(tcbR.CheckType(Protocol.Winsock));
				AssertImportant(cxn.iTCB == iTCB);
				AssertImportant(timeStamp.ToGraphable().Between(cxn.timeConnect, cxn.timeClose));
			}

			if (wioSocket != null)
			{
#if DEBUG
				// Extra validation that we got the right WebIO Socket.
				uint iDNS = this.allTables.dnsTable.IFindDNSEntryByIPAddress1(tcbR.addrRemote.Address, out uint iAddr);
				AssertImportant(wioSocket.iDNS == iDNS);
				AssertImportant(wioSocket.iAddr == iAddr);
				AssertImportant(wioSocket.port == (uint)tcbR.addrRemote.Port);
				AssertImportant(wioSocket.iTCB == iTCB);
				AssertImportant(timeStamp.ToGraphable().Between(wioSocket.timeStart, wioSocket.timeClose));
#endif // DEBUG

				tcbR.SetType(Protocol.WinHTTP);

				if (cxn != null)
				{
					AssertImportant((cxn.grbitType & (byte)Protocol.Winsock) != 0);
					cxn.grbitType |= (byte)Protocol.WinHTTP;
#if DEBUG
					if (wioSocket.cxnWinsock == null)
						wioSocket.cxnWinsock = cxn;
					else
						AssertImportant(wioSocket.cxnWinsock == cxn);
#endif // DEBUG
				}
			}
		} // CorrelateConnection


		/*
			Find the/a TCB entry that corresponds to this address & process.
			Try hard to find the closest reasonable match.
			Note that in some cases addr.Port==0, perhaps after a WinINet Redirect.
		*/
		public uint CorrelateByAddress(in IPEndPoint addr, IDVal pid, IDVal tid, uint socket, Protocol bitfType)
		{
			uint iTcbFound1 = 0;
			uint iTcbFound2 = 0;
			uint iTcbFound3 = 0;

			AssertCritical(pid != pidIdle);
			AssertImportant(!addr.Empty());
			if (addr.Empty()) return 0;

			AssertCritical(addr.Port > 0);

			TcbRecord tcbR;
			for (int iTcbR = this.Count-1; iTcbR >= 0; --iTcbR)
			{
				tcbR = this[iTcbR];

				AssertCritical(tcbR.addrRemote.Empty() || tcbR.addrRemote.Port > 0);
				if (!tcbR.addrRemote.Empty() ? tcbR.addrRemote.Equals(addr) : (tcbR.pid == pid))
				{
					if (tcbR.socket == socket)
					{
						iTcbFound1 = (uint)iTcbR+1;
						break;
					}

					if (pid != ((tcbR.pid != pidUnknown) ? tcbR.pid : tcbR.pidAlt))
						continue;

					if (tcbR.grbitProtocol == (byte)Protocol.Unknown)
					{
						// Ready to match!

						if (iTcbFound1 == 0 && tcbR.tid == tid)
							iTcbFound1 = (uint)iTcbR+1;
					}
					else if ((tcbR.grbitProtocol & (byte)bitfType) != 0)
					{
						if (iTcbFound2 == 0)
							iTcbFound2 = (uint)iTcbR+1;
					}

					if (iTcbFound3 == 0)
						iTcbFound3 = (uint)iTcbR+1;
				}
			}

			if (iTcbFound1 == 0)
			{
				iTcbFound1 = iTcbFound2;

				if (iTcbFound1 == 0)
					iTcbFound1 = iTcbFound3;
			}

			AssertInfo(iTcbFound1 != 0);

			tcbR = TcbrFromI(iTcbFound1);
			tcbR?.SetType(bitfType);

			return iTcbFound1;
		}


		/*
			TcpIp Send/Receive records may need to correlate with higher level events, eg. WebIO.

			There is a defect in older versions of Windows/WebIO which always get 0 data with the ConnectionSocketSend events.
			Pipe through these TCP Send counts to compensate (if !webioTable.fHaveSendCounts).
			There is no such defect in the ConnectionSocketReceive events.
		*/
		void CorrelateSendRecv(IDVal pid, IDVal tid, TcbRecord tcbr, in TimestampUI timeStamp, uint cbSend)
		{
			AssertCritical(tcbr != null);

			// Toward the beginning of a trace we may get Send/Recv events without corresponding setup and correlation.
			// Do remedial correlate here.

			// Already correlated, and no Send bytes to register, or they're not needed?
			if ((cbSend == 0 || this.allTables.webioTable.fHaveSendCounts) && tcbr.fCorrelatedSendRecv)
				return;

			tcbr.fCorrelatedSendRecv = true;

			WebIO.Connection cxn = this.allTables.webioTable.requestTable.EnsureConnectionTCB(pid, tid, IFromTcbr(tcbr), in timeStamp, cbSend != 0);
			if (cxn != null)
			{
				tcbr.SetType(Protocol.WinHTTP);

				AssertImportant(cxn.cbSendTCB == 0);
				cxn.cbSendTCB = cbSend;

				if (cxn.socket.iDNS == 0)
				{
					cxn.socket.iDNS = this.allTables.dnsTable.IFindDNSEntryByIPAddress1(tcbr.addrRemote.Address, out cxn.socket.iAddr);

					if (cxn.socket.iDNS == 0)
						cxn.socket.iAddr = this.allTables.dnsTable.AddDNSEntry(strNA, tcbr.addrRemote.Address, ref cxn.socket.iDNS);
				}
			}
		}


		static QWord GetTCB(in IGenericEvent evt) => evt.GetAddrValue("Tcb");

		static IDVal GetProcessId(in IGenericEvent evt, string strField = "ProcessId")
		{
			IDVal pid = (IDVal)evt.GetUInt32(strField);
			return (pid != pidIdle) ? pid : pidUnknown;
		}

		static void AssertStatus(in IGenericEvent evt)
		{
			AssertImportant(evt.GetUInt32("Status") == 0);
		}


		public static readonly Guid guid = new Guid("{2f07e2ee-15db-40f1-90ef-9d7ba282188a}"); // Microsoft-Windows-TCPIP

		/*
			Only the following events have a reliable Process ID:
			TCP.RequestConnect
			TCP.InspectConnectComplete // always nearby
			TCP.TcbSynSend // always nearby
			TCP.InspectConnectWithNameResContext
			TCP.ConnectTcbProceeding
			TCP.SendPosted?
			TCP.ReceiveRequest?
			UDP.CloseEndpointBound
			// And those which have an explicit ProcessId/Pid field (when not 0).
		*/

		enum TCP
		{
			RequestConnect = 1002,
			AcceptListenerComplete = 1017,
			ConnectTcbProceeding = 1031,
			ConnectTcbComplete = 1033,
			ConnectTcbFailure = 1034,
			CloseTcbRequest = 1038,
			AbortTcbRequest = 1039, // unused
			DisconnectTcbComplete = 1043, // unused
			ShutdownTcb = 1044,
			ConnectTcbTimeout = 1045, // unused
			DataTransferReceive = 1074,
			ReceiveRequest = 1156,
			SendPosted = 1159,
			ConnectionRundown = 1300,
			DataTransferSend = 1332,
			InspectConnectWithNameResContext = 1382, // rare!
		};

		enum UDP
		{
			EndpointSendMessages = 1169,
			EndpointReceiveMessages = 1170,
			CloseEndpointBound = 1397,
			InetInspect = 1454 // far too noisy!
		};

		public void Dispatch(in IGenericEvent evt)
		{
			uint cb;
			ushort socket;
			IDVal pid, tid;
			QWord tcb;
			TcbRecord tcbr;
			SocketAddress addrLocal, addrRemote;
			TimestampUI timeStamp;
			WinsockAFD.Connection cxn;

			switch ((TCP)evt.Id)
			{
			case TCP.RequestConnect:
				// evt.ProcessId is usually reliable, unless it's pidSystem
				tcb = GetTCB(evt);
				addrLocal = evt.GetLocalAddress();
				addrRemote = evt.GetRemoteAddress();

				// This does HandleOpenRecord:
				tcbr = AddTcbRecord(tcb, TcbRecord.MyStatus.Connect_Request, evt.Timestamp, evt.ProcessId, evt.ThreadId, in addrLocal, in addrRemote);
				CorrelateConnection(tcbr, in addrLocal, in addrRemote, evt.ProcessId, evt.ThreadId, evt.Timestamp);
				break;

			case TCP.ConnectTcbProceeding:
				tid = tidUnknown;
				pid = GetProcessId(in evt); // usually unknown

				AssertStatus(in evt);

				tcbr = FindTcbRecord(GetTCB(evt), pid);

				addrLocal = evt.GetLocalAddress();
				addrRemote = evt.GetRemoteAddress();

				if (pid == pidUnknown || pid == evt.ProcessId)
				{
					// evt.ProcessId is reliable?
					pid = evt.ProcessId;
					tid = evt.ThreadId;
				}

				CorrelateConnection(tcbr, in addrLocal, in addrRemote, pid, tid, evt.Timestamp);
				break;

			case TCP.ConnectTcbComplete:
				// evt.ProcessId is usually unreliable
				IDVal pidField = pid = GetProcessId(in evt); // sometimes unknown
				tcb = GetTCB(evt);

				AssertStatus(in evt);

				tcbr = FindTcbRecord(tcb, pid);
				AssertImportant(tcbr != null);

				addrLocal = evt.GetLocalAddress();
				addrRemote = evt.GetRemoteAddress();

				tid = tidUnknown;
				if (pid == evt.ProcessId || (pid == pidUnknown && tcbr?.pid == evt.ProcessId))
				{
					// These are now assumed to be reliable.
					pid = evt.ProcessId;
					tid = evt.ThreadId;
				}

				if (tcbr != null)
				{
					tcbr.HandleOpenRecord(TcbRecord.MyStatus.Connect_Complete, pid, tid, evt.Timestamp, addrLocal, in addrRemote);
				}
				else
				{
					tcbr = RestoreTcbRecord(tcb, TcbRecord.MyStatus.Inferred, evt.Timestamp, pid, addrLocal, addrRemote);
					tcbr.EnsurePID(pidField);
				}

				// ThreadId doesn't correlate here.
				CorrelateConnection(tcbr, in addrLocal, in addrRemote, pid, tidUnknown, evt.Timestamp);
				break;

			case TCP.ConnectTcbFailure:
				// evt.ProcessId is unreliable
				pid = GetProcessId(in evt); // usually unknown
				tcb = GetTCB(evt);
				// Status is usually non-zero

				tcbr = FindTcbRecord(tcb, pid);
				// The record may already be closed/shutdown.
				AssertInfo(tcbr != null);

				if (tcbr != null)
				{
					timeStamp = evt.Timestamp.ToGraphable();
					addrRemote = evt.GetRemoteAddress();
					tcbr.HandleCloseRecord(TcbRecord.MyStatus.Connect_Fail, pid, in timeStamp, in addrRemote);
				}
				break;

			case TCP.AcceptListenerComplete:
				pid = GetProcessId(in evt);
				tcb = GetTCB(evt);

				AssertStatus(in evt);

				tcbr = FindTcbRecord(tcb, pid);

				addrRemote = evt.GetRemoteAddress();
				addrLocal = evt.GetLocalAddress();

				if (tcbr != null)
				{
					tcbr.HandleOpenRecord(TcbRecord.MyStatus.Inferred, pid, tidUnknown, evt.Timestamp, in addrLocal, in addrRemote);
				}
				else
				{
					tcbr = RestoreTcbRecord(tcb, TcbRecord.MyStatus.Inferred, evt.Timestamp, pid, addrLocal, addrRemote);
					tcbr.EnsurePID(pid);
				}

				// Here the actual ThreadId matches the ThreadId of WebIO.AFD.AcceptExWithAddress
				cxn =  this.allTables.wsTable.CorrelateListener(tcbr, pid, (pid==evt.ProcessId) ? evt.ThreadId : tidUnknown);
				break;

			case TCP.CloseTcbRequest:
				// evt.ProcessId is unreliable
				pid = GetProcessId(in evt); // usually unknown
				tcb = GetTCB(evt);

				AssertStatus(in evt);

				tcbr = FindTcbRecord(tcb, pid);

				if (tcbr != null)
				{
					timeStamp = evt.Timestamp.ToGraphable();
					addrRemote = evt.GetRemoteAddress();
					tcbr.HandleCloseRecord(TcbRecord.MyStatus.Close, pid, in timeStamp, in addrRemote);
				}
				break;

			case TCP.ShutdownTcb:
				// evt.ProcessID is unreliable
				pid = GetProcessId(in evt); // reliable
				tcb = GetTCB(evt);

				// Status is usually non-zero

				// TCB shutdown begins, but send/receive events may still happen (flushing?).

				tcbr = FindTcbRecordClosed(tcb, pid);

				AssertImportant(pid != pidUnknown); // else test evt.ProcessId against tcbr.pid?

				if (tcbr != null)
				{
					timeStamp = evt.Timestamp.ToGraphable();
					addrRemote = evt.GetRemoteAddress();
					tcbr.HandleCloseRecord(TcbRecord.MyStatus.Shutdown, pid, in timeStamp, in addrRemote);
				}
				break;

			case TCP.ConnectionRundown:
				// evt.ProcessId/ThreadId will always be that of XPerf/WPR.exe!  Ignore that.
				pid = GetProcessId(evt, "Pid");
				tcb = GetTCB(evt);

				addrLocal = evt.GetLocalAddress();
				addrRemote = evt.GetRemoteAddress();

				tcbr = AddTcbRecord(tcb, TcbRecord.MyStatus.Rundown, evt.Timestamp, pid, tidUnknown, in addrLocal, in addrRemote);
				tcbr.EnsurePID(pid);
				break;

			case TCP.DataTransferReceive:
				// evt.ProcessId is unreliable
				tcb = GetTCB(evt);
				cb = evt.GetUInt32("NumBytes");

				if (cb == 0) break;

				tcbr = FindTcbRecord(tcb, pidUnknown);

				if (tcbr == null)
					tcbr = RestoreTcbRecord(tcb, TcbRecord.MyStatus.Inferred, evt.Timestamp, pidUnknown, null, null);

				tcbr.cbRecv += cb;
				break;

			case TCP.DataTransferSend:
				// evt.ProcessId is unreliable
				tcb = GetTCB(evt);
				cb = evt.GetUInt32("BytesSent");

				if (cb == 0) break;

				tcbr = FindTcbRecord(tcb, pidUnknown);

				if (tcbr == null)
					tcbr = RestoreTcbRecord(tcb, TcbRecord.MyStatus.Inferred, evt.Timestamp, pidUnknown, null, null);

				tcbr.cbSend += cb;
				break;

			// There is a defect in older versions of Windows/WebIO
			// which always get 0 data with the ConnectionSocketSend events.
			// Just in case, pipe through these TCP Send events to compensate.
			// There is no such defect in the ConnectionSocketReceive events.
			// In any case, record the Post byte count for bookkeeping.

			case TCP.SendPosted:
				cb = evt.GetUInt32("NumBytes");

				if (cb == 0) break;

				tcb = GetTCB(evt);
				pid = evt.ProcessId; // reliable? Use for Restore and pidAlt
				if (pid == 0)
					pid = pidUnknown;

				tcbr = FindTcbRecord(tcb, pidUnknown);
				if (tcbr != null)
				{
					if (tcbr.pidAlt == pidUnknown)
						tcbr.pidAlt = pid;
				}
				else
				{
					tcbr = RestoreTcbRecord(tcb, TcbRecord.MyStatus.Inferred, evt.Timestamp, pid, null, null);
				}

				timeStamp = evt.Timestamp.ToGraphable();

				CorrelateSendRecv(evt.ProcessId, evt.ThreadId, tcbr, in timeStamp, cb);

				tcbr.cbPost += cb;
				break;

			case TCP.ReceiveRequest:
				cb = (uint)evt.GetAddrValue("NumBytes");

				if (cb == 0) break;

				tcb = GetTCB(evt);
				pid = evt.ProcessId; // reliable? Use for Restore and pidAlt
				if (pid == 0)
					pid = pidUnknown;

				tcbr = FindTcbRecord(tcb, pidUnknown);

				if (tcbr != null)
				{
					if (tcbr.pidAlt == pidUnknown)
						tcbr.pidAlt = pid;
				}
				else
				{
					tcbr = RestoreTcbRecord(tcb, TcbRecord.MyStatus.Inferred, evt.Timestamp, pid, null, null);
				}

				timeStamp = evt.Timestamp.ToGraphable();

				CorrelateSendRecv(evt.ProcessId, evt.ThreadId, tcbr, in timeStamp, 0);
				break;

			case TCP.InspectConnectWithNameResContext:
				tcb = GetTCB(evt);
				pid = evt.ProcessId; // reliable
				if (pid <= TcbRecord.pidSystem)
					pid = pidUnknown;

				AssertStatus(in evt);

				addrRemote = evt.GetRemoteAddress();
				IPEndPoint ipEndPoint = NewEndPoint(addrRemote);

				uint iDNSCache = 0;
				this.allTables.dnsTable.AddDNSEntry(evt.GetString("DnsName"), ipEndPoint.Address, ref iDNSCache);

				tcbr = FindTcbRecord(tcb, pid);
				AssertImportant(tcbr != null);

				if (tcbr == null)
					tcbr = RestoreTcbRecord(tcb, TcbRecord.MyStatus.Inferred, evt.Timestamp, pid, null, addrRemote);

				AssertCritical(tcbr.addrRemote.Address.Equals(ipEndPoint.Address));
				AssertImportant(tcbr.addrRemote.Port == 0 || tcbr.addrRemote.Port == ipEndPoint.Port);

				tcbr.addrRemote.Port = ipEndPoint.Port;
				break;

			default:
			// Transition from TCP to UDP
			switch ((UDP)evt.Id)
			{
			// The InetInspect record (ID=1454) has InspectType = CreateEndpoint. But InetInspect is far too noisy, therefore CreateEndpoint will always be inferred.

			case UDP.EndpointSendMessages:
			case UDP.EndpointReceiveMessages:
				pid = GetProcessId(in evt, "Pid"); // reliable
				tcb = evt.GetAddrValue("Endpoint"); // Not exactly a TCB
				tcbr = FindTcbRecord(tcb/*pseudo-TCB*/, pid);

				addrRemote = (evt.GetUInt32("RemoteSockAddrLength") != 0) ? evt.GetSocketAddress("RemoteSockAddr") : null;
				addrLocal = (evt.GetUInt32("LocalSockAddrLength") != 0) ? evt.GetSocketAddress("LocalSockAddr") : null;
				socket = addrLocal?.Port() ?? 0;

				if ((UDP)evt.Id == UDP.EndpointReceiveMessages)
				{
					// There may be a lingering Receive message after the TCB was closed.
					if (tcbr == null)
						tcbr = FindTcbRecordClosed(tcb/*pseudo-TCB*/, pid);

					// Sometimes the addresses are swapped.
					if (tcbr != null && socket != tcbr.socket && addrRemote?.Port() == tcbr.socket && (NewEndPoint(addrLocal).Equals(tcbr.addrRemote)))
					{
						SocketAddress addrT = addrLocal;
						addrLocal = addrRemote;
						addrRemote = addrT;
						socket = addrLocal?.Port() ?? 0;
					}
				}

				IPEndPoint ipAddrRemote = NewEndPoint(addrRemote);

				if (tcbr == null)
				{
					IDVal pidAlt = (evt.ProcessId > TcbRecord.pidSystem) ? evt.ProcessId : pidUnknown;
					tcbr = RestoreTcbRecord(tcb/*pseudo-TCB*/, TcbRecord.MyStatus.Inferred, evt.Timestamp, pidAlt, addrLocal, addrRemote);
					tcbr.pid = pid;
					tcbr.tid = evt.ThreadId;
					tcbr.fUDP = true;
				}
				else
				{
					// UDP can have different Remote Addresses on the same Endpoint.
					// Find the record with matching address.
					// If needed, create separate records and link them backward in time.

					TcbRecord tcbrFound;
					for (tcbrFound = tcbr; tcbrFound != null; tcbrFound = TcbrFromI(tcbrFound.iNext))
					{
						AssertCritical(tcbrFound.fUDP);
						AssertCritical(tcbrFound.tcb == tcb);
						AssertCritical(tcbrFound.pid == pid);
						AssertCritical(tcbrFound.status == TcbRecord.MyStatus.Inferred || tcbrFound.status == TcbRecord.MyStatus.Close);

						if (tcbrFound.addrRemote.Equals(ipAddrRemote) && socket == tcbrFound.socket)
							break;
					}
					if (tcbrFound == null)
					{
						tcbrFound = (TcbRecord)tcbr.Clone(ipAddrRemote);
						tcbrFound.iNext = IFromTcbr(tcbr);
						tcbrFound.socket = socket;
						Add(tcbrFound);
					}
					tcbr = tcbrFound;
				}

				tcbr.EnsurePID(pid);
				AssertImportant(tcbr.fUDP);

				cb = (uint)evt.GetUInt32("NumBytes");
				if ((UDP)evt.Id == UDP.EndpointSendMessages)
				{
					// Correlate this UDP Send event with the preceeding Winsock Send event.
					// AFD.SendMessageWithAddress TID cb Address
					// UDP.EndpointSendMessages   TID cb RemoteSockAddr

					cxn =  this.allTables.wsTable.CorrelateUDPSendEvent(pid, evt.ThreadId, cb, socket, ipAddrRemote);

					if (cxn != null)
					{
						if (cxn.iTCB == 0)
							cxn.iTCB = this.IFromTcbr(tcbr);
						else
							AssertImportant(cxn.iTCB == this.IFromTcbr(tcbr));

						if (cxn.socket == 0)
							cxn.socket = socket;
						else
							AssertImportant(cxn.socket == socket);

						AssertImportant(tcbr.addrRemote.Address.Equals(cxn.addrRemote.Address));
						
						tcbr.SetType(Protocol.Winsock);
					}

					tcbr.cbSend += cb;
				}
				else
				{
					// Note that this UDP Receive event with the preceeds the Winsock Receive event.

					tcbr.tidRecvCache = evt.ThreadId;
					tcbr.cbRecvCache = cb;
					tcbr.cbRecv += cb;

					this.udpRecvCache.Set(pid, cb, tcbr);
				}
				break;

			case UDP.CloseEndpointBound:
				AssertStatus(in evt);
				tcb = evt.GetAddrValue("Endpoint"); // Not exactly a TCB
				pid = evt.ProcessId;
				socket = evt.GetLocalAddress()?.Port() ?? 0;

				// For UDP there can be various TCB (really, Endpoint) records with different addresses.
				for (tcbr = FindTcbRecord(tcb/*pseudo-TCB*/, pidUnknown); tcbr != null; tcbr = TcbrFromI(tcbr.iNext))
				{
					AssertCritical(tcbr.fUDP);
					AssertCritical(tcbr.tcb == tcb);
					AssertCritical(tcbr.pid == pid);
					AssertCritical(tcbr.status == TcbRecord.MyStatus.Inferred);
					AssertCritical(tcbr.socket == socket || tcbr.addrRemote.Port == socket);

					timeStamp = evt.Timestamp.ToGraphable();
					tcbr.HandleCloseRecord(TcbRecord.MyStatus.Close, pid, in timeStamp, null/*addrRemote*/);

					// Correlate with WinSock Datagram / UDP record.

					uint iTCB = IFromTcbr(tcbr);
					cxn = this.allTables.wsTable.CorrelateByAddress(tcbr.addrRemote, iTCB, tcbr.socket, pid, tidUnknown);
					if (cxn != null)
					{
						AssertImportant(cxn.socktype == WinsockAFD.SOCKTYPE.SOCK_DGRAM);
						AssertImportant(cxn.ipProtocol == WinsockAFD.IPPROTO.UDP);
						AssertImportant(cxn.iTCB == iTCB);
						AssertImportant(evt.Timestamp.ToGraphable().Between(cxn.timeConnect, cxn.timeClose));
						AssertImportant(tcbr.CheckType(Protocol.Winsock));
					}
				}
				break;
			}
				break;
			}
		}
	}
}

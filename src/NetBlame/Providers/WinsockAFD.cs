// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic; // List<>
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices; // MethodImpl

using Microsoft.Windows.EventTracing.Events;
using Microsoft.Windows.EventTracing.Symbols;

using NetBlameCustomDataSource.Link;

using static NetBlameCustomDataSource.Util;

using TimestampETW = Microsoft.Windows.EventTracing.TraceTimestamp;
using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using AddrVal = System.UInt64;

using IDVal = System.Int32; // Process/ThreadID (ideally UInt32)

using ProcessHash = System.Collections.Generic.Dictionary<System.UInt64, System.Int32>;


namespace NetBlameCustomDataSource.WinsockAFD
{
	public enum IPPROTO : byte
	{
		IP            = 0,  // IPv4
		HOPOPTS       = 0,  // IPv6 Hop-by-Hop options
		ICMP          = 1,
		IGMP          = 2,
		GGP           = 3,
		IPV4          = 4,
		ST            = 5,
		TCP           = 6,  // (stream socket)
		CBT           = 7,
		EGP           = 8,
		IGP           = 9,
		PUP           = 12,
		UDP           = 17, // (datagram socket)
		IDP           = 22,
		RDP           = 27,
		HyperV        = 34, // pseudo: AF_HYPERV
		VSock         = 40, // pseudo: AF_VSOCK
		IPV6          = 41, // IPv6 header
		ROUTING       = 43, // IPv6 Routing header
		FRAGMENT      = 44, // IPv6 fragmentation header
		ESP           = 50, // encapsulating security payload
		AH            = 51, // authentication header
		ICMPV6        = 58, // ICMPv6
		NONE          = 59, // IPv6 no next header
		DSTOPTS       = 60, // IPv6 Destination options
		ND            = 77,
		ICLFXBM       = 78,
		PIM           = 103,
		PGM           = 113,
		L2TP          = 115,
		SCTP          = 132,
		RAW           = 255
	}

	public enum SOCKTYPE : byte
	{
	//	SocketType field (AFD.Create record when EnterExit==0):
		SOCK_NULL      = 0,
		SOCK_STREAM    = 1, // stream socket   (TCP) (common)
		SOCK_DGRAM     = 2, // datagram socket (UDP) (common)
		SOCK_RAW       = 3, // raw-protocol interface
		SOCK_RDM       = 4, // reliably-delivered message
		SOCK_SEQPACKET = 5  // sequenced packet stream
	}

	public class Connection : IGraphableEntry
	{
		public TimestampETW timeRef;

		public TimestampUI timeCreate;
		public TimestampUI timeConnect; // 0 = uninitialized, timeMax = correlating, timeN = initialized
		public TimestampUI timeClose;

		public AddrVal qwEndpoint; // not unique
		public AddrVal hProcess; // WinsockTable.hashProcess maps hProcess -> pid ... when available.

		public IDVal pid;
		public IDVal tidOpen;
		public IDVal tidConnect; // Connect*WithAddress / AcceptExWithAddress
		public IDVal tidClose;

		public uint iDNS; // 1-based
		public uint iTCB; // 1-based
#if DEBUG
		public uint cbSendNested; // redundant
		public SocketAddress addrLocal;
#endif
		public uint cbSend;
		public uint cbSendConnect; // Connect*WithAddress
		public uint cbRecv;
		public uint status;

		public bool fSuperConnect; // AFD_SUPER_CONNECT: Not LDAP, Kerberos, etc.
		public byte grbitType; // Protocol Layers: Winsock/LDAP, including WinINet/WinHTTP when linked to this Connection

		public ushort socket; // for correlating with TCB
		public IPPROTO ipProtocol;
		public SOCKTYPE socktype;
		public IPEndPoint addrRemote;

		public Connection cxnNext; // Next UDP Datagram with a different address

		public XLink xlink;

		public IStackSnapshot stack;

		public bool FClosed => !this.timeClose.HasMaxValue();


		public Connection(AddrVal qwEndpoint, IDVal pid, IDVal tid, in TimestampETW timeStamp)
		{
			this.grbitType = (byte)Protocol.Winsock;
			this.qwEndpoint = qwEndpoint;
			this.timeRef = timeStamp;
			this.timeCreate = timeStamp.ToGraphable();
			this.pid = pid;
			this.tidOpen = tid;
			this.timeConnect = TimestampUI.Zero;
			this.timeClose.SetMaxValue();
		}


		public bool MatchAddr(uint iTCB, uint socket, IDVal pid, IDVal tid, IPEndPoint addrRemote)
		{
			if (this.socket != socket) return false;
			if (this.pid != WinsockTable.pidUnknown && this.pid != pid) return false;
			if (tid != WinsockTable.tidUnknown && this.tidConnect != tid) return false;
			if (this.iTCB != 0)
				return this.iTCB == iTCB;
			else
				return this.addrRemote?.Equals(addrRemote) ?? false;
		}

		public bool MatchUDPAddr(IDVal pid, ushort socket, IPEndPoint addr)
		{
			if (this.ipProtocol != IPPROTO.UDP) return false;
			if (this.socktype == SOCKTYPE.SOCK_RAW) return false;
			if (this.pid != pid) return false;
			if (this.socket != 0 && this.socket != socket) return false;
			return (this.addrRemote?.Equals(addr) ?? false);
		}

		public Connection Clone(in IPEndPoint ipAddr)
		{
			Connection cxn = (Connection)this.MemberwiseClone();
			cxn.addrRemote = ipAddr;
			cxn.cxnNext = null;
			cxn.iTCB = 0;
			return cxn;
		}

		// Implement IGraphableEntry
		public IDVal Pid => this.pid;
		public IDVal TidOpen => this.tidOpen;
		public TimestampETW TimeRef => this.timeRef;
		public TimestampUI TimeOpen => this.timeCreate;
		public TimestampUI TimeClose => this.timeClose;
		public IStackSnapshot Stack => this.stack;
		public XLinkType LinkType => this.xlink.typeNext;
		public uint LinkIndex => this.xlink.IFromNextLink;
	}

	/*
		This table of WinSock connections grows and shrinks.
		*** Do not hold an index. ***
	*/
	public class WinsockTable : List<Connection>
	{
		readonly AllTables allTables;

		readonly ProcessHash hashProcess = new ProcessHash(64); // Map Process Handles to Process IDs

		public WinsockTable(int capacity, in AllTables _allTables) : base(capacity) { this.allTables = _allTables; }

		public const IDVal pidUnknown = -1;
		public const IDVal tidUnknown = -1;
		public const IDVal pidSystem = 4;


		// This list/table can shrink (CloseConnection). Do not hold indices.
		public Connection CxnFromI(uint iCxn) => null;
		public uint IFromCxn(Connection cxn) => 0;


		/*
			Return the most recent, matching, open Connection record.
			Used by AFD.BindWithAddress/.Connect*WithAddress/.AcceptExWithAddress
			to match the most recent AFD.Create (not closed) with the given Endpoint value.
			HIGH TRAFFIC FUNCTION!
		*/
		Connection FindConnection(AddrVal qwEndpoint, AddrVal hProc)
		{
			Connection cxn = this.FindLast(c => c.qwEndpoint == qwEndpoint);

			if (cxn == null)
				return null;

			if (cxn.FClosed)
				return null;

			// Here we may have another chance to set the Process ID.
			ConfirmProcess(cxn);

			AssertCritical(cxn.hProcess == hProc);

			return (cxn.hProcess == hProc) ? cxn : null;
		}


		/*
			Try to find the connection, which may be closed.
			If it IS closed then do extra validation: confirm it's the same process.
			Used with: AFD.Send/.Receive
			Can return null!
		*/
		Connection FindConnection2(AddrVal qwEndpoint, AddrVal hProc)
		{
			Connection cxn = this.FindLast(c => c.qwEndpoint == qwEndpoint);

			if (cxn == null)
				return null;

			// Here we may have another chance to set the Process ID.
			ConfirmProcess(cxn);

			// If this Connection is closed, then make sure it's not from another process.
			// If this Connection is open, then it should NEVER be from another process.

			AssertCritical(FImplies(!cxn.FClosed, cxn.hProcess == hProc));

			return (cxn.hProcess == hProc) ? cxn : null;
		}

		/*
			Find the most recent UDP Connection with the given Endpoint (and Process):
				With the same address (.addrRemote), OR
				With a null address, OR
				The most recent with a different address,
				Else null
			HandleUDPAddress will take care of it from there.
		*/
		Connection FindConnectionUDP(AddrVal qwEndpoint, AddrVal hProc, in IPEndPoint addr)
		{
			Connection cxn = FindConnection2(qwEndpoint, hProc);

			// Found the most recent (possibly closed?) Connection with the given Endpoint and Process.
			// Now find a duplicate Connection with the same IPEndPoint (.addrRemote), if any.
			// Because that's what UDP does.

			for (Connection cxnFound = cxn; cxnFound != null; cxnFound = cxnFound.cxnNext)
			{
				AssertCritical(cxnFound.ipProtocol == IPPROTO.UDP);
				AssertImportant(FImplies(cxnFound.cxnNext != null, cxnFound.addrRemote != null));
				if (cxnFound.addrRemote?.Equals(addr) ?? true)
					return cxnFound;
			}

			return cxn;
		}


		/*
			Try to reconstruct the Connection from the current event.
			Perhaps AFD.Create happened before the beginning of the trace.
			Cannot return null.
		*/
		Connection RestoreConnection(AddrVal qwEndpoint, AddrVal hProc, in IGenericEvent evt)
		{
			IDVal pid = GetProcessId(hProc, in evt);
			IDVal tid = (pid == evt.ProcessId) ? evt.ThreadId : tidUnknown;

			AssertImportant(FindConnection2(qwEndpoint, hProc) == null);

			Connection cxn = new Connection(qwEndpoint, pid, tid, evt.Timestamp)
			{
				hProcess = hProc,
				socktype = SOCKTYPE.SOCK_STREAM,
				ipProtocol = IPPROTO.TCP
			};

			this.Add(cxn);

			return cxn;
		}

		Connection RestoreConnection(AddrVal qwEndpoint, in IGenericEvent evt)
		{
			AddrVal hProc = evt.GetAddrValue("Process");
			return RestoreConnection(qwEndpoint, hProc, in evt);
		}

		/*
			Try to reconstruct the Connection from the current event.
			UDP events don't get a Create event (too noisy!), so generate one from the first Send/Receive.
			Cannot return null.
		*/
		Connection RestoreConnectionUDP(AddrVal qwEndpoint, AddrVal hProc, in IPEndPoint addr, IDVal pid, IDVal tid, in TimestampETW timeStamp)
		{
			// Shouldn't find this UDP-Endpoint still open.
			AssertImportant(FindConnection(qwEndpoint, hProc) == null);

			Connection cxn = new Connection(qwEndpoint, pid, tid, timeStamp)
			{
				hProcess = hProc,
				addrRemote = addr,
				ipProtocol = IPPROTO.UDP,
				socktype = SOCKTYPE.SOCK_DGRAM
			};

			this.Add(cxn);

			return cxn;
		}


		/*
			Correlate a Connection with another recent event by process, socket and either iTCB or IPEndPoint.
			If tid != tidUnknown (-1) then also match to: Connection.tidConnect
			Invoked by:
				TCP.RequestConnect/.ConnectTcbProceeding/.ConnectTcbComplete via CorrelateConnection
				UDP.CloseEndpointBound directly
				WINET.HandleClosed/.Connect_Stop via StopRequest
		*/
		public Connection CorrelateByAddress(IPEndPoint addrRemote, uint iTCB, uint socket, IDVal pid, IDVal tid)
  		{
			AssertCritical(pid != pidUnknown);
			AssertCritical(socket != 0);
			AssertImportant(iTCB != 0);
			AssertImportant(!addrRemote.Empty());

			Connection cxn = this.FindLast(c => c.MatchAddr(iTCB, socket, pid, tid, addrRemote));

			if (cxn == null)
			{
				// pid and tid from the TCP record are not always reliable.
				// Look up the Winsock Connection via just the socket and address.

				cxn = this.FindLast(c => c.socket == socket && (c.addrRemote?.Equals(addrRemote) ?? false));

				if (cxn == null || !FImplies(cxn.iTCB != 0, cxn.iTCB == iTCB))
					return null;
			}

			AssertImportant(cxn.pid != pidUnknown); // possible!

			if (cxn.iTCB == 0)
			{
				if (iTCB != 0)
				{
					cxn.iTCB = iTCB;

					TcpIp.TcbRecord tcbr = this.allTables.tcpTable.TcbrFromI(iTCB);
					tcbr.SetType(Protocol.Winsock);

					if (!tcbr.fPidSure && hashProcess.TryGetValue(cxn.hProcess, out pid))
						tcbr.pid = cxn.pid;
				}
			}
			else
			{
				AssertImportant(cxn.iTCB == iTCB);
			}

			if (cxn.pid == pidUnknown)
				cxn.pid = pid;

			return cxn;
		}


		/*
			Mark the type of the most recent Winsock Connection with the given PID, TID, iTCB, etc.
			Used by: TCP.AcceptListenerComplete
		*/
		public Connection CorrelateListener(TcpIp.TcbRecord tcbR, IDVal pid, IDVal tid)
		{
			AssertImportant(tcbR != null);
			AssertCritical(!tcbR.addrRemote.Empty());
			if (tcbR.addrRemote.Empty())
				return null;

			if (this.Count == 0)
				return null;

			Connection cxn;

			if (tid != tidUnknown)
			{
				cxn = this.FindLast(c =>
					c.tidConnect == tid &&
					c.socket == tcbR.socket &&
					(c.addrRemote.Empty() || c.addrRemote.Equals(tcbR.addrRemote))
				);
			}
			else
			{
				cxn = this.FindLast(c =>
					c.socket == tcbR.socket &&
					(c.addrRemote.Empty() || c.addrRemote.Equals(tcbR.addrRemote))
				);
			}

			// This could fire near the start of the trace.
			AssertImportant(cxn != null || pid <= pidSystem);

			if (cxn == null)
				return null;

			if (pid != pidUnknown)
			{
				if (cxn.pid == pidUnknown)
					cxn.pid = pid;
				else
					AssertImportant(cxn.pid == pid);

				this.ConfirmProcess(cxn);
			}

			if (cxn.iTCB == 0)
			{
				tcbR.SetType(Protocol.Winsock);
				cxn.iTCB = allTables.tcpTable.IFromTcbr(tcbR);
			}
			else
			{
				tcbR.CheckType(Protocol.Winsock);
				AssertImportant(cxn.iTCB == allTables.tcpTable.IFromTcbr(tcbR));
			}

			if (cxn.socket == 0)
				cxn.socket = tcbR.socket;
			else
				AssertImportant(cxn.socket == tcbR.socket);

			if (cxn.addrRemote.Empty())
				cxn.addrRemote = tcbR.addrRemote;
			else
				AssertCritical(cxn.addrRemote.Equals(tcbR.addrRemote));

			return cxn;
		}


		/*
			The UDP code correlates a UDP.EndpointSendMessages event with the corresponding WinSock/AFD Connection.
			Used by: UDP.EndpointSendMessages

			Correlate this pattern of SEND events:
				AFD.SendMessageWithAddress Enter PID TID AFD-Endpoint cb Address [socket from AFD.BindWithAddress]
				UDP.EndpointSendMessages         PID TID UDP-Endpoint cb RemoteSockAddr [socket from LocalSockAdr]
		*/
		public Connection CorrelateUDPSendEvent(IDVal pid, IDVal tid, uint cb, ushort socket, IPEndPoint ipAddr)
		{
			AssertCritical(pid >= pidSystem);
			AssertCritical(tid != pidUnknown);
			AssertCritical(socket != 0);
			AssertImportant(ipAddr != null);

			// Correlate via a recent AFD.SendMessageWithAddress event.
			Connection cxn = this.FindLast(c =>
				c.tidConnect == tid &&
				c.cbSendConnect == cb &&
				c.MatchUDPAddr(pid, socket, ipAddr)
				);

			if (cxn != null)
			{
				// It was a one-time correlation.
				cxn.tidConnect = tidUnknown;
				cxn.cbSendConnect = 0;
			}

			return cxn;
		}


		/*
			For UDP Send and Receive events there may be multiple addresses per connection.
			Either set the Connection's address to this one,
			OR extend a chain of linked Connections with the same Endpoint but different addresses.

			Used by: AFD.SendMessageWithAddress/.ReceiveFromWithAddress/.ReceiveMessageWithAddress
		*/
		Connection HandleUDPAddress(Connection cxn, in IPEndPoint ipAddr)
		{
			Connection cxnFound = cxn;

			AssertImportant(!ipAddr.Empty() || cxn.socktype == SOCKTYPE.SOCK_RAW);

			if (cxn.addrRemote == null)
			{
				cxn.addrRemote = ipAddr;
			}
			else
			{
				for (; cxnFound != null; cxnFound = cxnFound.cxnNext)
				{
					if (cxnFound.ipProtocol == IPPROTO.UDP && cxnFound.addrRemote.Equals(ipAddr))
						break;
				}
				if (cxnFound == null)
				{
					cxnFound = cxn.Clone(ipAddr);

					AssertCritical(cxnFound.cxnNext == null);
					cxnFound.cbSend = cxnFound.cbRecv = 0;
#if DEBUG
					cxnFound.cbSendNested = 0;
#endif
					cxnFound.cxnNext = cxn;
					cxnFound.xlink.AddRefLink();
					Add(cxnFound);
				}
			}

			if (cxnFound.iDNS == 0)
				cxnFound.iDNS = allTables.dnsTable.IDNSFromAddress(ipAddr.Address);

			return cxnFound;
		}


		/*
			Do the work for: AFD.Close
		*/
		void CloseConnection(in IGenericEvent evt)
		{
			AssertImportant(evt.GetUInt32("Status") == S_OK);

			TimestampUI timeStamp = evt.Timestamp.ToGraphable();
			AddrVal hProc = evt.GetAddrValue("Process");
			AddrVal addrEndpoint = evt.GetAddrValue("Endpoint");

			Connection cxnPrev = null;
			Connection cxnNext = null;
			for (Connection cxn = FindConnection(addrEndpoint, hProc); cxn != null; cxn = cxnNext)
			{
				AssertImportant(!cxn.FClosed);
				cxnNext = cxn.cxnNext;
				cxn.timeClose = timeStamp;

				if (cxn.addrRemote.Empty() && cxn.socket == 0 && (cxn.cbSend | cxn.cbRecv) == 0)
				{
					// This was one of many useless, unclaimed Open/Close connections
					// which didn't get the event:  BindWithAddress / Connect[Ex]WithAddress
					// Remove it.

					cxn.xlink.Unlink();

					// If there's a list, it's because there are multiple addresses on an _active_ UDP connection.
					AssertImportant(cxnPrev == null && cxn.cxnNext == null);

					if (cxnPrev != null)
						cxnPrev.cxnNext = cxnNext;

					this.Remove(cxn);

					AssertCritical(this.LastIndexOf(cxn) < 0);
				}
				else
				{
					cxn.tidClose = evt.ThreadId;
					cxnPrev = cxn;

					if (cxn.addrRemote == null)
						cxn.addrRemote = new IPEndPoint(0, 0);
					else if (cxn.iDNS == 0)
						cxn.iDNS = this.allTables.dnsTable.IDNSFromAddress(cxn.addrRemote.Address);
				}
			}
		}


		/*
			Nested AFD events can double-count.
			Empirically filter out the double-counters according to their location code.
		*/
		bool FKnownDoubleCountEvent(uint location)
		{
			switch (location)
			{
		// CONFIRMED:
			// AFDETW_TRACEBSEND
			case 3073: // AfdTLBufferedSendCompleteBatch
			case 3024: // AfdRestartBufferSend
				return true;
#if DEBUG
		// UNCONFIRMED:
			// AFDETW_TRACESENDTO
			case 3038: // AfdTLDgramSendToComplete
			case 3039: // AfdRestartSendDatagram
			case 3049: // AfdTLFastDgramSendComplete
			case 3050: // AfdRestartFastDatagramSend
			case 3064: // AfdRioQueueSendCompletion
			case 3202: // AfdFastDatagramSend
			// AFDETW_TRACESENDMSG
			case 3045: // AfdTLSendMsgComplete
			case 3046: // AfdTDISendMsgComplete
			case 3063: // AfdRioQueueSendCompletion
			case 3102: // AfdTLFastDgramSendComplete
			case 3103: // AfdRestartFastDatagramSend
			case 3200: // AfdFastDatagramSend
			// AFDETW_TRACESEND
			case 3007: // AfdSend
			case 3008: // AfdSend
			case 3009: // AfdSend
			case 3010: // AfdSend
			case 3011: // AfdSend
			case 3012: // AfdSend
			case 3015: // AfdSend
			case 3017: // AfdSend
			case 3019: // AfdSend
			case 3026: // AfdRestartSendConnDatagram
			case 3027: // AfdRestartSendTdiConnDatagram
			case 3048: // AfdTLVcSendDgram
			case 3052: // AfdTLVcSendDgramComplete
			case 3053: // AfdFastConnectionSend
			case 3062: // AfdRioQueueSendCompletion
			case 3066: // AfdRioFlushSendQueue
			case 3402: // AfdRestartFastDatagramSend
			default:   // Missed one!?
				AssertImportant(false); // confirm primary counting for these cases
				break;

		// CONFIRMED:
			case 3014: // AfdSend
			case 3018: // AfdSend
			case 3022: // AfdSend
			case 3023: // AfdRestartSend
			case 3025: // AfdCompleteBufferedSendsUnlock
			case 3051: // AfdFastConnectionSend
			case 3201: // AfdFastDatagramSend
			case 3403: // AfdTLFastDgramSendComplete
				break;
#endif // DEBUG
			}
			return false;
		}


		/*
			AFD location codes for operations where evt.ProcessId is empirically believed to be reliable.
		*/
		static readonly HashSet<uint> Reliable = new HashSet<uint>
		{
			// AfdCreate
			/*Enter*/ 1002, 1006, 1015,
			/*Exit*/  1012, 1013,

			// AfdConnectWithAddress
			/*Enter*/ 5023, 5502,

			// AfdConnectExWithAddress
			/*Enter*/ 5031,

			// AfdBindWithAddress
			/*Enter*/ 7010,
			/*Exit*/  7022,

			// AfdSend
			/*Enter*/ 3003, 3047, 3058, 3100,
			/*Exit*/  3014, 3051, 3201,

			// AfdSendMessageWithAddress
			/*Enter*/ 3100, 3044,
			/*Exit*/  3200, 3045,

			// AfdReceive
			/*Enter*/ 4106, 4115, 4117, 4200,
			/*Exit*/  4109, 4110, 4111, 4116, 4118, 4122,

			// AfdReceiveMessageWithAddress
			// AfdReceiveFromWithAddress
			/*Exit*/  4201,

			// AfdClose
			/*Enter*/ 2000, // except when pidSystem
			/*Exit*/  2001, // except when pidSystem

			// AfdAbort
			/*Abort*/ 8000, 8004
		};


		/*
			AFD location codes for operations where evt.ProcessId is empirically known to be unreliable.
		*/
		static readonly HashSet<uint> Unreliable = new HashSet<uint>
		{
#if DEBUG
			// AfdCreate
			// AfdConnectWithAddress
			// AfdConnectExWithAddress
			// AfdBindWithAddress
			/* None */
			
			// AcceptExWithAddress
			/*Exit*/  6101,

			// AfdSend
			/*Enter*/ 3006, 3056,
			/*Exit*/  3018, 3023, 3024, 3025, 3073,

			// AfdSendMessageWithAddress
			/*Exit*/  3102,

			// AfdReceive
			/*Enter*/ 4107,
			/*Exit*/  4114, 4123,

			// AfdReceiveMessageWithAddress
			// AfdReceiveFromWithAddress
			/*Exit*/  4052,

			// AfdClose
			/*Enter*/ // 2000, // when pidSystem
			/*Exit*/  // 2001, // when pidSystem

			// AfdAbort
			/*Abort*/ 8016, 8028
#endif // DEBUG
		};


		/*
			Confirm our understanding of the reliability of evt.ProcessId
			according to the Location value of the event.
		*/
		[System.Diagnostics.Conditional("DEBUG")]
		void TestProcessId(in IGenericEvent evt)
		{
			uint location = evt.GetUInt32("Location");

			if (Unreliable.Contains(location)) // evt.ProcessId could be anything
				return;

			if (evt.ProcessId == pidSystem && (location == 2000 || location == 2001)) // special case for AFD.Close
				return;

			AddrVal hProcess = evt.GetAddrValue("Process");
			if (this.hashProcess.TryGetValue(hProcess, out IDVal pidHash))
			{
				AssertImportant(pidHash == evt.ProcessId); // else location belongs in Unreliable{}

				if (pidHash == evt.ProcessId)
					AssertImportant(Reliable.Contains(location));
				else
					Unreliable.Add(location); // suppress further assertion failures for this location
			}
			else
			{
				AssertImportant(Reliable.Contains(location)); // since it's not in Unreliable{}
			}
		}


		/*
			Build the correlation between process handle (reliable in all WinSock events)
			and a process id. (evt.ProcessId is often unreliable.)
			Most AFD (Winsock) tasks should call either TrySetProcessId or GetProcessId.
		*/
		void SetProcessId(AddrVal hProcess, IDVal pid)
		{
			AssertCritical(pid != pidUnknown);
			this.hashProcess[hProcess] = pid;
		}

		void TrySetProcessId(AddrVal hProcess, IDVal pid)
		{
			AssertImportant(pid != 0);
			AssertCritical(pid != pidUnknown);

			if (!this.hashProcess.TryGetValue(hProcess, out IDVal pidHash))
				SetProcessId(hProcess, pid);
			else
				AssertImportant(pidHash == pid);
		}

		void TrySetProcessId(in IGenericEvent evt)
		{
			uint location = evt.GetUInt32("Location");
			if (Reliable.Contains(location))
				TrySetProcessId(evt.GetAddrValue("Process"), evt.ProcessId);
			else
				AssertImportant(Unreliable.Contains(location));
		}


		/*
			Get a reliable ProcessID from the event, whether or not evt.ProcessId is reliable.
			The Process Handle is reliable, and we have built up a correlation.
			May possibly return: pidUnknown
		*/
		IDVal GetProcessId(AddrVal hProc, IDVal pidEvt, uint location)
		{
			if (hashProcess.TryGetValue(hProc, out IDVal pid))
			{
				AssertImportant(FImplies(Reliable.Contains(location), pid == pidEvt));
				return pid;
			}

			pid = pidUnknown;
			if (Reliable.Contains(location))
			{
				pid = pidEvt;
				this.hashProcess[hProc] = pid; // destructive
			}
			else
			{
				AssertImportant(Unreliable.Contains(location));
			}

			return pid;
		}


		IDVal GetProcessId(AddrVal hProc, in IGenericEvent evt)
		{
			uint location = evt.GetUInt32("Location");
			return GetProcessId(hProc, evt.ProcessId, location);
		}

		/*
			If the connection still has an unknown process, maybe we can update it now.
			Else assert its consistency.
		*/
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		void ConfirmProcess(Connection cxn)
		{
			AssertCritical(cxn.hProcess != 0);

			if (cxn.pid == pidUnknown)
			{
				// Perhaps we've since acquired the Process ID.
				if (this.hashProcess.TryGetValue(cxn.hProcess, out IDVal pid))
					cxn.pid = pid;
			}
			else
			{
				AssertCritical(this.hashProcess.TryGetValue(cxn.hProcess, out IDVal pid) && cxn.pid == pid);
			}
		}


		public static readonly Guid guid = new Guid("{e53c6823-7bb8-44bb-90dc-3f86090d48a6}"); // Microsoft-Windows-WinSock-AFD

		public enum AFD
		{
			Create = 1000,
			Close = 1001,
			Send = 1003,
			Receive = 1004,
			ReceiveFromWithAddress = 1009,
			SendMessageWithAddress = 1013,
			ReceiveMessageWithAddress = 1015,
			ConnectWithAddress = 1018,
			ConnectExWithAddress = 1021,
			AcceptExWithAddress = 1027,
			BindWithAddress = 1030,
			Abort = 1032,
		};

		const uint S_OK = 0;

		public void Dispatch(in IGenericEvent evt)
		{
			uint status;
			uint cb;
			Connection cxn;
			AddrVal hProc;
			AddrVal addrEndpoint;
			IDVal tid, pid;
			IPEndPoint ipAddr;

			switch ((AFD)evt.Id)
			{
			case AFD.Create:
				if (evt.GetUInt32("EnterExit") == 0)
				{
					// On rare occasions, when a process ends, a new process can pick up its handle.
					// We can force a new assocation with this Create event.
					AssertImportant(Reliable.Contains(evt.GetUInt32("Location")));
					pid = evt.ProcessId; // dependable
					hProc = evt.GetAddrValue("Process");
					SetProcessId(hProc, pid);

					status = evt.GetUInt32("Status");
					AssertInfo(status == S_OK);
					if (!SUCCEEDED(status))
						break;

					addrEndpoint = evt.GetAddrValue("Endpoint");

					AssertImportant(FindConnection(addrEndpoint, hProc) == null);
					cxn = new Connection(addrEndpoint, pid, evt.ThreadId, evt.Timestamp)
					{
						stack = evt.Stack,
						hProcess = hProc,
						ipProtocol = (IPPROTO)evt.GetUInt32("Protocol"),
						socktype = (SOCKTYPE)evt.GetUInt32("SocketType")
					};

					// Find counterexamples (not critical)
					AssertImportant((cxn.ipProtocol == IPPROTO.UDP) == (cxn.socktype == SOCKTYPE.SOCK_DGRAM || cxn.socktype == SOCKTYPE.SOCK_RAW));

					var family = (AddressFamily)evt.GetUInt32("AddressFamily");
					if (family == AF_HYPERV) cxn.ipProtocol = IPPROTO.HyperV;
					else if (family == AF_VSOCK) cxn.ipProtocol = IPPROTO.VSock;

					cxn.xlink.GetLink(evt.ThreadId, cxn.timeCreate, in allTables.threadTable);
					this.Add(cxn);
				}
				break;

			case AFD.Close:
				if (evt.GetUInt32("EnterExit") == 1)
					CloseConnection(in evt);

				break;
/*
	PATTERN: Winsock & TCP Stream with Enter/Exit
	Thread1 AFD.Create 0/1
	Thread2 AFD.BindWithAddress 0/1
	Thread2 AFD.Connect[Ex]WithAddress 0
	ThreadX AFD.ConnectEx 1 (opt)
	ThreadX AFD.Send/.Receive 0/1

	PATTERN: Winsock & UDP Datagram with Enter/Exit
	Thread1 AFD.Create 0/1
	Thread1 AFD.BindWithAddress 0/1
	ThreadX AFD.SendMessageWithAddress 0/[1]
	ThreadX AFD.ReceiveMessage/FromWithAddress 1
*/
			case AFD.BindWithAddress:
				// Note: This event has two opcodes:
				//	OPEN (10) - EnterExit == 0
				//	CONNECTED (12) - EnterExit == 1

				AssertImportant(evt.Opcode == (evt.GetUInt32("EnterExit") == 0 ? 10 : 12));

				hProc = evt.GetAddrValue("Process");
				addrEndpoint = evt.GetAddrValue("Endpoint");
				cxn = FindConnection(addrEndpoint, hProc);

				if (cxn == null)
					cxn = RestoreConnection(addrEndpoint, in evt);

				// Overwritten by AFD.Connect*WithAddress, if available.
				cxn.tidConnect = evt.ThreadId;

				if (cxn.socket == 0)
				{
					// This is used for correlating with TCP or UDP.
					if (evt.GetUInt32("AddressLen") != 0)
					{
						SocketAddress sa = evt.GetSocketAddress();

						// This is a curious value for correlating with TCP/UDP.
						// If it is 0 then the protocol is probably not IPv4/IPv6.
						if (sa.Port() != 0)
							cxn.socket = sa.Port();
#if DEBUG
						if (!sa.IsAddrZero())
							cxn.addrLocal = sa;
#endif // DEBUG
					}

					// If this is Exit and socket==0 then this must be a non-standard protocol.
					AssertImportant(FImplies(evt.GetUInt32("EnterExit")==1 && cxn.socket==0, cxn.ipProtocol!=IPPROTO.TCP && cxn.ipProtocol!=IPPROTO.UDP));
				}
#if DEBUG
				if (evt.GetUInt32("AddressLen") != 0)
				{
					SocketAddress sa = evt.GetSocketAddress();
					ushort socket = sa.Port();
					AssertCritical(FImplies(socket != 0, socket == cxn.socket));
					AssertImportant(cxn.addrLocal?.SafeEquals(sa) ?? sa.IsAddrZero());
				}
#endif // DEBUG
				// Last non-zero status wins.
				status = evt.GetUInt32("Status");
				AssertImportant(status == S_OK); // else ignore?
				if (status != S_OK)
					cxn.status = status;

				break;

			case AFD.ConnectWithAddress:
			case AFD.ConnectExWithAddress:
				if (evt.GetUInt32("EnterExit") == 0)
				{
					TrySetProcessId(in evt);

					hProc = evt.GetAddrValue("Process");
					addrEndpoint = evt.GetAddrValue("Endpoint");
					cxn = FindConnection(addrEndpoint, hProc);

					if (cxn == null)
						cxn = RestoreConnection(addrEndpoint, in evt);

					cxn.timeConnect = evt.Timestamp.ToGraphable();

					cxn.addrRemote = NewEndPoint(in evt);
					cxn.iDNS = allTables.dnsTable.IDNSFromAddress(cxn.addrRemote.Address);

					cxn.tidConnect = evt.ThreadId;
					// Note: cxn.tidConnect ?= cxn.tidOpen

					if ((AFD)evt.Id == AFD.ConnectExWithAddress)
						cxn.fSuperConnect = true;

					// Last non-zero status wins.
					status = evt.GetUInt32("Status");
					if (status != S_OK)
						cxn.status = status;
				}
				break;

			case AFD.AcceptExWithAddress:
				// evt.ProcessId is not dependable
				if (evt.GetUInt32("EnterExit") == 1)
				{
					hProc = evt.GetAddrValue("Process");

					// This endpoint will transfer the Socket and perhaps the Local Address.
					addrEndpoint = evt.GetAddrValue("Endpoint");
					cxn = FindConnection(addrEndpoint, hProc);
					ushort socket = cxn?.socket ?? 0;
#if DEBUG
					pid = GetProcessId(hProc, evt);
					AssertImportant(cxn?.pid == pid);

					SocketAddress addrLocal = cxn?.addrLocal;
#endif // DEBUG
					addrEndpoint = evt.GetAddrValue("AcceptEndpoint");

					cxn = FindConnection(addrEndpoint, hProc);

					if (cxn == null)
						cxn = RestoreConnection(addrEndpoint, in evt);

					AssertImportant(cxn.pid == pid);
					AssertCritical(cxn.addrRemote.Empty());
					AssertImportant(cxn.socket == 0);

					cxn.socket = socket;
					cxn.addrRemote = NewEndPoint(in evt);
					cxn.iDNS = allTables.dnsTable.IDNSFromAddress(cxn.addrRemote.Address);
#if DEBUG
					AssertImportant(cxn.addrLocal.Empty());
					cxn.addrLocal = addrLocal;
#endif // DEBUG
					// This thread matches with TCP.AcceptListenerComplete via CorrelateListener.
					cxn.tidConnect = evt.ThreadId;

					// Last non-zero status wins.
					status = evt.GetUInt32("Status");
					if (status != S_OK)
						cxn.status = status;
				}
				break;

			case AFD.Send:
				TrySetProcessId(in evt);
				if (evt.GetUInt32("EnterExit") == 1)
				{
					hProc = evt.GetAddrValue("Process");
					addrEndpoint = evt.GetAddrValue("Endpoint");
					cxn = FindConnection2(addrEndpoint, hProc);

					if (cxn == null)
						cxn = RestoreConnection(addrEndpoint, hProc, in evt);

					AssertCritical(cxn.ipProtocol != IPPROTO.UDP);

					uint cbSend = evt.GetUInt32("BufferLength");
					uint location = evt.GetUInt32("Location");

					if (FKnownDoubleCountEvent(location))
					{
#if DEBUG
						cxn.cbSendNested += cbSend;
#endif // DEBUG
						break;
					}

					cxn.cbSend += cbSend;

					// Last non-zero status wins.
					status = evt.GetUInt32("Status");
					if (status != S_OK)
						cxn.status = status;
				}
				break;

			case AFD.Receive:
				TrySetProcessId(in evt);
				if (evt.GetUInt32("EnterExit") == 1)
				{
					hProc = evt.GetAddrValue("Process");
					addrEndpoint = evt.GetAddrValue("Endpoint");
					cxn = FindConnection2(addrEndpoint, hProc);

					if (cxn == null)
						cxn = RestoreConnection(addrEndpoint, hProc, in evt);

					AssertCritical(cxn.ipProtocol != IPPROTO.UDP);

					cxn.cbRecv += evt.GetUInt32("BufferLength");

					// Last non-zero status wins.
					status = evt.GetUInt32("Status");
					if (status != S_OK)
						cxn.status = status;
				}
				break;
/*
	PATTERN: Winsock & UDP Datagram
	Thread0 AFD.Create Enter AFD-Endpoint SocketType Protocol
	Thread0 AFD.Create Exit  AFD-Endpoint

	Thread0 AFD.BindWithAddress Enter AFD-Endpoint
	Thread0 AFD.BindWithAddress Exit  AFD-Endpoint Address=0:Socket
	...
	Thread1 AFD.SendMessageWithAddress Enter AFD-Endpoint   Address=Addr:Port BufferLength
	Thread1 UDP.EndpointSendMessages LocalSockAddr=0:Socket RemoteSockAddress=Addr:Port Pid UDP-Endpoint
	Thread? AFD.SendMessageWithAddress Exit  AFD-Endpoint   Address=Addr:Port BufferLength // ***OPTIONAL***
	...
	Thread3 UDP.EndpointReceiveMessages LocalSockAddr=XXXX:Socket   RemoteSockAddr=Addr:Port Pid UDP-Endpoint
	Thread3 AFD.Receive*WithAddress Exit AFD-Endpoint Location=4052 Address=Addr:Port
	...
	Thread4 UDP.EndpointReceiveMessages LocalSockAddr=XXXX:Socket   RemoteSockAddr=Addr:Port Pid UDP-Endpoint
	Thread? AFD.Receive*WithAddress Exit AFD-Endpoint Location=4201 Address=Addr:Port
	...
	Thread5 UDP.CloseEndpointBound LocalAddress=0:Socket UDP-Endpoint

	^ The SendMessage pairs and ReceiveMessage pairs have corresponding Endpoint values and Addr:Port.
	^ But one Endpoint can have multiple Addr.

	v This pattern matches separate UDP-Endpoints for send/receive.

	Thread0 AFD.BindWithAddress   ProcessId AFD-Endpoint  Address:Port
	Thread1	UDP.EndpointSendMessages    PID UDP-Endpoint1 LocalSockAddr:Port
	Thread1 UDP.EndpointReceiveMessages PID UDP-Endpoint2 RemoteSockAddr:Port
*/
			// UDP Datagram
			case AFD.SendMessageWithAddress:
				if (evt.GetUInt32("EnterExit") == 0)
				{
					// In this case it appears that the evt.ProcessId is dependable.
					TrySetProcessId(in evt);

					ipAddr = NewEndPoint(in evt);
					hProc = evt.GetAddrValue("Process");
					addrEndpoint = evt.GetAddrValue("Endpoint");
					cxn = FindConnectionUDP(addrEndpoint, hProc, in ipAddr);

					if (cxn == null)
						cxn = RestoreConnectionUDP(addrEndpoint, hProc, in ipAddr, evt.ProcessId, evt.ThreadId, evt.Timestamp);

					AssertCritical(!cxn.FClosed);
					AssertCritical(cxn.ipProtocol == IPPROTO.UDP);
					AssertImportant(cxn.socktype == SOCKTYPE.SOCK_DGRAM || cxn.socktype == SOCKTYPE.SOCK_RAW);

					cxn = HandleUDPAddress(cxn, in ipAddr);

					AssertCritical(cxn.qwEndpoint == addrEndpoint);
					AssertImportant(allTables.tcpTable.TcbrFromI(cxn.iTCB)?.addrRemote?.Address.Equals(cxn.addrRemote.Address) ?? true);

					if (cxn.pid == pidUnknown)
						cxn.pid = evt.ProcessId;
					else
						AssertCritical(cxn.pid == evt.ProcessId);

					cxn.tidConnect = evt.ThreadId;

					cb = evt.GetUInt32("BufferLength");
					cxn.cbSend += cb;
					cxn.cbSendConnect = cb;

					// Last non-zero status wins.
					status = evt.GetUInt32("Status");
					if (status != S_OK)
						cxn.status = status;
				}
				break;

			// UDP Datagram
			case AFD.ReceiveFromWithAddress:
			case AFD.ReceiveMessageWithAddress:
				AssertImportant(evt.GetUInt32("EnterExit") == 1);
				hProc = evt.GetAddrValue("Process");

				if (evt.GetUInt32("Location") == 4052)
				{
					// ASYNCHRONOUS but on the same thread as UdpEndpointReceiveMessages
					// evt.ProcessId is not relevant.
					pid = GetProcessId(hProc, in evt);
					tid = evt.ThreadId; // match by thread
				}
				else
				{
					AssertCritical(evt.GetUInt32("Location") == 4201);

					// SYNCHRONOUS but match to an asynchronous UdpEndpointReceiveMessages
					pid = evt.ProcessId; // match the real Process ID
					tid = tidUnknown; // cannot match by thread
					TrySetProcessId(hProc, pid);
				}

				ipAddr = NewEndPoint(in evt);
				addrEndpoint = evt.GetAddrValue("Endpoint");
				cxn = FindConnectionUDP(addrEndpoint, hProc, in ipAddr);

				if (cxn == null)
					cxn = RestoreConnectionUDP(addrEndpoint, hProc, in ipAddr, pid, (pid==evt.ProcessId)?evt.ThreadId:tidUnknown, evt.Timestamp);

				AssertImportant(!cxn.FClosed); // lingering received message!?
				AssertCritical(cxn.ipProtocol == IPPROTO.UDP);
				AssertImportant(cxn.socktype == SOCKTYPE.SOCK_DGRAM || cxn.socktype == SOCKTYPE.SOCK_RAW);

				cxn = HandleUDPAddress(cxn, in ipAddr);

				AssertCritical(cxn.qwEndpoint == addrEndpoint);

				if (cxn.pid == pidUnknown)
					cxn.pid = pid;
				else
					AssertCritical(cxn.pid == pid);

				cb = evt.GetUInt32("BufferLength");
				cxn.cbRecv += cb;

				// Last non-zero status wins.
				status = evt.GetUInt32("Status");
				if (status != S_OK)
					cxn.status = status;

				if (cxn.socktype == SOCKTYPE.SOCK_RAW)
					break;

				// CorrelateUDPRecvEvent has side effects to clear caches for found records.
				uint iTCB = allTables.tcpTable.CorrelateUDPRecvEvent(pid, tid, cb, cxn.socket, ipAddr);

				if (cxn.iTCB == 0)
				{
					cxn.iTCB = iTCB;

					if (iTCB != 0 && (cxn.pid == pidUnknown || cxn.socket == 0))
					{
						var tcb = allTables.tcpTable.TcbrFromI(iTCB);

						AssertImportant(tcb.CheckType(Protocol.Winsock));

						if (cxn.pid == pidUnknown)
						{
							AssertCritical(tcb.pid != pidUnknown);
							cxn.pid = tcb.pid;
							TrySetProcessId(hProc, cxn.pid);
						}
						else
						{
							AssertCritical(cxn.pid == tcb.pid);
						}

						if (cxn.socket == 0)
							cxn.socket = tcb.socket;
						else
							AssertCritical(cxn.socket == tcb.socket);
					}
					else
					{
						// This could still be a RAW socket (and we missed the Create event). So no associated TcpIp event.
						AssertImportant(iTCB != 0 || tid == tidUnknown);
					}
				}
				else
				{
					AssertImportant(cxn.iTCB == iTCB);
				}

				AssertImportant(allTables.tcpTable.TcbrFromI(cxn.iTCB)?.addrRemote?.Address.Equals(cxn.addrRemote.Address) ?? true);
				break;
#if DEBUG
			default:
				Unreliable.Add(evt.GetUInt32("Location")); // for TestProcessId
				break;
#endif // DEBUG
			} // switch evt.id

			TestProcessId(in evt);
		}
	}
}
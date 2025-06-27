// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic; // List<>
using System.Net;

using Microsoft.Windows.EventTracing.Events;
using Microsoft.Windows.EventTracing.Symbols;

using NetBlameCustomDataSource.Link;

using static NetBlameCustomDataSource.Util;

using TimestampETW = Microsoft.Windows.EventTracing.TraceTimestamp;
using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using AddrVal = System.UInt64;

using IDVal = System.Int32; // Process/ThreadID (ideally UInt32)

using ProcessHash = System.Collections.Generic.Dictionary<System.UInt64, System.Int32>;
using System.Security.Cryptography;


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
		public IDVal pid;
		public IDVal tidOpen;
		public IDVal tidConnect; // Connect*WithAddress / AcceptExWithAddress
		public IDVal tidClose;

		public uint iDNS; // 1-based
		public uint iTCB; // 1-based
#if DEBUG
		public uint cbSendNested; // redundant
#endif
		public uint cbSend;
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

		public Connection()
		{
			this.qwEndpoint = AddrVal.MaxValue;
		}


		public bool MatchAddr(uint iTCB, uint socket, IDVal pid, IDVal tid, IPEndPoint addrRemote)
		{
			if (this.socket != socket) return false;
			if (this.pid != pid) return false;
			if (tid != 0 && this.tidConnect != tid) return false;
			if (this.iTCB != 0)
				return this.iTCB == iTCB;
			else
				return this.addrRemote?.Equals(addrRemote) ?? false;
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

		const IDVal pidUnknown = -1;
		const IDVal tidUnknown = -1;


		void TrySetProcess(in IGenericEvent evt, IDVal pid)
		{
			AssertImportant(pid != 0);
			AssertCritical(pid != pidUnknown);

			AddrVal hProcess = evt.GetAddrValue("Process");
			if (!this.hashProcess.TryGetValue(hProcess, out IDVal pidHash))
				this.hashProcess[hProcess] = pid;
			else
				AssertImportant(pidHash == pid);
		}


		// This list/table can shrink (CloseConnection). Do not hold indices.
		public Connection CxnFromI(uint iCxn) => null;
		public uint IFromCxn(Connection cxn) => 0;


		/*
			Return the most recent, matching, open Connection record.
			HIGH TRAFFIC FUNCTION!
		*/
		Connection FindConnection(AddrVal qwEndpoint)
		{
			Connection cxn = this.FindLast(c => c.qwEndpoint == qwEndpoint);

			if (cxn == null || cxn.FClosed)
				return null;

			return cxn;
		}


		/*
			Try to find the connection, which may be closed.
			If it IS closed then do extra validation.
			Do this to complete an outstanding Send or Receive.
			Can return null!
		*/
		Connection FindConnection2(AddrVal qwEndpoint, in IGenericEvent evt)
		{
			Connection cxn = this.FindLast(c => c.qwEndpoint == qwEndpoint);

			if (cxn == null)
				return null;

			IDVal pid = pidUnknown;

			// If this Connection is closed, then make sure it's not from another process.
			// If this Connection is open, then it should NEVER be from another process.

			if (!cxn.FClosed)
			{
				AssertImportant(!this.hashProcess.TryGetValue(evt.GetAddrValue("Process"), out pid) || cxn.pid == pid);
				return cxn;
			}

			// If we can't be sure that the PIDs match, then assume that they do.
			if (!this.hashProcess.TryGetValue(evt.GetAddrValue("Process"), out pid))
				return cxn;

			return (cxn.pid == pid) ? cxn : null;
		}


		/*
			Try to reconstruct the Connection from the current event.
			Can return null!
		*/
		Connection RestoreConnection(AddrVal qwEndpoint, in IGenericEvent evt)
		{
			AssertImportant(FindConnection2(qwEndpoint, in evt) == null); // Should have called FindConnection2 instead of FindConnection?

			// evt.ProcessId is very likely unreliable, so if we can't look up the true PID then ignore it.
			if (!this.hashProcess.TryGetValue(evt.GetAddrValue("Process"), out IDVal pid))
				return null;

			IDVal tid = (pid == evt.ProcessId) ? evt.ThreadId : tidUnknown;

			// The corresponding TcpAcceptListenerComplete can provide the PID.
			Connection cxn = new Connection(qwEndpoint, pid, tid, evt.Timestamp)
			{
				ipProtocol = IPPROTO.TCP
			};
			this.Add(cxn);

			return cxn;
		}


		/*
			Mark the type of the most recent Winsock Connection with the given PID, TCB, and time overlap.
			Can be 0: tid, iTCB (?)
		*/
		public Connection CorrelateByAddress(IPEndPoint addrRemote, uint iTCB, uint socket, IDVal pid, IDVal tid)
  		{
			AssertImportant(iTCB != 0);

			// There either was no TCB or there's no way to correlate.
			AssertCritical(socket != 0);
			AssertCritical(!addrRemote.Empty());

			Connection cxn = this.FindLast(c => c.MatchAddr(iTCB, socket, pid, tid, addrRemote));

			if (cxn == null)
				return cxn;

			if (iTCB == 0)
				return cxn;

			AssertImportant(cxn.iTCB == 0 || cxn.iTCB == iTCB);

			if (cxn.iTCB == 0)
			{
				this.allTables.tcpTable.TcbrFromI(iTCB).SetType(Protocol.Winsock);
				cxn.iTCB = iTCB;
			}

			return cxn;
		}


		/*
			Mark the type of the most recent Winsock Connection with the given PID, TID, iTCB, etc.
		*/
		public Connection CorrelateListener(SocketAddress sockRemote, TcpIp.TcbRecord tcbR, IDVal pid, IDVal tid, in TimestampUI timeStamp)
  		{
			AssertImportant(tcbR != null);
			AssertImportant(!timeStamp.HasMaxValue());

			// There either was no TCB or there's no way to correlate.
			AssertCritical(!sockRemote.Empty());
			if (sockRemote.Empty())
				return null;

			IPEndPoint addrRemote = NewEndPoint(sockRemote);

			Connection cxn = this.FindLast(cxn =>
				cxn.tidConnect == tid &&
				!cxn.addrRemote.Empty() &&
				cxn.addrRemote.Equals(addrRemote));

			if (cxn == null)
				return null;

			AssertImportant(timeStamp.Between(cxn.timeCreate, cxn.timeClose));

			AssertImportant(FImplies(cxn.pid != pidUnknown, cxn.pid == pid));

			if (cxn.pid == pidUnknown)
				cxn.pid = pid;

			uint iTCB = allTables.tcpTable.IFromTcbr(tcbR);

			AssertImportant(FImplies(cxn.iTCB != 0, cxn.iTCB == iTCB));

			if (cxn.iTCB == 0)
			{
				tcbR.SetType(Protocol.Winsock);
				cxn.iTCB = iTCB;
			}

			AssertImportant(FImplies(cxn.socket != 0, cxn.socket == tcbR.socket));

			if (cxn.socket == 0)
				cxn.socket = tcbR.socket;

			return cxn;
		}


		void CloseConnection(AddrVal qwEndpoint, IDVal tid, in TimestampUI timeStamp)
		{
			Connection cxnPrev = null;
			Connection cxnNext = null;
			for (Connection cxn = FindConnection(qwEndpoint); cxn != null; cxn = cxnNext)
			{
				cxnNext = cxn.cxnNext;
				cxn.timeClose = timeStamp;

				if (cxn.addrRemote.Empty() && cxn.socket == 0 && (cxn.cbSend | cxn.cbRecv) == 0)
				{
					// This was one of many useless, unclaimed Open/Close connections
					// which didn't get the event: Connect[Ex]WithAddress
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
					cxn.tidClose = tid;
					cxnPrev = cxn;
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
				AssertImportant(false); // confirm primary counting for these cases
				goto default;

		// CONFIRMED:
			case 3014: // AfdSend
			case 3018: // AfdSend
			case 3022: // AfdSend
			case 3023: // AfdRestartSend
			case 3025: // AfdCompleteBufferedSendsUnlock
			case 3051: // AfdFastConnectionSend
			case 3201: // AfdFastDatagramSend
			case 3403: // AfdTLFastDgramSendComplete
#endif // DEBUG
			default:
				return false;
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
			Connection cxn;
			AddrVal addrEndpoint;

			switch ((AFD)evt.Id)
			{
			case AFD.Create:
				if (evt.GetUInt32("EnterExit") == 0)
				{
					IDVal pid = (IDVal)evt.GetAddrValue("ProcessId");
					AssertCritical(pid == evt.ProcessId); // Else can we trust evt.ThreadId!?

					this.hashProcess[evt.GetAddrValue("Process")] = pid; // destructive

					status = evt.GetUInt32("Status");
					AssertImportant(status == S_OK);
					if (!SUCCEEDED(status))
						break;

					addrEndpoint = evt.GetAddrValue("Endpoint");

					AssertImportant(FindConnection(addrEndpoint) == null);
					cxn = new Connection(addrEndpoint, pid, evt.ThreadId, evt.Timestamp)
					{
						stack = evt.Stack,
						ipProtocol = (IPPROTO)evt.GetUInt32("Protocol"),
						socktype = (SOCKTYPE)evt.GetUInt32("SocketType")
					};
					// Find counterexamples (not critical)
					AssertImportant((cxn.ipProtocol==IPPROTO.TCP || cxn.ipProtocol==IPPROTO.ICMP) == (cxn.socktype==SOCKTYPE.SOCK_STREAM));
					AssertImportant((cxn.ipProtocol==IPPROTO.UDP) == (cxn.socktype==SOCKTYPE.SOCK_DGRAM));

					var family = (System.Net.Sockets.AddressFamily)evt.GetUInt32("AddressFamily");
					if (family == AF_HYPERV) cxn.ipProtocol = IPPROTO.HyperV;
					else if (family == AF_VSOCK) cxn.ipProtocol = IPPROTO.VSock;

					cxn.xlink.GetLink(evt.ThreadId, cxn.timeCreate, in allTables.threadTable);
					this.Add(cxn);
				}
				break;

			case AFD.Close:
				if (evt.GetUInt32("EnterExit") == 1)
					{
					AssertImportant(evt.GetUInt32("Status") == S_OK);
					TimestampUI timeStamp = evt.Timestamp.ToGraphable();
					addrEndpoint = evt.GetAddrValue("Endpoint");
					CloseConnection(addrEndpoint, evt.ThreadId, in timeStamp);
					}
				break;

			case AFD.ConnectWithAddress:
			case AFD.ConnectExWithAddress:
				if (evt.GetUInt32("EnterExit") == 0)
				{
					addrEndpoint = evt.GetAddrValue("Endpoint");
					cxn = FindConnection(addrEndpoint);

					if (cxn == null)
						cxn = RestoreConnection(addrEndpoint, in evt);

					if (cxn == null) break;

					cxn.timeConnect = evt.Timestamp.ToGraphable();

					if (evt.GetUInt32("AddressLen") != 0)
					{
						cxn.addrRemote = NewEndPoint(evt.GetSocketAddress());
						cxn.iDNS = allTables.dnsTable.IDNSFromAddress(cxn.addrRemote.Address);
					}

					cxn.tidConnect = evt.ThreadId;
					AssertImportant(cxn.tidConnect == cxn.tidOpen); // for CorrelateByTimeThread

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
					addrEndpoint = evt.GetAddrValue("AcceptEndpoint");
					cxn = FindConnection(addrEndpoint);

					if (cxn == null)
						cxn = RestoreConnection(addrEndpoint, in evt);

					if (cxn == null) break;

					AssertCritical(cxn.addrRemote.Empty());

					if (evt.GetUInt32("AddressLen") != 0)
					{
						cxn.addrRemote = NewEndPoint(evt.GetSocketAddress());
						cxn.iDNS = allTables.dnsTable.IDNSFromAddress(cxn.addrRemote.Address);
					}

					cxn.tidConnect = evt.ThreadId;

					// Last non-zero status wins.
					status = evt.GetUInt32("Status");
					if (status != S_OK)
						cxn.status = status;
				}
				break;

			case AFD.BindWithAddress:
				// Note: This event has two opcodes:
				//	OPEN (10) - EnterExit == 0
				//	CONNECTED (12) - EnterExit == 1

				AssertImportant(evt.Opcode == (evt.GetUInt32("EnterExit") == 0 ? 10 : 12));

				// evt.ProcessId is not dependable
				if (evt.GetUInt32("EnterExit") == 1)
				{
					addrEndpoint = evt.GetAddrValue("Endpoint");
					cxn = FindConnection(addrEndpoint);

					if (cxn == null)
						cxn = RestoreConnection(addrEndpoint, in evt);

					if (cxn == null) break;

					// This is used for correlating with TcpIp.
					if (evt.GetUInt32("AddressLen") != 0)
					{
						// This is a curious value for correlating with TCP.
						cxn.socket = evt.GetSocketAddress().Port();
					}

					// Last non-zero status wins.
					status = evt.GetUInt32("Status");
					if (status != S_OK)
						cxn.status = status;
				}
				break;

			case AFD.Send:
				// evt.ProcessId is not dependable
				if (evt.GetUInt32("EnterExit") == 1) // WSAPIEXIT
				{
					addrEndpoint = evt.GetAddrValue("Endpoint");
					cxn = FindConnection2(addrEndpoint, in evt);

					if (cxn == null)
						cxn = RestoreConnection(addrEndpoint, in evt);

					if (cxn == null) break;

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
				// evt.ProcessId is not dependable
				if (evt.GetUInt32("EnterExit") == 1)
				{
					addrEndpoint = evt.GetAddrValue("Endpoint");
					cxn = FindConnection2(addrEndpoint, in evt);

					if (cxn == null)
						cxn = RestoreConnection(addrEndpoint, in evt);

					// Can't resolve the connection or even the process, and there's no address to correlate.
					if (cxn == null)
						break;

					cxn.cbRecv += evt.GetUInt32("BufferLength");

					// Last non-zero status wins.
					status = evt.GetUInt32("Status");
					if (status != S_OK)
						cxn.status = status;
				}
				break;

			// UDP Datagram
			case AFD.SendMessageWithAddress:
				if (evt.GetUInt32("EnterExit") != 0)
					goto case AFD.ReceiveMessageWithAddress;

				// In this case it appears that the evt.ProcessId is dependable.
				AssertImportant(evt.GetUInt32("Location") == 3100); // only know of this case
				TrySetProcess(in evt, evt.ProcessId);
				break;

			case AFD.ReceiveFromWithAddress:
			case AFD.ReceiveMessageWithAddress:
				// evt.ProcessId is not dependable
				if (evt.GetUInt32("EnterExit") == 1)
				{
					bool fSend = (AFD)evt.Id == AFD.SendMessageWithAddress;

					addrEndpoint = evt.GetAddrValue("Endpoint");
					cxn = FindConnection2(addrEndpoint, in evt);

					uint cb = evt.GetUInt32("BufferLength");

					IPEndPoint ipAddr = null;
					if (evt.GetUInt32("AddressLen") != 0)
						ipAddr = NewEndPoint(evt.GetSocketAddress());

					if (cxn == null)
					{
						cxn = RestoreConnection(addrEndpoint, in evt);
						if (cxn == null)
						{
							// Couldn't resolve the ProcessID.
							// It's in the corresponding UDP event.
							// Try again.

							if (ipAddr == null)
								break;

							IDVal pid = allTables.tcpTable.PidFromUDPEvent(ipAddr, cb, fSend);
							if (pid == pidUnknown)
								break;

							TrySetProcess(in evt, pid);
							cxn = RestoreConnection(addrEndpoint, in evt);
							if (cxn == null)
								break;
						}

						cxn.ipProtocol = IPPROTO.UDP;
					}

					AssertImportant(cxn.ipProtocol == IPPROTO.UDP);

					if (ipAddr != null)
					{
						if (cxn.addrRemote.Empty())
						{
							cxn.addrRemote = ipAddr;
							cxn.iDNS = allTables.dnsTable.IDNSFromAddress(ipAddr.Address);
						}
						else
						{
							Connection cxnFound;
							for (cxnFound = cxn; cxnFound != null; cxnFound = cxnFound.cxnNext)
							{
								AssertCritical(cxn.qwEndpoint == addrEndpoint);
								if (cxnFound.ipProtocol == IPPROTO.UDP && cxnFound.addrRemote.Equals(ipAddr))
									break;
							}
							if (cxnFound == null)
							{
								cxnFound = cxn.Clone(ipAddr);
								cxnFound.iDNS = allTables.dnsTable.IDNSFromAddress(ipAddr.Address);

								AssertCritical(cxnFound.cxnNext == null);
								cxnFound.cbSend = cxnFound.cbRecv = 0;
#if DEBUG
								cxnFound.cbSendNested = 0;
#endif
								cxnFound.cxnNext = cxn;
								cxnFound.xlink.AddRefLink();
								Add(cxnFound);
							}
							cxn = cxnFound;
						}

						if (cxn.iTCB == 0)
							cxn.iTCB = allTables.tcpTable.CorrelateUDPAddress(ipAddr, cb, cxn.pid, fSend);
					}

					if (fSend)
						cxn.cbSend += cb;
					else
						cxn.cbRecv += cb;

					// Last non-zero status wins.
					status = evt.GetUInt32("Status");
					if (status != S_OK)
						cxn.status = status;
				}
				break;
			}
		}
	}
}
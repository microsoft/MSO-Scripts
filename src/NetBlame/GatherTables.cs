// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net; // IPAddress
using Microsoft.Windows.EventTracing.Symbols; // IStackSnapshot

using NetBlameCustomDataSource.Link;
using NetBlameCustomDataSource.Stack;
using NetBlameCustomDataSource.TcpIp;

using static NetBlameCustomDataSource.Util;

using IDVal = System.Int32; // Process/Thread ID (ideally: System.UInt32)
using QWord = System.UInt64;

/*
	TimeStamp Strategy

	The WPA UI needs TimestampUI:
		TableBuilder.AddColumn(ColumnConfiguration, timeStampUI);
	Certain methods need TimestampETW:
		processDataSource.GetProcess(TimestampETW, PID, Proxmity)
		threadDataSource.GetThread(TimestampETW, TID, Proximity)
		stackDataSource.GetStack(TimestampETW, TID)

	It is rather expensive to convert from TimestampETW to TimestampUI.
	It is apparently not possible to convert from TimestampUI to TimestampETW.

	So the XLink has the TimestampETW for use with the process, the stack, and the thread associated with the stack.
*/
using TimestampETW = Microsoft.Windows.EventTracing.TraceTimestamp; // struct
using TimestampUI = Microsoft.Performance.SDK.Timestamp; // struct


namespace NetBlameCustomDataSource.Tables
{
	/*
		This entry is for the auxiliary DNS table.
	*/
	public struct DNSIndex
	{
		public ushort iDNS; // 1-based index into the DNS table
		public ushort iAddr; // 1-based index into the DNS entry's address table
	}


	/*
		This entry is for the auxiliary ThreadPool table.
	*/
	public class ThreadPoolItem : IGraphableEntry
	{
		public Tasks.TaskItem tpTask;
		public Tasks.ITaskTableBase tpTable; // Table (base interface) which contains tpTask.

		public ThreadPoolItem(in Tasks.TaskItem _tpTask, in Tasks.ITaskTableBase _tpTable)
		{
			this.tpTask = _tpTask;
			this.tpTable = _tpTable;
		}


		public XLinkType Type => tpTable.PoolType;
		public uint IFromTask => tpTable.IFromTask(this.tpTask);

		public Tasks.ITaskItemInfo TaskItemInfo => (Tasks.ITaskItemInfo)this.tpTask;


		// Implement IGraphableEntry
		public IDVal Pid => this.tpTask.pid;
		public IDVal TidOpen => this.tpTask.tidCreate;
#if AUX_TABLES
		public TimestampETW TimeRef => this.tpTask.timeRef;
#else // !AUX_TABLES
		public TimestampETW TimeRef => default;
#endif // !AUX_TABLES
		public TimestampUI TimeOpen => this.tpTask.timeCreate;
		public TimestampUI TimeClose => this.tpTask.timeDestroy;
		public IStackSnapshot Stack => this.tpTask.stack;
		public XLinkType LinkType => this.tpTask.xlink.typeNext;
		public uint LinkIndex => this.tpTask.xlink.IFromNextLink;
	}

	public class TPCompare : IComparer<ThreadPoolItem>
	{
		// Implement IComparer to sort by time.

		public int Compare(ThreadPoolItem tp1, ThreadPoolItem tp2)
		{
			long val1 = tp1.TimeOpen.ToNanoseconds;
			long val2 = tp2.TimeOpen.ToNanoseconds;
			if (val1 > val2) return 1;
			if (val1 == val2) return 0;
			return -1;
		}
	} // TPCompare


	public class StreamEntry
	{
		public string type;
		public string url;
		public string domain;
		public string anon_key;
		public string method;
		public string origin;
		public string referer;
		public string error; // Socket.Status()
		public string status; // HTTP Status
		public ushort socket; // Socket.WSSocket()
		public ushort port;
		public uint cbSend, cbRecv;
		public IDVal pid, tid;
		public int stream_id;
		public int source_id;
		public QWord request_id;
		public QWord id;
		public IPEndPoint addrRemote;
		public TimestampUI timeFirst;
		public TimestampUI timeLast;
	}


	/*
		This is the master table entry, accumulating data from all other tables.
	*/
	public class URL : IGraphableEntry
	{
		// None of these string values should be null.
		public readonly string strURL;
		public readonly string strMethod; // GET, POST, RDIR
		public string strServer; // usually but not always a subtring of strURL; may be N/A
		public string strServerAlt; // may be N/A, but never equal to strServer
		public string strStatus; // "HTTP/1.1 400 Bad Request"

		public IPEndPoint ipAddrPort;

		public Protocol netType;

		public readonly IDVal pid;
		public IDVal tid; // ThreadID of the callstack, else of the URL request/operation.

		public uint cbSend;
		public uint cbRecv;

		public TimestampETW timeRef;

		public TimestampUI timeOpen;
		public TimestampUI timeClose;

		public QWord qwRequest;    // for WebIO and WinINet Request and WinSock Endpoint
		public QWord qwConnection; // for WebIO and WinINet Connection
		public UInt32 dwSocket;    // for WebIO and WinINet Socket
		public QWord tcbId;        // for TcpIp's Transfer Control Block

		public XLink xlink;

		public MyStackSnapshot myStack;


		public URL(string strURL, string strMethod, IDVal pid, IDVal tid, in TcbRecord tcbR, Protocol netType)
		{
			this.pid = pid;
			this.tid = tid;
			this.strMethod = strMethod ?? String.Empty;
			this.strURL = strURL ?? String.Empty;
			this.strStatus = string.Empty;
			this.netType = netType;
			AssertCritical((netType & (netType - 1)) == 0); // One protocol only!

			AssertCritical(this.ipAddrPort.Empty());

			if (tcbR != null)
			{
				this.ipAddrPort = tcbR.addrRemote;
				this.tcbId = tcbR.tcb;
				AssertImportant((tcbR.grbitProtocol & (byte)netType) == (byte)netType);
			}
		}

		// Implement IGraphableEntry
		public IDVal Pid => this.pid;
		public IDVal TidOpen => this.tid;
		public TimestampETW TimeRef => this.timeRef;
		public TimestampUI TimeOpen => this.timeOpen;
		public TimestampUI TimeClose => this.timeClose;
		// This implementation of IStackSnapshot returns myStack.stackLast, which invoked the network request.
		public IStackSnapshot Stack => this.myStack;
		public XLinkType LinkType => this.xlink.typeNext;
		public uint LinkIndex => this.xlink.IFromNextLink;
	} // URL

	public class URLTable : List<URL>
	{
		readonly AllTables allTables;

		public URLTable(in AllTables _allTables)
				: base(_allTables.webioTable.requestTable.Count + _allTables.winetTable.Count) // TODO: optimize capacity
		{
			this.allTables = _allTables;
		}


		URL AddURL(string strURL, string strMethod, IDVal pid, IDVal tid, uint cbSend, uint cbRecv, TcbRecord tcb, Protocol netType)
		{
			if (tcb != null)
			{
				tcb.SetType(netType);
				tcb.fGathered = true;
			}

			URL url = new URL(strURL, strMethod, pid, tid, in tcb, netType);
			this.Add(url);

			url.cbSend += cbSend;
			url.cbRecv += cbRecv;

			return url;
		}

		void AddURLConnection(WebIO.Request req, WebIO.Connection cxn)
		{
			TcbRecord tcbR = null;
			if (cxn.socket != null)
			{
				tcbR = this.allTables.tcpTable.TcbrFromI(cxn.socket.iTCB);
				AssertCritical(tcbR == null || tcbR.pid == req.pid);
			}

			URL url = AddURL(req.strURL, req.strMethod, req.pid, req.tidStack, cxn.cbSend, cxn.cbRecv, tcbR, Protocol.WinHTTP);
			url.strServer = this.allTables.dnsTable.GetServerNameAndAlt(tcbR?.addrRemote?.Address, req.strURL, req.strServer, out url.strServerAlt);
			url.strStatus = cxn.strHeader ?? String.Empty;
			url.qwConnection = cxn.qwConnection;
			url.dwSocket = (UInt32)(cxn.socket?.qwSocket ?? 0);
			url.qwRequest = req.qwRequest;
			url.timeOpen = req.timeOpen;
			url.timeClose = req.timeClose;
			url.timeRef = req.timeRef;
			url.xlink = req.xlink; // no AddRef
			url.myStack = new MyStackSnapshot(in req.stack, in req.xlink, req.tidStack);
			if (tcbR == null)
			{
				IPAddress addr = this.allTables.dnsTable.AddressFromI(cxn.socket?.iDNS ?? 0, cxn.socket?.iAddr ?? 0);
				if (addr != null)
					url.ipAddrPort = new IPEndPoint(addr, (int)cxn.socket.port);
			}
		}

		static readonly WebIO.Connection s_cxnWebIO_Null = new WebIO.Connection(0, null);

		void AddURL(WebIO.Request req)
		{
			if (req.rgConnection != null)
			{
				foreach (WebIO.Connection cxn in req.rgConnection)
					AddURLConnection(req, cxn);
			}
			else
			{
				AddURLConnection(req, s_cxnWebIO_Null);
			}
#if DEBUG
			req.fGathered = true;
#endif // DEBUG
		}

		void AddURL(in WinINet.Request req, in TcbRecord tcb)
		{
			URL url = AddURL(req.strURL, req.strMethod, req.pid, req.tid1, req.cbSend, req.cbRecv, tcb, Protocol.WinINet);

			url.strServer = this.allTables.dnsTable.GetServerNameAndAlt(req.addrRemote?.Address, req.strURL, req.strServerName, out url.strServerAlt);
			url.strStatus = req.Status;

			AssertCritical(!url.xlink.HasValue);
			url.xlink = req.xlink; // No AddRef.
			url.myStack = new MyStackSnapshot(in req.stack, in req.xlink, req.stack?.ThreadId ?? req.xlink.taskLinkNext?.tidExec ?? req.tid1); // Best effort for ThreadID.

			AssertCritical(!url.timeOpen.HasValue());
			AssertCritical(!url.timeClose.HasValue());
			AssertCritical(FImplies(req.timeClose1.HasValue(), req.timeClose2.HasValue()));
			AssertCritical(FImplies(req.timeClose1.HasMaxValue(), req.timeClose2.HasMaxValue()));

			// TODO: timeClose1 or timeClose2?
			url.timeOpen = req.timeOpen;
			url.timeClose = req.timeClose1;
			url.timeRef = req.timeRef;

			url.qwRequest = req.qwRequest;
			url.qwConnection = req.qwConnect;
			url.dwSocket = req.socket; // ushort
#if DEBUG
			req.fGathered = true;
#endif // DEBUG
		}


		void AddURL(Chromium.Request req)
		{
			AssertCritical(!req.IsSpeculative);

			Chromium.Socket socket = req.Session?.socket;
			TcbRecord tcbr = this.allTables.tcpTable.TcbrFromI(socket?.cxn?.iTCB ?? 0);

			URL url = AddURL(req.URL, req.method, req.pid, req.tid, req.cbUpload, req.cbDownload, tcbr, Protocol.Chromium);

			url.strServer = req.Domain;
			url.strServerAlt = req.Canon;
			url.timeOpen = req.timeStampBeginJob;
			url.timeClose = req.timeStampEndJob;
			url.timeRef = req.timeRef;
			url.qwRequest = req.uidRequest;
			url.strStatus = req.Error();

			ushort port = req.port;

			if (socket != null)
			{
				port = (ushort)socket.addrRemote.PortGraphable();
				url.qwConnection = socket.cxn?.qwEndpoint ?? 0;
				url.dwSocket = socket.WSSocket(); // ushort

				socket.fGathered = true;
			}

			IPAddress ipAddr = req.IPAddress(socket);
			if (!ipAddr.Empty())
				url.ipAddrPort = new IPEndPoint(ipAddr, port);

			url.myStack = new MyStackSnapshot(in req.stack, in req.xlink, req.tid); // aggregated stacks
#if DEBUG
			req.fGathered = true;
#endif // DEBUG
		}

		void AddURL(Chromium.Session session, Chromium.Session.Stream stream)
		{
			AssertCritical(!stream.fAbandoned && !stream.fIgnore);
			AssertInfo(stream.request != null); // handled, but... late trace start?

			Chromium.Request req = stream.request;
			Chromium.Socket socket = session.socket;
			WinsockAFD.Connection cxn = socket?.cxn;
			TcbRecord tcbr = this.allTables.tcpTable.TcbrFromI(cxn?.iTCB ?? 0);

			URL url = null;

			if (req != null)
			{
				url = AddURL(req.URL, req.method, req.pid, req.tid, stream.CbSend(), stream.CbRecv(), tcbr, Protocol.Chromium);

				url.strServer = req.Domain;
				url.strServerAlt = req.Canon;
				url.timeOpen = req.timeStampBeginJob;
				url.timeClose = req.timeStampEndJob;
				url.timeRef = req.timeRef;
				url.qwRequest = req.uidRequest;

				url.myStack = new MyStackSnapshot(in req.stack, in req.xlink, req.tid); // aggregated stacks
			}
			else
			{
				url = AddURL(stream.strURL, stream.strMethod, session.pid, session.tid, stream.CbSend(), stream.CbRecv(), tcbr, Protocol.Chromium);

				string strCanon = session.resolver?.rgstrCanon?[0] ?? string.Empty;
				url.strServerAlt = !strCanon.Equals(session.domain) ? strCanon : string.Empty;
				url.strServer = session.domain;
				url.timeRef = session.timeReference;
				url.timeOpen = stream.timeFirst;
				url.timeClose = stream.timeLast;
			}

			url.strStatus = stream.strHTTPStatus;

			IPEndPoint ipEP = socket?.addrRemote;
			AssertImportant(FImplies(ipEP.Empty(), cxn?.addrRemote == null));

			if (ipEP.Empty())
			{
				IPAddress ipAddr = req?.IPAddress(socket);
				ushort port = (req?.port > session.port) ? req.port : session.port;
				if (!ipAddr.Empty())
					ipEP = new IPEndPoint(ipAddr, port);
			}
			url.ipAddrPort = ipEP;

			if (socket != null)
			{
				url.qwConnection = cxn?.qwEndpoint ?? 0;
				url.dwSocket = socket.WSSocket(); // ushort

				socket.fGathered = true;
			}
		}

		// This Socket is attached to no Request.
		void AddURL(Chromium.Socket socket)
		{
			WinsockAFD.Connection cxn = socket.cxn;
			AssertImportant(cxn != null);
			if (cxn == null)
				return;

			TcbRecord tcbr = this.allTables.tcpTable.TcbrFromI(cxn.iTCB);

			URL url = AddURL(string.Empty, Util.ComposeMethod(cxn), socket.pid, socket.tid, cxn.cbSend, cxn.cbRecv, tcbr, Protocol.Chromium);

			url.timeOpen = socket.timeStampConnect;
			url.timeClose = socket.timeStampClose;
			url.timeRef = cxn.timeRef;
			url.qwConnection = cxn.qwEndpoint;
			url.dwSocket = socket.WSSocket(); // ushort

			AssertImportant(socket.addrRemote.Equals(cxn.addrRemote));
			IPEndPoint ipEP = socket.addrRemote;
			if (ipEP.Empty())
				ipEP = cxn.addrRemote;

			url.ipAddrPort = ipEP;

			if (!ipEP.Empty() && !ipEP.Address.Empty())
				url.strServer = this.allTables.dnsTable.GetServerNameAndAlt(ipEP.Address, null, strNA, out url.strServerAlt);

			url.myStack = new MyStackSnapshot(in cxn.stack, in cxn.xlink, cxn.tidOpen); // aggregated stacks

			socket.fGathered = true;
		}

		void AddURL(WinsockAFD.Connection cxn)
		{
			TcbRecord tcb = this.allTables.tcpTable.TcbrFromI(cxn.iTCB);

			// WinSock is a mid-level protocol. This TCB may have already been 'gathered' by a higher level protocol.
			if (tcb == null)
			{
				if (cxn.Protocol > Protocol.Winsock)
					return;
			}
			else if (tcb.fGathered)
			{
				AssertImportant(cxn.Protocol > Protocol.Winsock);
				return;
			}

			// Any Connection with another protocol should have already been 'gathered'.
			AssertImportant(cxn.Protocol == Protocol.Winsock);

			URL url = AddURL(null, Util.ComposeMethod(cxn), cxn.pid, cxn.tidOpen, cxn.cbSend, cxn.cbRecv, tcb, Protocol.Winsock);

			uint iAddr = this.allTables.dnsTable.IFindAddress(cxn.iDNS, cxn.addrRemote.Address); // 1-based
			url.strServer = this.allTables.dnsTable.GetServerNameAndAlt(cxn.iDNS, iAddr, null, strNA, out url.strServerAlt);
			url.timeOpen = cxn.timeCreate;
			url.timeClose = cxn.timeClose;
			url.timeRef = cxn.timeRef;
			AssertCritical(url.TimeRef.HasValue);
			url.qwConnection = cxn.qwEndpoint;
			url.dwSocket = cxn.socket; // ushort
			url.strStatus = cxn.status.ToHexOrBlank();
			url.xlink = cxn.xlink;
			url.myStack = new MyStackSnapshot(in cxn.stack, in cxn.xlink, cxn.tidOpen);

			if (tcb == null)
			{
				AssertCritical(!cxn.addrRemote.Empty());
				url.ipAddrPort = cxn.addrRemote;
			}
			else
			{
				AssertCritical(url.ipAddrPort.Address.Equals(cxn.addrRemote.Address));
				AssertCritical(url.ipAddrPort.Port == cxn.addrRemote.Port);
			}
		}

		void AddURL(TcbRecord tcb)
		{
			tcb.grbitProtocol |= (byte)(tcb.fUDP ? Protocol.UDP : Protocol.TCP);

			string strMethod = null;
			if ((tcb.grbitProtocol & (byte)Protocol.Rundown) != 0)
			{
				strMethod = Protocol.Rundown.ToString();
			}
			else if (tcb.addrRemote != null)
			{
				strMethod = Util.ServiceFromPort(tcb.addrRemote.Port);
				string strType = Util.AddressType(tcb.addrRemote.Address);
				if (strType != null)
				{
					if (strMethod != null)
						strMethod += " : " + strType;
					else
						strMethod = strType;
				}
			}

			URL url = AddURL(null, strMethod, tcb.Pid, tcb.TidOpen, tcb.cbSend, tcb.cbRecv, tcb, Prominent((Protocol)tcb.grbitProtocol));

			url.strServer = this.allTables.dnsTable.GetServerNameAndAlt(tcb.addrRemote?.Address, null, strNA, out url.strServerAlt);

			// Use overflow arithmetic to determine which event is later but not MaxValue.
			AssertCritical(TimestampUI.MaxValue.ToNanoseconds + 1 < 0);
			if (tcb.timeClose.ToNanoseconds + 1 >= tcb.timeShutdown.ToNanoseconds + 1)
				url.timeClose = tcb.timeClose;
			else
				url.timeClose = tcb.timeShutdown;

			url.dwSocket = tcb.socket; // ushort

			url.timeRef = tcb.timeOpen; // This TimestampETW is required to get the process from the PID.
			url.timeOpen = tcb.timeOpen.ToGraphable();
			url.xlink.Reset();
			url.myStack = new MyStackSnapshot(); // no stacks
		}


		void GatherTcpIp()
		{
			foreach (TcpIp.TcbRecord tcb in this.allTables.tcpTable)
			{
#if AUX_TABLES
				// For the TcpIp table:
				AssertCritical(tcb.iDNS == 0);
				tcb.iDNS = !tcb.addrRemote.Empty() ? this.allTables.dnsTable.IDNSFromAddress(tcb.addrRemote.Address) : 0;
#endif // AUX_TABLES
				if (tcb.fGathered) continue;

				// If it's a Rundown or Unknown TCB that has no process and no activity then it is effectively irrelevant.

				if ((Protocol)tcb.grbitProtocol <= Protocol.Rundown
						&& tcb.pid <= TcbRecord.pidSystem && tcb.cbPost + tcb.cbSend + tcb.cbRecv == 0)
				{
					tcb.fGathered = true;
					continue;
				}

				if (tcb.pid == TcbRecord.pidUnknown)
					tcb.pid = tcb.pidAlt;

				AddURL(tcb);
			}
		}


		void GatherWinINet()
		{
			foreach (WinINet.Request req in this.allTables.winetTable)
			{
				this.allTables.winetTable.StopRequest(req, null);
				AssertImportant(req.pid != 0);
				AssertInfo(req.iTCB != 0); // No TCB is normal in some cases.
				AssertInfo(!String.IsNullOrWhiteSpace(req.strURL));

				TcbRecord tcb = this.allTables.tcpTable.TcbrFromI(req.iTCB);
#if AUX_TABLES
				req.strServerName = this.allTables.dnsTable.GetServerNameAndAlt(req.addrRemote?.Address, req.strURL, req.strServerName, out req.strServerAlt);
#endif // AUX_TABLES

				AddURL(in req, in tcb);
			}
		}


		void GatherWebIO()
		{
			foreach (WebIO.Request req in this.allTables.webioTable.requestTable)
			{
				AddURL(req);
			}
		}


		/*
			Finalize the Chromium records.
			Incorporate into the URL table.
		*/
		void GatherChromium()
		{
			TimestampUI timeStampEnd = this.allTables.traceMetadata.LastEventTime.ToGraphable();

			foreach (var session in this.allTables.chromiumTable.sessionTable)
			{
				if (session.FMigrated)
					session.AdjustForMigration(this.allTables.chromiumTable);

				IPEndPoint _addrRemote = session.RemoteAddress();

				foreach (var kvp in session.rgStream)
				{
					var stream = kvp.Value;
					AssertImportant(stream != null);
					if (stream == null) continue;

					if (!session.ValidStream(stream)) continue;

					if (stream.request != null)
					{
						AssertCritical(stream.request.FAttachedToStream); // else the Request shows up twice
						AssertCritical(FImplies(!session.fPreMigrate, stream.request.Session == session));
						stream.request.Close(session.resolver, this.allTables.dnsTable, in timeStampEnd);
#if DEBUG
						stream.request.fGathered = true;
#endif // DEBUG
					}

					AddURL(session, stream);
				}
#if DEBUG
				session.fGathered = true;
#endif // DEBUG
			} // foreach session

			// Now add incomplete Requests not added above (not linked to a Stream).
			foreach (var req in this.allTables.chromiumTable)
			{
#if DEBUG
				AssertCritical(req.fGathered == (req.stream != null));
#endif // DEBUG
				if (req.stream != null) continue;

				// REVIEW: Should these Speculative Preconnect placeholder Requests be removed from the list for the Chromium Requests table?
				if (req.IsSpeculative) continue;

				// It should be only incomplete or preconnect placeholder Requests (or some error conditions) that have no Stream.
				AssertImportant(req.Type == Chromium.StreamType.Unknown || req.IsPreconnect || req.iError != 0 || req.fCanceled || req.fRedirect);

				req.Close(in timeStampEnd);

				AddURL(req);
			} // foreach req

			// Gather the Sockets not enumerated with the Requests above.

			foreach (Chromium.Socket sock in this.allTables.chromiumTable.socketTable)
			{
				sock.Close(timeStampEnd);

				if (!sock.fGathered)
					AddURL(sock);
			}

			this.allTables.chromiumTable.EmitViewProfileAnnotationQueriesDB();
		} // GatherChromium


		void GatherWinSock()
		{
			var wsTable = this.allTables.wsTable;
			for (int icxn = wsTable.Count-1; icxn >= 0 ; --icxn)
			{
				WinsockAFD.Connection cxn = wsTable[icxn];

				if (cxn.addrRemote.Empty())
				{
					// This element never got a CloseConnection, where we would have removed it.

					cxn.xlink.Unlink(false);

					wsTable.RemoveAt(icxn); // incompatible with foreach()

					continue;
				}

				if (cxn.ipProtocol == WinsockAFD.IPPROTO.TCP || cxn.ipProtocol == WinsockAFD.IPPROTO.UDP)
				{
#if DEBUG
					// Confirm that we cannot still claim a TCB which is not otherwise claimed by WinINet or WinHTTP.
					if (cxn.iTCB == 0 && cxn.ipProtocol != WinsockAFD.IPPROTO.UDP && cxn.socktype != WinsockAFD.SOCKTYPE.SOCK_RAW)
						AssertImportant(0 == this.allTables.tcpTable.CorrelateByAddress(cxn.addrRemote, cxn.pid, cxn.tidClose, cxn.socket, (Protocol)cxn.grbitType));
#endif // DEBUG
					// Maybe we can finally claim a DNS entry which was added later.
					if (cxn.iDNS == 0)
						cxn.iDNS = this.allTables.dnsTable.IDNSFromAddress(cxn.addrRemote.Address);
				}
				else
				{
					AssertImportant(cxn.ipProtocol == WinsockAFD.IPPROTO.HyperV); // else do what?
				}

				AddURL(cxn);
			}
		} // GatherWinSock


		/*
			Index all IPAddress within the tables: DNSEntry[].rgAddrEntry[]
		*/
		[Conditional("AUX_TABLES")]
		public void GatherDNS()
		{
#if AUX_TABLES // I know, I know...
			DNSIndex dnsIndex;

			int count = 0;
			foreach (var dns in this.allTables.dnsTable)
			{
				count += dns.rgIpAddr.Count;

				if (dns.rgIpAddr.Count == 0)
					++count;
			}

			this.allTables.dnsIndexTable = new List<DNSIndex>(count);

			for (int iDNS = 0; iDNS < this.allTables.dnsTable.Count; ++iDNS)
			{
				var dns = this.allTables.dnsTable[iDNS];

				if (dns.rgIpAddr.Count == 0)
				{
					// Empty entry has no resolved IP Address.
					dnsIndex.iDNS = (ushort)(iDNS+1);
					dnsIndex.iAddr = 0;

					this.allTables.dnsIndexTable.Add(dnsIndex);
					continue;
				}

				for (int iAddr = 0; iAddr < dns.rgIpAddr.Count; ++iAddr)
				{
					dnsIndex.iDNS = (ushort)(iDNS+1);
					dnsIndex.iAddr = (ushort)(iAddr+1);

					this.allTables.dnsIndexTable.Add(dnsIndex);
				}
			}
#endif // AUX_TABLES
		}


		/*
			List all threadpool objects which ultimately spawned a network request/connection of interest, sorted by time.
			Office TP, WinHTTP TP, Windows TP, Windows Timers
		*/
		[Conditional("AUX_TABLES")]
		public void GatherThreadPools()
		{
#if AUX_TABLES // I know, I know...
// TODO: Shrink the thread table.

			int count = this.allTables.otpTable.Count
					+ this.allTables.idleTable.Count
					+ this.allTables.httpTable.Count
					+ this.allTables.wtpTable.wtpCallbackTable.Count
					+ this.allTables.wtpTable.wtpTimerTable.Count;

			var tpTable = new List<ThreadPoolItem>(count);

			foreach (var tpOffice in this.allTables.otpTable)
				tpTable.Add(new ThreadPoolItem(tpOffice, this.allTables.otpTable));

			foreach (var tpDispatch in this.allTables.odqTable)
				tpTable.Add(new ThreadPoolItem(tpDispatch, this.allTables.odqTable));

			foreach (var tpIdle in this.allTables.idleTable)
				tpTable.Add(new ThreadPoolItem(tpIdle, this.allTables.idleTable));

			foreach (var tpHTTP in this.allTables.httpTable)
				tpTable.Add(new ThreadPoolItem(tpHTTP, this.allTables.httpTable));

			foreach (var tpWCallback in this.allTables.wtpTable.wtpCallbackTable)
				tpTable.Add(new ThreadPoolItem(tpWCallback, this.allTables.wtpTable.wtpCallbackTable));

			foreach (var tpWTimer in this.allTables.wtpTable.wtpTimerTable)
				tpTable.Add(new ThreadPoolItem(tpWTimer, this.allTables.wtpTable.wtpTimerTable));

			foreach (var thread in this.allTables.threadTable)
				tpTable.Add(new ThreadPoolItem(thread, this.allTables.threadTable));

			tpTable.Sort(new TPCompare()); // Sort by time.

			this.allTables.tpTable = tpTable;
#endif // AUX_TABLES
		}


		/*
			Populate the Chromium Streams table.
		*/
		[Conditional("AUX_TABLES")]
		void GatherChromiumStreams()
		{
			int count = 0;
			foreach (var session in this.allTables.chromiumTable.sessionTable)
				count += session.rgStream.Count;

			var streamTable = new List<StreamEntry>(count);

			foreach (var session in this.allTables.chromiumTable.sessionTable)
			{
				if (session.rgStream.Count == 0) continue;

				IPEndPoint _addrRemote = session.RemoteAddress();

				string _anon_key = (session.anon_key != null) ? session.anon_key : session.resolver?.anon_key;

				string _type = session.Type.ToString();

				ushort _socket = session.socket?.WSSocket() ?? 0;

				foreach (var kvp in session.rgStream)
				{
					var stream = kvp.Value;
					AssertImportant(stream != null);
					if (stream == null) continue;

					if (!session.ValidStream(stream)) continue;

					StreamEntry entry = new StreamEntry()
					{
						stream_id = kvp.Key,
						error = session.Error(stream),
						domain = session.domain,
						port = session.port,
						pid = session.pid,
						tid = session.tid,
						id = session.uidVal,
						source_id = session.srcdep,
						url = stream.strURL,
						method = stream.strMethod,
						origin = stream.strOrigin,
						referer = stream.strReferer,
						cbSend = stream.CbSend(),
						cbRecv = stream.CbRecv(),
						status = stream.strHTTPStatus,
						request_id = stream.request?.uidRequest ?? 0,
						timeFirst = stream.timeFirst,
						timeLast = stream.timeLast,
						addrRemote = _addrRemote,
						socket = _socket,
						anon_key = _anon_key,
						type = _type
					};

					streamTable.Add(entry);
				}
			}

			this.allTables.streamTable = streamTable;
		}


		public void GatherAll()
		{
			GatherDNS();

			GatherThreadPools();

			GatherChromium();
			GatherChromiumStreams();

			GatherWinINet();

			GatherWebIO();

			GatherWinSock();

			GatherTcpIp();
		}
	} // URLTable
} // NetBlameCustomDataSource.Tables
// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Net;

using Microsoft.Windows.EventTracing.Events;
using Microsoft.Windows.EventTracing.Symbols;

using NetBlameCustomDataSource.Link;

using static NetBlameCustomDataSource.Util;

using TimestampETW = Microsoft.Windows.EventTracing.TraceTimestamp;
using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using IDVal = System.Int32; // type of Event.pid/tid / ideally: System.UInt32
using QWord = System.UInt64;


namespace NetBlameCustomDataSource.WinINet
{
	/*
		**** CASE 1 ****
		Gather and correlate data like this:
		 104 WININET_HTTP_REQUEST_HANDLE_CREATED (A): Thread#0, hRequest, Type (GET/POST), Path (no server), STACK // A new request is created!
		 108 WININET_HTTP_REQUEST_HANDLE_CREATED (B): Thread#0, hRequest, ServerName, Port (Full URL = ServerName+Path), (STACK)
		 200 WININET_HTTP_REQUEST.Start:              Thread#1, hRequest, Context
		1007 Wininet_SendRequest.Start:               Thread#1, Req#, Full URL, Time
		1045 Wininet_Connect.Start                    Thread#2, Req#
		---- TcpRequestConnect TCB,                   Thread#2, IPAddr:Port (connect by proximity! + thread)
		1046 Wininet_Connect.Stop                     Thread#2, Req#, IPAddr:Port (spurious timing; confirm IPAddr:Port with connected TCB)
		1031 Wininet_SendRequest_Main                 Thread#2, Req#, Size
		1033 Wininet_SendRequest_Extra                Thread#2, Req#, Size
		1037 Wininet_ReadData                         Thread#3, Req#, Size
		1064 WININET_STREAM_DATA_INDICATED            Thread#3, hRequest, Size
		1008 Wininet_SendRequest.Stop                 Thread#2?,Req#, Status or NULL, Time (there may be two, the second with the status)
		**** No matter the status, additional records may follow: ****
		 200 WININET_HTTP_REQUEST.Start:              Thread#2, hRequest, Context
		1008 Wininet_SendRequest.Stop                 Thread#2?,Req#, Status, Time (closes the request; this second one has the status)
		 105 WININET_HANDLE_CLOSED                    hRequest // This can occur earlier!

		**** CASE 2 (KEEP_ALIVE_CONNECTION_REUSED) **** via urlmon.dll!EdgeTransaction::Continue
		 104 WININET_HTTP_REQUEST_HANDLE_CREATED (A): Thread#0, hRequest, Type (GET/POST), Path (no server), STACK // A new request is created!
		 108 WININET_HTTP_REQUEST_HANDLE_CREATED (B): Thread#0, hRequest, ServerName, Port (Full URL = ServerName+Path), (STACK)
		 200 WININET_HTTP_REQUEST.Start:              Thread#1, hRequest, Context
		1007 Wininet_SendRequest.Start:               Thread#1, Req#, Full URL, Time
		1045 Wininet_Connect.Start                    Thread#2, Req#
		1046 Wininet_Connect.Stop                     Thread#2, Req#, Socket=Address=0 ****
		1008 Wininet_SendRequest.Stop                 Thread#2?,Req#, Status=NULL      ****
		~~~~
		 200 WININET_HTTP_REQUEST.Start:              Thread#2?,hRequest, Context
		1007 Wininet_SendRequest.Start:               Thread#3, Req#, Full URL (same as previous), Time
		1045 Wininet_Connect.Start                    Thread#3, Req#
		1046 Wininet_Connect.Stop                     Thread#3, Req#, IPAddr:Port
		1008 Wininet_SendRequest.Stop                 Thread#3?,Req#, Status, Time
		 105 WININET_HANDLE_CLOSED                    hRequest // This can occur earlier!

		**** CASE 3 ****
		 104 WININET_HTTP_REQUEST_HANDLE_CREATED (A): Thread#0, hRequest, Type (GET/POST), Path (no server), STACK // A new request is created!
		 108 WININET_HTTP_REQUEST_HANDLE_CREATED (B): Thread#0, hRequest, ServerName, Port (Full URL = ServerName+Path), (STACK)
		 200 WININET_HTTP_REQUEST.Start:              Thread#1, hRequest, Context
		1007 Wininet_SendRequest.Start:               Thread#1, Req#, Full URL, Time
		1017 Wininet_PreNet_CacheHit                  Thread#1, Req#
		 105 WININET_HANDLE_CLOSED                    hRequest
		 105 WININET_HANDLE_CLOSED                    hConnect // ignore

		**** CASE 4 (Redirect, Cache Miss) ****
		 104 WININET_HTTP_REQUEST_HANDLE_CREATED (A): Thread#0, hRequest, Type (GET/POST), Path (no server), STACK // A new request is created!
		 108 WININET_HTTP_REQUEST_HANDLE_CREATED (B): Thread#0, hRequest, ServerName, Port (Full URL = ServerName+Path), (STACK)
		 200 WININET_HTTP_REQUEST.Start:              Thread#1, hRequest, Context
		1007 Wininet_SendRequest.Start:               Thread#1, Req#, Full URL, Time
		1045 Wininet_Connect.Start                    Thread#2, Req#
		1046 Wininet_Connect.Stop                     Thread#3, Req#, IPAddr:Port
		1037 Wininet_ReadData.Info                    Thread#4, Req#, Size
		1049 Wininet_Redirect.Info                    Thread#4, Req#, Full URL 2
		1045 Wininet_Connect.Start                    Thread#4, Req#
		1046 Wininet_Connect.Stop                     Thread#4, Req#, IPAddr:Port
		1031 Wininet_SendRequest_Main                 Thread#4, Req#, Size
		1008 Wininet_SendRequest.Stop                 Thread#5, Req#, Status, Time

		**** CASE 5 (Redirect, Cache Hit) ****
		 104 WININET_HTTP_REQUEST_HANDLE_CREATED (A): Thread#0, hRequest, Type (GET/POST), Path (no server), STACK // A new request is created!
		 108 WININET_HTTP_REQUEST_HANDLE_CREATED (B): Thread#0, hRequest, ServerName, Port (Full URL = ServerName+Path), (STACK)
		 200 WININET_HTTP_REQUEST.Start:              Thread#1, hRequest, Context
		1007 Wininet_SendRequest.Start:               Thread#1, Req#, Full URL, Time
		1045 Wininet_Connect.Start                    Thread#2, Req#
		1046 Wininet_Connect.Stop                     Thread#3, Req#, IPAddr:Port
		1031 Wininet_SendRequest_Main                 Thread#3, Req#, Size
		1037 Wininet_ReadData.Info                    Thread#4, Req#, Size
		1049 Wininet_Redirect.Info                    Thread#4, Req#, Full URL 2 (no IPAddr:Port)
		1008 Wininet_SendRequest.Stop                 Thread#4, Req#, Status, Time

		**** CASE 6 (I don't know what's happening here!) ****
		 104 WININET_HTTP_REQUEST_HANDLE_CREATED (A)
		 108 WININET_HTTP_REQUEST_HANDLE_CREATED (B)
		 200 WININET_HTTP_REQUEST
		1007 Wininet_SendRequest.Start
		1045 Wininet_Connect.Start
		1046 Wininet_Connect.Stop        Socket#1
		1031 Wininet_SendRequest_Main
		1037 Wininet_ReadData.Info
		1045 Wininet_Connect.Start
		1046 Wininet_Connect.Stop        Socket#2
		1031 Wininet_SendRequest_Main

	Note this interaction between WinINet and Winsock when invoked via URLMon (as in IExplore, etc.).
	Here we get two network requests with two different sockets and TCBs.
	The first, via HttpOpenRequest, has a WinINet ETW event with URL, etc.
	The second, via ThreadPool dispatch & CWxSocket, has only a Winsock ETW event, no URL, etc.

	The effect, when aggregated, is that many WinINet requests are each accompanied by a separate Winsock request:
	They have the same IP address, but different TCB and socket and final call stack, so they're listed separately, with no URL.

		urlmon.dll!CINet::INetAsyncStart
		urlmon.dll!CINet::INetAsyncOpen
		urlmon.dll!CINet::INetAsyncConnect
		urlmon.dll!CINetHttp::INetAsyncOpenRequest
		|- wininet.dll!HttpOpenRequestW
		|    ...
		|    ETW: WinINet Event ***
		|
		|- urlmon.dll!CINetHttp::INetAsyncSendRequest
		|  - wininet.dll!HttpSendRequestW
		|    ...
		|    KernelBase.dll!TrySubmitThreadpoolCallback
		~    ...<Thread Pool>...
		|    wininet.dll!CSocket::InitializeSocket
		|    wininet.dll!CWxSocket::CreateInstance
		|    wininet.dll!CWxSocket::Initialize
		|    ws2_32.dll!WSASocketW
		|    mswsock.dll!WSPSocket
		|    ...
		|    ETW: Winsock Event ***

	ActivityIds are robust and consistent:
		The first QWORD is based on the handle of the current event and a counter.
		(The second QWORD is random, but constant for the handle/counter.)
		DNS-Client and Winsock-NameResolution events invoked by WinINet share the same ActivityId as the invoker.
	*/

	public class Request : Gatherable, IGraphableEntry
	{
		public readonly QWord qwConnect; // not unique
		public QWord qwContext; // not unique
		public QWord qwRequest; // not unique

		public QWord qwId; // unique per process

		// Some methods need this style of timestamp via IGraphable.
		public readonly TimestampETW timeRef;

		public readonly TimestampUI timeOpen;
		public TimestampUI timeSend; // Zero = uninitialized, MaxValue = correlating, timeN = initialized
		public TimestampUI timeClose1;
		public TimestampUI timeClose2;

		public readonly IDVal pid; // for correlation
		public IDVal tid1; // for correlation
		public IDVal tid2;

		public uint iDNS;
		public uint iAddr;
		public uint iTCB;

		public uint cbSend;
		public uint cbRecv;

		public string strServerName;
		public string strURL;
		public readonly string strMethod; // GET/POST
		public string strStatus; // "HTTP/1.1 400 Bad Request"
#if AUX_TABLES
		public string strServerAlt;
#endif // AUX_TABLES

		public IPEndPoint addrRemote; // includes the port

		public uint portS; // the port of the server, not necessarily of the remote address (eg. VPN)
		public ushort socket; // TCP socket identifier

		public bool fStopped;
		public bool fCacheHit;

		public XLink xlink;
		public IStackSnapshot stack;

#if DEBUG
		// We can link WinINet to Winsock, but for now we just mark the Winsock connection.
		public WinsockAFD.Connection cxnWinsock;
#endif // DEBUG

		public string Status => this.strStatus ?? (this.fCacheHit ? "PreNet Cache Hit" : String.Empty);

		public Request(QWord hConnection, string strMethod, IDVal pid, in TimestampETW timeStamp)
		{
			this.qwConnect = hConnection;
			this.timeRef = timeStamp;
			this.timeOpen = timeStamp.ToGraphable();
			this.timeClose1.SetMaxValue();
			this.timeClose2.SetMaxValue();
			this.pid = pid;
			this.strMethod = strMethod;
		}

		// Implement IGraphableEntry
		public IDVal Pid => this.pid;
		public IDVal TidOpen => this.tid1;
		public TimestampETW TimeRef => this.timeRef;
		public TimestampUI TimeOpen => this.timeOpen;
		public TimestampUI TimeClose => this.timeClose2; // TODO: timeClose1 or 2?
		public IStackSnapshot Stack => this.stack;
		public XLinkType LinkType => this.xlink.typeNext;
		public uint LinkIndex => this.xlink.IFromNextLink;
	}

	public class WinINetTable : List<Request>
	{
		readonly AllTables allTables;

		public WinINetTable(int capacity, in AllTables _allTables) : base(capacity) { this.allTables = _allTables; }

		/*
			Find the request in the given Process with the given Connect handle,
			and either the same Context or not yet initialized.
				RequestCreatedB
				HTTPRequest_Start
		*/
		Request FindRequestByCxn(QWord qwConnect, QWord qwContext, IDVal pid, in TimestampUI timeStamp)
		{
			for (int iReq = this.Count-1; iReq >= 0; --iReq)
			{
				Request req = this[iReq];
				if (req.pid == pid && timeStamp.Between(in req.timeOpen, in req.timeClose2) && req.qwConnect == qwConnect)
				{
					if (req.qwContext == qwContext)
						return req;

					if ((req.qwContext | req.qwRequest) == 0) // not yet initialized
						return req;
				}
			}
			return null;
		}


		/*
			Return the most recent Request with the give ID and Process ID.
			The Request could be stopped.
		*/
		Request FindRequestById(in IGenericEvent evt)
		{
			IDVal pid = evt.ProcessId;
			QWord qwId = GetQwId(in evt);
			AssertCritical(qwId != 0);
			Request req = this.FindLast(r => r.pid == pid && r.qwId == qwId);
#if DEBUG
			if (req != null)
			{
				// Confirm that there are no other open Requests with this PID & handle,
				// which would require reworking this function.
				List<Request> rgReq = this.FindAll(r => r.pid == pid && r.qwId == qwId);
				foreach (Request reqT in rgReq)
					AssertCritical(reqT == req || reqT.fStopped);
			}
#endif // DEBUG
			return req;
		}


		/*
			Close the Request by setting the various fields.
			The remote address (sockAddr) usually has a port.
			Redundant calls are fine.
		*/
		public void StopRequest(in Request req, in SocketAddress sockAddr)
		{
			AssertInfo(req.qwConnect != 0);
			AssertImportant(req.timeSend.HasValue());

			if (req.fStopped) return;

			req.fStopped = true;

			if (req.addrRemote.Empty())
			{
				uint iDNSNew = 0;  // none
				uint iAddrNew = 1; // first
				if (sockAddr != null)
				{
					// This is the remote address, and it usually has a port.
					// The other port value is that of the server request...not always the same.
					IPEndPoint ipep = NewEndPoint(in sockAddr);
					if (ipep.Port == 0)
						ipep.Port = (int)req.portS;

					req.addrRemote = ipep;
				}
				else if (req.iDNS != 0)
				{
					iDNSNew = req.iDNS;
					iAddrNew = req.iAddr;
				}
				else
				{
					// Build the server name from the URL, if needed.

					if (req.strServerName == null && req.strURL != null)
					{
						string strServerName = ServerNameFromURL(req.strURL);
						if (!String.IsNullOrEmpty(strServerName))
							req.strServerName = strServerName;
					}

					// Look up the DNS entry based on the server name.

					if (req.strServerName != null)
					{
						// We REALLY need an IP address, but it's not in this record(!?).
						// Look up an IP address from the server name, and there SHOULD be one from the DNSQuery.Stop.

						iDNSNew = this.allTables.dnsTable.IFindDNSEntryByServer(req.strServerName);
						req.iDNS = iDNSNew;
					}
				}

				if (iDNSNew != 0)
				{
					AssertCritical(req.addrRemote == null); // else preserve the port
					IPAddress addrNew = this.allTables.dnsTable.AddressFromI(iDNSNew, iAddrNew);
					if (addrNew != null)
						req.addrRemote = new IPEndPoint(addrNew, (int)req.portS);
				}

				AssertInfo(!req.addrRemote.Empty());
				AssertImportant(FImplies(req.addrRemote.Empty(), req.socket == 0)); // else there's still hope

				// Without an IP address, there's nothing more that can be done!
				if (req.addrRemote.Empty())
					return;
			}
#if DEBUG
			else if (sockAddr != null)
			{
				IPEndPoint ipep = NewEndPoint(in sockAddr);
				AssertImportant(req.addrRemote.Port == ipep.Port || req.addrRemote.Port == 0);
				AssertImportant(req.addrRemote.Equals(ipep.Address));
			}
#endif // DEBUG

			// Tie this record to a DNS entry.

			if (req.iDNS == 0)
			{
				uint iDNS = this.allTables.dnsTable.IDNSFromAddress(req.addrRemote.Address);

				if (iDNS == 0 && req.strServerName != null)
				{
					// Second chance for a Name<->Address mapping that didn't show up.
					this.allTables.dnsTable.AddDNSEntry(req.strServerName, req.addrRemote.Address, ref iDNS);
				}

				req.iDNS = iDNS;
			}

			// Tie this record to a TCB and to a Winsock connection.
			// But if there's no port then there was no TCB or Winsock, such as case 5 redirect, above.
			// Likewise if there's no socket, such as case 3 above.
			// Note that it is possible (case 6 above) to call this twice with the same Request but a different socket.

			if (req.socket != 0 && req.addrRemote.Port != 0)
			{
				if (req.iTCB == 0)
					req.iTCB = this.allTables.tcpTable.CorrelateByAddress(in req.addrRemote, req.pid, req.tid2, req.socket, Protocol.WinINet);

				WinsockAFD.Connection cxn = this.allTables.wsTable.CorrelateByAddress(req.addrRemote, req.iTCB, req.socket, req.pid, 0/*tid*/);

				if (cxn != null)
				{
					AssertImportant((cxn.grbitType & (byte)Protocol.Winsock) != 0);

					cxn.grbitType |= (byte)Protocol.WinINet;
#if DEBUG
					if (req.cxnWinsock == null)
						req.cxnWinsock = cxn;
#endif // DEBUG
				}
			}
		} // StopRequest


		[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
		struct GuidUnion
		{
			[System.Runtime.InteropServices.FieldOffset(0)]
			public Guid g0;
			[System.Runtime.InteropServices.FieldOffset(0)]
			public QWord qw1;
			[System.Runtime.InteropServices.FieldOffset(sizeof(QWord))]
			public QWord qw2;
		}

		GuidUnion gu = new GuidUnion(); // reusable when single-threaded

		// Return the first 8 bytes of the ActivityId Guid as a QWord.
		QWord GetQwId(in IGenericEvent evt)
		{
			gu.g0 = evt.ActivityId;
			return gu.qw1;
			// Here's a prettier (and thread-safe) but less efficient way to do it.
			// return BitConverter.ToUInt64(evt.ActivityId.ToByteArray());
		}


		public static readonly Guid guid = new Guid("{43d1a55c-76d6-4f7e-995c-64c711e5cafe}"); // Microsoft-Windows-WinINet

		enum WINET
		{
			RequestCreatedA = 104,
			HandleClosed = 105,
			RequestCreatedB = 108,
			HTTPRequest_Start = 200,
			TCP_Connection = 301,
			DNSQuery_Start = 304, // unused
			DNSQuery_Stop1 = 305, // cache miss
			DNSQuery_Stop2 = 307, // cache hit
			SendRequest_Start = 1007,
			SendRequest_Stop = 1008,
			PreNet_CacheHit = 1017,
			SendRequest_Main = 1031,
			SendRequest_Extra = 1033,
			ReadData = 1037,
			Connect_Start = 1045,
			Connect_Stop = 1046,
			Redirect = 1049,
			ReadData_Indicated = 1064, // Task.WININET_STREAM_DATA_INDICATED (HTTP2)
		};

		public void Dispatch(in IGenericEvent evt)
		{
			uint size;
			uint socket;
			QWord qwRequest;
			Request req, reqNew;
			TimestampUI timeStamp;
			string strServerName;

			switch ((WINET)evt.Id)
			{
			case WINET.DNSQuery_Stop1: // cache miss
			case WINET.DNSQuery_Stop2: // cache hit
				uint iDNS = allTables.dnsTable.ParseDNSEntries(evt.GetString("HostName"), evt.GetString("AddressList"), out uint iAddr);
				if (iDNS != 0)
				{
					req = FindRequestById(in evt);
					if (req != null)
					{
						AssertImportant(req.iDNS == 0 || req.iDNS == iDNS);
						AssertImportant(req.iAddr == 0 || req.iAddr == iAddr);
						req.iDNS = iDNS;
						req.iAddr = iAddr;
					}
				}
				break;

			case WINET.RequestCreatedA:
				AssertImportant(FindRequestByCxn(evt.GetAddrValue("ConnectionHandle"), 0, evt.ProcessId, evt.Timestamp.ToGraphable()) == null);

				// NOTE: ObjectName == URL w/o server name
				req = new Request(evt.GetAddrValue("ConnectionHandle"), evt.GetString("Verb"), evt.ProcessId, evt.Timestamp)
				{
					qwId = GetQwId(in evt), // Unique value based on the handle and a counter.
					stack = evt.Stack
				};
				AssertCritical(req.qwId != 0);
				req.xlink.GetLink(evt.ThreadId, req.timeOpen, in allTables.threadTable);

				this.Add(req);
				break;

			case WINET.RequestCreatedB:
				// This record is not very useful.  Its data is mostly for DEBUG validation.
				// But the pwzServerName can be used for additional Name<->Address mapping.

				timeStamp = evt.Timestamp.ToGraphable();
				req = FindRequestByCxn(evt.GetAddrValue("ConnectionHandle"), 0, evt.ProcessId, timeStamp);
				AssertImportant(req != null);
				if (req == null) break;

				AssertCritical(req.qwId == GetQwId(in evt));
				AssertImportant(req.pid == evt.ProcessId);
				AssertCritical(req.strServerName == null);
				AssertCritical(req.addrRemote == null);

				req.strServerName = evt.GetString("ServerName");
				req.portS = evt.GetUInt32("ServerPort"); // pre-byte-swapped
				break;

			case WINET.HTTPRequest_Start:
				// This event is used to tie togther the call stack, request handle, request number in preceeding and subsequent records.
				// Its thread may not match the RequestCreated record, but it should match the SendRequest_Start record.
				// This may occur once or twice in the lifetime of a request, and if twice then its parameters will match the first.
				// In fact the HTTPRequest_Start/Stop events can seem rather spurious.

				QWord qwContext = evt.GetAddrValue("Context"); // often 0!
				timeStamp = evt.Timestamp.ToGraphable();
				req = FindRequestByCxn(evt.GetAddrValue("HINTERNET"), qwContext, evt.ProcessId, timeStamp);
				AssertImportant(req != null);
				if (req == null) break;

				AssertCritical(req.qwId == GetQwId(in evt));
				AssertImportant(req.pid == evt.ProcessId);
				AssertImportant(req.qwContext == 0 || req.qwContext == qwContext);

				if (req.qwContext == 0)
					req.qwContext = qwContext;

				if (req.tid1 == 0)
					req.tid1 = evt.ThreadId;

				break;

			case WINET.TCP_Connection:
				req = FindRequestById(in evt);
				if (req == null)
					req = new Request(evt.GetAddrValue("ConnectionHandle"), "Unknown", evt.ProcessId, evt.Timestamp);

				strServerName = (evt.GetUInt16("_ServerNameLength") != 0) ? evt.GetString("ServerName") : null;

				if (req.strServerName == null)
					req.strServerName = strServerName;
				else
					AssertInfo(req.strServerName.Equals(strServerName)); // can fail with a VPN

				socket = evt.GetUInt32("LocalPort");

				if (req.socket == 0)
					req.socket = (ushort)socket;
				else
					AssertImportant(req.socket == socket);

				break;

			case WINET.SendRequest_Start:
				req = FindRequestById(in evt);
				AssertInfo(req != null);
				if (req == null) break;

				timeStamp = evt.Timestamp.ToGraphable();
				qwRequest = evt.GetAddrValue("Request");

				AssertInfo(req.qwConnect != 0);

				if (req.qwRequest != 0)
				{
					// Case 2 above: Keep Alive / Connection Reused

					AssertImportant(req.tid1 != 0);
					AssertImportant(req.tid2 != 0);
					AssertImportant(req.strURL != null);
					AssertImportant(req.fStopped);
					AssertImportant(req.timeSend.HasValue());
					AssertImportant(!req.timeClose1.HasMaxValue());

					if (req.timeClose2.HasMaxValue())
						req.timeClose2 = timeStamp;

					// That Request is now stopped and closed.
					// Make a separate request.

					reqNew = new Request(req.qwConnect, req.strMethod, req.pid, evt.Timestamp)
					{
						qwId = req.qwId,
						tid1 = evt.ThreadId,
						strServerName = req.strServerName, // confirmed below
						portS = req.portS
					};
					this.Add(reqNew);

					req = reqNew;
				}

				AssertImportant(req.tid2 == 0);
				AssertCritical(req.tid1 == evt.ThreadId);
				AssertCritical(req.qwRequest == 0); // Case2 should already be covered above!
				AssertCritical(req.timeClose1.HasMaxValue());
				AssertImportant(!req.timeSend.HasValue());
				AssertImportant(req.strURL == null);

				req.qwRequest = qwRequest;
				req.timeSend = timeStamp;
				req.strURL = evt.GetString("AddressName");

				// SendRequest_Start is one of the few WinINet records which should have a call stack attached.
				// Make use of this second chance, if needed.

				if (req.stack == null && evt.Stack != null)
				{
					req.stack = evt.Stack;
					req.xlink.ReGetLink(evt.ThreadId, in timeStamp, in allTables.threadTable);
				}
# if DEBUG
				if (req.strServerName != null)
				{
					strServerName = ServerNameFromURL(req.strURL);
					if (strServerName != null)
						AssertImportant(String.Equals(strServerName, req.strServerName, StringComparison.OrdinalIgnoreCase));
				}
#endif // DEBUG
				break;

			case WINET.SendRequest_Stop:
				req = FindRequestById(in evt);
				AssertInfo(req != null);
				if (req == null) break;

				AssertInfo(req.qwConnect != 0);
				AssertImportant(req.timeSend.HasValue());
				AssertInfo(req.timeClose2.HasMaxValue()); // Sometimes WINET.HandleClosed comes early.
				AssertImportant(req.strURL != null);
				AssertImportant(req.strStatus == null);
				AssertInfo(req.tid2 != 0); // no Connect_Start

				if (req.timeClose1.HasMaxValue())
					req.timeClose1 = evt.Timestamp.ToGraphable();

				// Other ReadData events for this Request may arrive after this event, even when cbData!=0.

				if (evt.GetUInt32("StatusLineLength") != 0)
					req.strStatus = evt.GetString("StatusLine");

				break;

			case WINET.PreNet_CacheHit:
				req = FindRequestById(in evt);
				AssertImportant(req != null);
				if (req == null) break;

				req.fCacheHit = true;
				break;

			case WINET.Redirect:
				req = FindRequestById(in evt);
				AssertImportant(req != null);
				if (req == null) break;

				// Redirected: Close out the previous REQUEST and create a new one.

				AssertCritical(req.pid == evt.ProcessId);
				AssertInfo(req.qwConnect != 0);
				AssertImportant(req.timeSend.HasValue());
				AssertImportant(req.timeClose2.HasMaxValue());
				AssertImportant(req.strURL != null);
				AssertImportant(req.strStatus == null);
				AssertInfo(req.tid2 != 0); // no Connect_Start

				timeStamp = evt.Timestamp.ToGraphable();

				req.timeClose1 = timeStamp;
				req.timeClose2 = timeStamp;
				req.fStopped = true;
				req.strStatus = "Redirected";

				reqNew = new Request(req.qwConnect, "RDIR", evt.ProcessId, evt.Timestamp)
				{
					qwContext = req.qwContext,
					qwRequest = req.qwRequest,
					timeSend = timeStamp,
					tid1 = evt.ThreadId,
					qwId = req.qwId,
					strURL = evt.GetString("AddressName"),
					stack = req.stack
				};
				reqNew.strServerName = ServerNameFromURL(reqNew.strURL);
				reqNew.xlink.Copy(req.xlink);

				this.Add(reqNew);
				break;

			case WINET.SendRequest_Extra:
				// "The Wininet_SendRequest_Extra event reports additional request data (e.g. POST)"
			case WINET.SendRequest_Main:
				size = evt.GetUInt32("Size");
				if (size == 0) break;

				req = FindRequestById(in evt);
				AssertInfo(req != null);
				if (req == null) break;

				req.cbSend += size;
				break;

			case WINET.ReadData:
				size = evt.GetUInt32("Size");
				if (size == 0) break;

				qwRequest = evt.GetAddrValue("Request");
				if (qwRequest == 0) break; // HTTP2 - See ReadData_Indicated

				req = FindRequestById(in evt);
				AssertInfo(req != null);
				if (req == null) break;

				req.cbRecv += size;
				break;

			case WINET.ReadData_Indicated:
				size = evt.GetUInt32("Size");
				if (size == 0) break;

				// "The NULL Request fields in WinInet_ReadData events are caused by HTTP2 streams,
				// which use the Task.WININET_STREAM_DATA_INDICATED event to report the data read."

				req = FindRequestById(in evt);
				AssertInfo(req != null);
				if (req == null) break;

				req.cbRecv += size;
				break;

			case WINET.HandleClosed:
				req = FindRequestById(in evt);
				if (req == null) break;

				// Handle Case 3 above, if the request is closed before completing.

				timeStamp = evt.Timestamp.ToGraphable();

				if (req.timeClose1.HasMaxValue())
					req.timeClose1 = timeStamp;

				if (req.timeClose2.HasMaxValue())
					req.timeClose2 = timeStamp;

				AssertImportant(req.tid1 != 0);
				// If req.tid2==0 then there was no Connect.Start event. Too bad.

				StopRequest(req, null);

				break;

			case WINET.Connect_Start:
				req = FindRequestById(in evt);
				AssertInfo(req != null);
				if (req == null)
				{
					// The Request must have been logged before the beginning of the trace. Reconstruct it here.
					req = new Request(0, strNA, evt.ProcessId, evt.Timestamp)
					{
						qwRequest = evt.GetAddrValue("Request"),
						qwId = GetQwId(in evt),
						strURL = strNA,
						tid1 = evt.ThreadId
					};
					req.timeSend = req.timeOpen;
					this.Add(req);
				}
				else
				{
					AssertImportant(req.qwConnect != 0);
				}
				AssertImportant(req.timeSend.HasValue());
				AssertImportant(req.pid == evt.ProcessId);

				// We'll use this ThreadId to _help_ match this Connect_Start with its Connect_Stop.
				// We'll also try (again?) to correlate this Request with a TCB record
				// by finding the TcpRequestConnect record which happens on *this thread* before the Connect_Stop.
				// See tcpTable.CorrelateByAddress within StopRequest.

				req.tid2 = evt.ThreadId;
				break;

			case WINET.Connect_Stop:
				req = FindRequestById(in evt);
				AssertInfo(req != null);
				if (req == null) break;

				// Processing for this request begins on a new thread.

				AssertImportant(req.pid == evt.ProcessId);

				SocketAddress addrRemote = evt.GetRemoteAddress();
				socket = evt.GetUInt32("Socket");
# if DEBUG
				// Case 6 above: Multiple Connect_Stop in a single request.
				// Just make sure they reference the same IP Address.
				if (req.socket != 0 && socket != 0 && req.socket != socket && !req.addrRemote.Empty())
				{
					uint iTCB1 = this.allTables.tcpTable.CorrelateByAddress(req.addrRemote, evt.ProcessId, evt.ThreadId, req.socket, Protocol.WinINet);
					uint iTCB2 = this.allTables.tcpTable.CorrelateByAddress(req.addrRemote, evt.ProcessId, evt.ThreadId, socket, Protocol.WinINet);
					AssertImportant(iTCB1 != 0 && iTCB2 != 0);
					if (iTCB1 != 0 && iTCB2 != 0)
						AssertImportant(this.allTables.tcpTable.TcbrFromI(iTCB1).addrRemote.Equals(this.allTables.tcpTable.TcbrFromI(iTCB2).addrRemote));
				}
#endif // DEBUG
				// Last non-zero socket wins.
				if (socket != 0)
					req.socket = (ushort)socket;

				StopRequest(req, addrRemote);
				break;
			}
		}
	}
}
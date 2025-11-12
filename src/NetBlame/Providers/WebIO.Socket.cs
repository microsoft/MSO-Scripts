// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Windows.EventTracing.Events;

using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using IDVal = System.Int32; // type of Event.pid/tid / ideally: System.UInt32
using QWord = System.UInt64;


/*
	- Each Process may have multiple Sessions.
	- Each Session may have multiple Requests, and corresponds to one Process.
	- Each Request corresponds to one Session.
	- Each Request usually has one Connection. But it may a second Connection, such as for authentication.
	- Each Connection can have multiple Sockets, but only one at a time.
	- Each Socket corresponds to one TCB.
	- A Connection & Socket can be shared concurrently across multiple Requests within a Session when they reference the same URL.
	- A Connection can continue to receive data on its current Socket even after the Request is closed.
	- A Socket cannot continue to send/receive data, nor be used by a Connection/Request once it is closed. (A lingering Receive carries an error.)

	Reusable: HRequest, Request, Connection, HSocket

	TCP correlates to WebIO as TcpRequestConnect occurs on the same thread between ConnectionSocketConnect.Start/Stop with the same IP address.

	For SOCKETs we see these patterns:
	PATTERN 1 - Simple:
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 200 - ConnectionSocketConnect.Start - Thread = T1, Handle = H, AddrCount = 1+, Context = C
			TTTTTTTT-TTTT-TTTT-0000-000000000000       TcpRequestConnect, etc.       - Thread = T1 or 0
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 201 - ConnectionSocketConnect.Stop  - Thread = T2, Handle = H, AddrCount = 1+, Context = C
			...
			WWWWWWWW-WWWW-WWWW-VVVV-VVVVZZZZZZZ? 204 - ConnectionSocketClose         - Thread = T3, Handle = H

	PATTERN 2 - Multiple Sequential Sockets:
	A		XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 200 - ConnectionSocketConnect.Start - Thread = T1, Handle = H1, AddrCount = N,   Context = C
			TTTTTTTT-TTTT-TTTT-0000-000000000000       TcpRequestConnect, etc.       - Thread = T1
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 204 - ConnectionSocketClose         - Thread = T2, Handle = H1
			                                           ... ... ...
	B		XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 200 - ConnectionSocketConnect.Start - Thread = T3, Handle = H2, AddrCount = N-1, Context = C
			UUUUUUUU-UUUU-UUUU-0000-000000000000       TcpRequestConnect             - Thread = T3
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 204 - ConnectionSocketClose         - Thread = T4, Handle = H2
			                                           ... ... ...
	C		XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 200 - ConnectionSocketConnect.Start - Thread = T5, Handle = H3, AddrCount = 1,   Context = C
			SSSSSSSS-SSSS-SSSS-0000-000000000000       TcpRequestConnect             - Thread = T5
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 202 - ConnectionSocketConnect.Stop2 - Thread = T6, Handle = H3, AddrCount = 0,   Context = C
			WWWWWWWW-WWWW-WWWW-VVVV-VVVVZZZZZZZ?  19 - RequestClose.Start            - Thread = T7
			WWWWWWWW-WWWW-WWWW-VVVV-VVVVZZZZZZZ? 204 - ConnectionSocketClose         - Thread = T7, Handle = H3 (Here the ActivityID is not the HRequest.)
			WWWWWWWW-WWWW-WWWW-VVVV-VVVVZZZZZZZ?  30 - RequestClose.Stop             - Thread = T7

	PATTERN 3 - Multiple Parallel Connections/Sockets:
	A		RRRRRRRR-RRRR-RRRR-RRRR-RRRRRRRRRRRR  17 - RequestCreate                   - Request = R1, HRequest = XXXXXXXXXXXXXXXX, Session, HSession
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZZ 104 - RequestWaitingForConnection     - Request = R1, Connection = C1
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZZ 205 - ConnectionNameResolutionRequest - Connection = C1
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 206 - ConnectionNameResolutionRequest - Connection = C1
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 203 - ConnectionSocketCreate          - Connection = C1, HSocket = H1 
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 200 - ConnectionSocketConnect.Start   - Connection = C1, HSocket = H1, Thread = T1, AddrCount = N
			TTTTTTTT-TTTT-TTTT-0000-000000000000       TcpRequestConnect, etc.         - Thread = T1
	B		XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 206 - ConnectionNameResolutionRequest - Connection = C2
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 203 - ConnectionSocketCreate          - Connection = C2, HSocket = H2 
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 200 - ConnectionSocketConnect.Start   - Connection = C2, HSocket = H2, Thread = T2, AddrCount = N
			UUUUUUUU-UUUU-UUUU-0000-000000000000       TcpRequestConnect               - Thread = T2
	C		XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 206 - ConnectionNameResolutionRequest - Connection = C3
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 203 - ConnectionSocketCreate          - Connection = C3, HSocket = H3
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 200 - ConnectionSocketConnect.Start   - Connection = C3, HSocket = H3, Thread = T3, AddrCount = N
			SSSSSSSS-SSSS-SSSS-0000-000000000000       TcpRequestConnect               - Thread = T3
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 201 - ConnectionSocketConnect.Stop    - Connection = C1, HSocket = H1, AddrCount = N
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 21? - ConnectionSocketSend/Receive    - Connection = C1, HSocket = H1, cbSize
			                                           ... ... ...
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 201 - ConnectionSocketConnect.Stop    - Connection = C2, HSocket = H2, AddrCount = N
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 201 - ConnectionSocketConnect.Stop    - Connection = C3, HSocket = H3, AddrCount = N
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 21? - ConnectionSocketSend/Receive    - Connection = C2/3, HSocket = H2/3, cbSize
			                                           ... ... ...
			WWWWWWWW-WWWW-WWWW-VVVV-VVVVZZZZZZZ?  19 - RequestClose.Start              - Request = R1, HRequest = XXXXXXXXXXXXXXXX, Session, HSession
			WWWWWWWW-WWWW-WWWW-VVVV-VVVVZZZZZZZ?  30 - RequestClose.Stop               - Request = R1, HRequest = XXXXXXXXXXXXXXXX, Session, HSession

	PATTERN 4 - Sharing a Connection / Socket:
	A		RRRRRRRR-RRRR-RRRR-RRRR-RRRRRRRRRRRR  17 - RequestCreate                   - Request = R1, HRequest = XXXXXXXXXXXXXXXX, Session, HSession
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZZ 104 - RequestWaitingForConnection     - Request = R1, Connection = C1
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZZ 205 - ConnectionNameResolutionRequest - Connection = C1
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 206 - ConnectionNameResolutionRequest - Connection = C1
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 203 - ConnectionSocketCreate          - Connection = C1, HSocket = H1 
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 200 - ConnectionSocketConnect.Start   - Connection = C1, HSocket = H1, IPAddr:Port, cAddrMax
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 201 - ConnectionSocketConnect.Stop    - Connection = C1, HSocket = H1, cAddrMax
			XXXXXXXX-XXXX-XXXX-YYYY-YYYYZZZZZZZ? 21? - ConnectionSocketSend/Receive    - Connection = C1, HSocket = H1, cbSize
			... etc. ...
			SSSSSSSS-SSSS-SSSS-SSSS-SSSSZZZZZZZZ  19 - RequestClose.Start              - Request = R1, HRequest = XXXXXXXXXXXXXXXX
			SSSSSSSS-SSSS-SSSS-SSSS-SSSSZZZZZZZZ  19 - RequestClose.Stop               - Request = R1, HRequest = XXXXXXXXXXXXXXXX
			------------------------------------
	B		QQQQQQQQ-QQQQ-QQQQ-QQQQ-QQQQQQQQQQQQ  17 - RequestCreate                   - Request = R2, HRequest = UUUUUUUUUUUUUUUU, Session, HSession
			UUUUUUUU-UUUU-UUUU-AAAA-AAAABBBBBBBB 104 - RequestWaitingForConnection     - Request = R2, Connection = C1? // Connection not reliable here.
			UUUUUUUU-UUUU-UUUU-AAAA-AAAABBBBBBB? 21x - ConnectionSocketSend/Receive    - Connection = C1, HSocket = H1, cbSize
			... etc. ...
			TTTTTTTT-TTTT-TTTT-TTTT-TTTTBBBBBBB?  19 - RequestClose.Start              - Request = R2, HRequest = UUUUUUUUUUUUUUUU
            TTTTTTTT-TTTT-TTTT-TTTT-TTT?BBBBBBB? 204 - ConnectionSocketClose [OR LATER]- Connection = C1, HSocket = H1
			TTTTTTTT-TTTT-TTTT-TTTT-TTTTBBBBBBB?  19 - RequestClose.Stop               - Request = R2, HRequest = UUUUUUUUUUUUUUUU
*/

namespace NetBlameCustomDataSource.WebIO
{
	// NOTE: A Socket instance may be shared across multiple Requests via a shared Connection.

	public class Socket
	{
		public readonly QWord qwSocket;

		public readonly TimestampUI timeStart;
		public TimestampUI timeStop;
		public TimestampUI timeClose;

		// for correlation with TCBs
		public readonly IDVal tidConnect;

		public uint iTCB;
		public uint iDNS;  // Used only for correlation: RequestTable.CorrelateByAddress
		public uint iAddr; // 1-based index to address within the DNS entry
		public uint port;
#if DEBUG
		public readonly QWord qwConnection;
		public readonly QWord qwContext;
		public WinsockAFD.Connection cxnWinsock;

		// cumulative data xfer
		public uint cbSend;
		public uint cbRecv;

		public uint cRef;

		public void AddRef() { ++this.cRef; }
#else // !DEBUG
		public void AddRef() {}
#endif // !DEBUG

		public bool FStopped => !this.timeStop.HasMaxValue();
		public bool FClosed => !this.timeClose.HasMaxValue();

		public Socket(in IGenericEvent evt)
		{
			this.qwSocket = evt.GetUInt64("SocketHandle");
#if DEBUG
			this.qwConnection = evt.GetAddrValue("Connection");
			this.qwContext = evt.GetAddrValue("Context");
#endif // DEBUG
			this.tidConnect = evt.ThreadId;
			this.timeStart = evt.Timestamp.ToGraphable();
			this.timeStop.SetMaxValue();
			this.timeClose.SetMaxValue();
		} // ctor
	} // Socket
} // NetBlameCustomDataSource.WebIO

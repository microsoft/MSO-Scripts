// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using IDVal = System.Int32; // type of Event.pid/tid / ideally: System.UInt32
using QWord = System.UInt64;

using static NetBlameCustomDataSource.Util;

namespace NetBlameCustomDataSource.WebIO
{
	public class Connection
	{
		public QWord qwConnection; // Other Requests may reference this Connection value.
		public uint cbSend;
		public uint cbRecv;
		public uint error;
		public Socket socket;      // Other Requests may reference this socket.
		public string strHeader;

		// Thread for matching TcpIpEvents: ConnectionSocketConnect_Start matches TcpRequestConnect
		public IDVal tidTCB;

		// Thread of most recent Receive activity: ConnectionSocketReceive_Stop
		public IDVal tidRecv;

		// Because of a defect in the way ConnectionSocketSend.Stop works (in earlier versions of Windows),
		// we may have to collect cbSend indirectly. See SendSocketTCB.

		// Thread of most recent Send activity, between: ConnectionSocketSend_Start/Stop
		public IDVal tidSend;
		public uint cbSendTCB;

		public QWord ctxSend;
		public QWord ctxRecv;

		// There's another one of these used by an earlier Request.
		public bool fDuplicate;
#if DEBUG
		public bool fOutdated;
		public bool fTransferred;
#endif // DEBUG

		public Connection(QWord qwConnection, Socket socket)
		{
			this.qwConnection = qwConnection;
			this.socket = socket;
			socket?.AddRef();
		}

		public bool MatchTCB(uint iTCB, uint iDNS, uint iAddr, IDVal tid)
		{
			if (tid != 0 && this.tidTCB != tid) return false;
			if (this.socket.iTCB != 0)
				return this.socket.iTCB == iTCB; // TCB + maybe tid: fairly confident
			else if (tid != 0)
				return this.socket.iDNS == iDNS && this.socket.iAddr == iAddr; // DNS + tid: fairly confident
			else
				return false; // no TCB, no tid: very uncertain
		}

		[Conditional("DEBUG")]
		public void TestSocket(in Microsoft.Windows.EventTracing.Events.IGenericEvent evt, QWord qwCxn)
		{
			QWord qwSocket = evt.TryGetUInt64("SocketHandle"); // ConnectSocketStop, etc.
			if (qwSocket == 0)
				qwSocket = evt.TryGetUInt64("Socket"); // ConnectSocketClose
#if DEBUG
			AssertCritical(qwSocket != 0);
			AssertCritical(this.socket != null);
			AssertImportant(this.socket.qwSocket == qwSocket || (this.socket.FClosed && qwSocket == QWord.MaxValue));
			AssertImportant(this.socket.qwConnection == qwCxn);
			AssertImportant(this.qwConnection == qwCxn);
#endif // DEBUG
		}
	}
}
// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using Microsoft.Windows.EventTracing.Events;

using NetBlameCustomDataSource.WinHTTP;

using static NetBlameCustomDataSource.Util;

using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using QWord = System.UInt64;
using IDVal = System.Int32;

namespace NetBlameCustomDataSource.WebIO
{
	public class WebIOTable
	{
		readonly AllTables allTables;

		// Created in the constructor.
		public readonly RequestTable requestTable;
		public readonly SessionTable sessionTable;

		public bool fHaveSendCounts; // optimization

		public WebIOTable(int capacity, in AllTables _allTables)
		{
			this.allTables = _allTables;

			// TODO: Smarter initialization?
			this.sessionTable = new SessionTable(capacity);
			this.requestTable = new RequestTable(capacity);
		}


		public static readonly Guid guid = new Guid("{50b3e73c-9370-461d-bb9f-26f32d68887d}"); // Microsoft-Windows-WebIO

		public enum WIO
		{
			// Level 4:
			SessionCreate = 5,
			SessionClose_Start = 7,
			RequestCreate = 17,
			RequestClose_Start = 19, // unused
			SessionClose_Stop = 29, // unused
			RequestClose_Stop = 30,
			RequestHeader_Send = 100,
			RequestHeader_Recv = 101,
			RequestWaitingForConnection_Stop = 104,
			RequestSend_Start = 130, // unused
			RequestSend_Stop = 131, // unused
			ConnectionSocketConnect_Start = 200,
			ConnectionSocketConnect_Stop = 201,
			ConnectionSocketConnect_Stop2 = 202,
			ConnectionSocketCreate = 203, // unused
			ConnectionSocketClose = 204,
			ConnectionNameResolutionRequest_Start = 205,
			ConnectionNameResolutionRequest_Stop = 206,
			ConnectionSocketSend_Start = 213,
			ConnectionSocketSend_Stop = 214,
			ConnectionSocketReceive_Start = 215,
			ConnectionSocketReceive_Stop = 216,
			// Level 5:
			// ThreadAction in WinHTTP.cs (59995-59998)
		};

/* ActivityId relationships:

	For these tasks the first QWORD of the ActivityId = Request Handle
		WebIO:Task.ThreadAction.Queue/.Cancel/.Start/.Stop
		WebIO:Task.RequestWaitingForConnection
		WebIO:Task.ConnectionSocketCreate*
		WebIO:Task.ConnectionSocketConnect*
		WebIO:Task.ConnectionSocketSend*#
		WebIO:Task.ConnectionSocketReceive*#
		WebIO:Task.ConnectionNameResolutionRequest.Start
		WebIO:Task.ConnectionNameResolutionRequest.Stop*
		WebIO:Task.ConnectionNameResolution
		WebIO:Task.RequestHeader*(ID=101)
		DNS-Client:_
		Winsock-NameResolution:WinsockGai
			* Not Reliably.
			# See PATTERN 4B - Shared Connection. See: GetConnection()

	For these tasks the ActivityId is the same:
		WebIO:Task.RequestClose.Start (19)
		WebIO:Task.ConnectionSocketClose (204)
		WebIO:Task.RequestClose.Stop (30)

	For these tasks the ActivityId is the same:
		WebIO:Task.RequestCreate
		WinHttp:ThreadAction.Start[Queue]/.Cancel/.Start/.Stop

	For these tasks the last half of the ActivityId is the same:
		WebIO:RequestClose
		WebIO:SessionClose
		(But note that multiple requests can be associated with a session.)

	Note: For all *:ThreadAction.* the ActivityId is less relevant (but should match).
		Use the Context field to track the ThreadPool actions.
*/

		static public bool IsSocketReceiveStopClose(in IGenericEvent evt)
		{
			WIO wio = (WIO)evt.Id;
			return wio == WIO.ConnectionSocketReceive_Start || wio == WIO.ConnectionSocketReceive_Stop
				|| wio == WIO.ConnectionSocketConnect_Stop || wio == WIO.ConnectionSocketConnect_Stop2
				|| wio == WIO.ConnectionSocketClose;
		}


		/*
			Get a value which represents a Request handle, derived from the ActivityId.
			This value is not always the handle of the true corresponding Request.
		*/
		static public QWord GetHReq(in IGenericEvent evt)
		{
			AssertCritical(evt.Id == (int)WIO.RequestWaitingForConnection_Stop // always valid?
					|| evt.Id == (int)WIO.ConnectionSocketCreate
					|| evt.Id == (int)WIO.ConnectionSocketConnect_Start
					|| evt.Id == (int)WIO.ConnectionSocketConnect_Stop
					|| evt.Id == (int)WIO.ConnectionSocketConnect_Stop2
					|| evt.Id == (int)WIO.ConnectionSocketClose // valid in one case
					|| evt.Id == (int)WIO.RequestHeader_Send
					|| evt.Id == (int)WIO.RequestHeader_Recv
					|| evt.Id == (int)WIO.ConnectionSocketSend_Start
					|| evt.Id == (int)WIO.ConnectionSocketSend_Stop
					|| evt.Id == (int)WIO.ConnectionSocketReceive_Start
					|| evt.Id == (int)WIO.ConnectionSocketReceive_Stop
					|| evt.Id == (int)WIO.ConnectionNameResolutionRequest_Start // always valid?
					|| evt.Id == (int)WIO.ConnectionNameResolutionRequest_Stop
					|| evt.Id >= (int)ThreadAction.First && evt.Id <= (int)ThreadAction.Last);

			// Return the first 8 bytes of the ActivityId Guid.
			return BitConverter.ToUInt64(evt.ActivityId.ToByteArray());
		}

		/*
			Get a value which is probably the Request Id.
			The low 32 bits are in the ActivityId.
			The high 32 bits are inferred from an adjacent allocation within the same heap.
		*/
		static public QWord GuessRequest(in IGenericEvent evt, QWord qw)
		{
			uint dw = BitConverter.ToUInt32(evt.ActivityId.ToByteArray(), 8);
			qw = (qw & 0xFFFFFFFF00000000) | dw;
			return qw;
		}

		Request RestoreRequest(in IGenericEvent evt, QWord qwReq)
		{
			Request req = new Request(evt, GetHReq(in evt)); // GetHReq not always dependable here, bit it's the best we've got.
			req.qwRequest = qwReq;
			req.xlink.ReGetLink(evt.ThreadId, in req.timeOpen, in allTables.threadTable); // Useful??
			this.requestTable.Add(req);
			return req;
		}

		Request RestoreRequestCxn(in IGenericEvent evt, QWord qwCxn)
		{
			Request req = RestoreRequest(in evt, GuessRequest(in evt, qwCxn));
			req.qwConnectionCur = qwCxn;
			req.ConfirmValidity();
			return req;
		}


		const uint S_OK = 0;

		public void Dispatch(in IGenericEvent evt)
		{
			QWord qw;
			QWord hReq;
			Request req;
			Connection cxn;
			Socket socket;
			TimestampUI timeStamp;

			switch ((WIO)evt.Id)
			{
			case WIO.SessionCreate:
				AssertImportant(evt.GetUInt32("Error") == S_OK);
				timeStamp = evt.Timestamp.ToGraphable();
				sessionTable.AddSession(evt.GetAddrValue("Session"), evt.GetUInt64("SessionHandle"), evt.ProcessId, evt.ThreadId, in timeStamp, evt.Stack, in allTables.threadTable);
				break;

			case WIO.SessionClose_Start:
				AssertImportant(evt.GetUInt32("Error") == S_OK);
				timeStamp = evt.Timestamp.ToGraphable();
				sessionTable.CloseSession(evt.GetAddrValue("ApiObject"), evt.GetUInt64("ApiHandle"), in timeStamp);
				break;

			case WIO.RequestCreate:
				AssertImportant(evt.GetUInt32("Error") == S_OK);
				requestTable.AddRequest(in evt, in this.allTables);
				break;

			case WIO.RequestClose_Stop:
				AssertImportant(evt.GetUInt32("Error") == S_OK);
				timeStamp = evt.Timestamp.ToGraphable();
				req = requestTable.CloseRequest(evt.GetAddrValue("ApiObject")/*qwReq*/, evt.GetUInt64("ApiHandle")/*hReq*/, evt.ProcessId, in timeStamp);
				break;
#if DEBUG
			case WIO.RequestHeader_Send:
				try
				{
					// Parses ALL of this event's fields.
					qw = evt.GetAddrValue("Request");
					req = requestTable.FindRequest(qw, evt.ProcessId, evt.Timestamp.ToGraphable());
					AssertImportant(req != null);
					req?.HandleValidity(in evt, true);
				}
				catch
				{
					// Parsing the "Headers" field threw an exception.
					hReq = GetHReq(in evt); // not a field
					req = requestTable.FindOpenRequestByHReq(hReq, evt.ProcessId);
					AssertImportant(req != null);
					req?.HandleValidity(in evt, false);
				}
				break;
#endif // DEBUG
			case WIO.RequestHeader_Recv:
				requestTable.AddHeader(in evt);
				break;

			case WIO.ConnectionSocketConnect_Start:
				AssertImportant(evt.GetUInt32("Error") == S_OK);

				qw = evt.GetAddrValue("Connection");
				req = requestTable.FindOpenRequestByConnection(qw, evt.ProcessId);

				AssertInfo(req != null);
				if (req == null)
					req = RestoreRequestCxn(in evt, qw);

				AssertInfo(req.qwRequest == GuessRequest(in evt, qw));
				AssertImportant(req.FOpen);
				req.ConfirmValidity(); // valid qwConnectionCur

				if (req.rgConnection == null)
					req.rgConnection = new List<Connection>((int)evt.GetUInt64("RemainingAddressCount"));

				// New Socket and Connection

				socket = new Socket(in evt);
				socket.iDNS = this.allTables.dnsTable.ParseAddressPortString(evt.GetAddressString(), out socket.iAddr, out socket.port);

				cxn = new Connection(qw, socket);
				cxn.tidTCB = evt.ThreadId;
				req.rgConnection.Add(cxn);
				break;

			case WIO.ConnectionSocketConnect_Stop2:
				// Stop2 is a rare event which ends a set of multiple connections attached to the same request.
			case WIO.ConnectionSocketConnect_Stop:
				AssertImportant(evt.GetUInt32("AddressLength") == 0);

				cxn = requestTable.FindConnectionByHandle(in evt, out req);
				AssertInfo(cxn != null);
				if (cxn == null) break;

				AssertImportant(req.qwConnectionCur == cxn.qwConnection); // Is this the right Request?

				req.FShared = false;

				cxn.error = evt.GetUInt32("Error");
				cxn.tidTCB = 0;
				socket = cxn.socket;
#if DEBUG
				AssertImportant(req.rgConnection.Capacity >= (int)evt.GetUInt64("RemainingAddressCount"));
				AssertImportant(cxn.qwConnection == evt.GetAddrValue("Connection"));
				AssertImportant(socket.qwConnection == evt.GetAddrValue("Connection"));
				AssertImportant(socket.qwContext == evt.GetAddrValue("Context"));
				AssertImportant(!socket.FStopped);
#endif // DEBUG
				socket.timeStop = evt.Timestamp.ToGraphable();
				break;

			case WIO.ConnectionSocketClose:
				AssertImportant(evt.GetUInt32("Result") == S_OK);
				requestTable.CloseSocket(in evt);
				break;

			case WIO.ConnectionSocketSend_Start:
				AssertImportant(evt.GetUInt32("Error") == S_OK);
				AssertImportant(evt.GetUInt64("Information") == 0);

				cxn = requestTable.GetConnection(in evt);
				if (cxn == null)
				{
					qw = evt.GetAddrValue("Connection");
					req = RestoreRequestCxn(in evt, qw);
					cxn = requestTable.SetRequestConnection(in evt, req, qw, -1);
					AssertCritical(req.rgConnection[^1] == cxn);
				}

				// Unfortunately we can't get a cbSend directly (in older versions of Windows), like we can for cbRecv.
				// Attract a TCB correlation via TCP.SendPosted.  See SendSocketTCB.

				AssertImportant(cxn.cbSendTCB == 0);
				AssertImportant(cxn.tidSend == 0);
				cxn.tidSend = evt.ThreadId;
				cxn.ctxSend = evt.GetAddrValue("Context");
				break;

			case WIO.ConnectionSocketSend_Stop:
				cxn = requestTable.GetConnection(in evt);
				if (cxn == null) break;

				AssertImportant(cxn.error == S_OK);
				cxn.error = evt.GetUInt32("Error");

				UInt64 cbSend = evt.GetUInt64("Information");
				AssertCritical(cbSend <= uint.MaxValue);

				// cbSend = data size from New Windows, else from Old Windows via TcpIp records.
				if (cbSend == 0)
					cbSend = cxn.cbSendTCB;
				else
					this.fHaveSendCounts = true;
#if DEBUG
				cxn.socket.cbSend += (uint)cbSend;
#endif // DEBUG
				cxn.cbSend += (uint)cbSend;

				// Stop attracting a TCB correlation via TCP.SendPosted.
				cxn.cbSendTCB = 0;
				cxn.tidSend = 0;
				cxn.ctxSend = 0;
				break;

			case WIO.ConnectionSocketReceive_Start:
				AssertImportant(evt.GetUInt32("Error") == S_OK);
				AssertImportant(evt.GetUInt64("Information") == 0);

				cxn = requestTable.GetConnection(in evt);
				if (cxn == null)
				{
					qw = evt.GetAddrValue("Connection");
					req = RestoreRequestCxn(in evt, qw);
					cxn = requestTable.SetRequestConnection(in evt, req, qw, -1);
					AssertCritical(req.rgConnection[^1] == cxn);
				}

				cxn.tidRecv = evt.ThreadId; // for correlation with WebIO, not necessarily with Stop
				cxn.ctxRecv = evt.GetAddrValue("Context");
				break;

			case WIO.ConnectionSocketReceive_Stop:
				UInt64 cbRecv = evt.GetUInt64("Information");
				AssertCritical(cbRecv <= uint.MaxValue);
				AssertImportant(FImplies(evt.GetUInt32("Error") != S_OK, cbRecv == 0));
				if (cbRecv == 0) break;

				cxn = requestTable.GetConnection(in evt);
				if (cxn == null)
				{
					qw = evt.GetAddrValue("Connection");
					req = RestoreRequestCxn(in evt, qw);
					cxn = requestTable.SetRequestConnection(in evt, req, qw, -1);
					AssertCritical(req.rgConnection[^1] == cxn);
				}

				// NOTE: The Socket can continue to receive data even after the Request is closed!
				// Even if the server sent some more data, it doesn't count for a closed Socket.
				if (cxn.socket.FClosed) break;

				cxn.tidRecv = evt.ThreadId; // for correlating the Header, not necessarily with Start
				cxn.cbRecv += (uint)cbRecv;
#if DEBUG
				cxn.socket.cbRecv += (uint)cbRecv;
#endif // DEBUG
				cxn.ctxRecv = 0;
				break;

			case WIO.RequestWaitingForConnection_Stop:
				timeStamp = evt.Timestamp.ToGraphable();
				qw = evt.GetAddrValue("Request");
				req = requestTable.FindRequest(qw, evt.ProcessId, timeStamp);
				if (req == null)
					req = RestoreRequest(in evt, qw);

				AssertImportant(req == requestTable.FindOpenRequestByHReq(GetHReq(in evt), evt.ProcessId));

				// This Connection value may later be abandoned in an abbreviated Connection (PATTERN 4B).
				// This Connection might already exist, and it (its Socket) needs to be transferred from another Request.
				// WARNING: N^2 Search

				QWord qwCxn = evt.GetAddrValue("Connection");
				IDVal pid = evt.ProcessId;
				Connection cxnT = null;
				Request reqT = this.requestTable.FindLast(r => r.pid == pid && r != req &&
						(cxnT = r.rgConnection?.FindLast(c => c.qwConnection == qwCxn && !c.socket.FClosed)) != null);

				AssertCritical((reqT==null) == (cxnT==null));

				if (cxnT != null)
				{
					cxnT.socket.timeStop.SetMaxValue(); // no longer stopped
					cxn = new Connection(qwCxn, cxnT.socket);

					if (req.rgConnection == null)
						req.rgConnection = new List<Connection>(1);

					req.rgConnection.Add(cxn);
#if DEBUG
					cxnT.socket.AddRef();
					cxnT.fOutdated = true;
					cxnT.fTransferred = false;
					cxn.fOutdated = false;
					cxn.fTransferred = true;
#endif // DEBUG
					cxn.fDuplicate = true;
				}
				// else this Connection will be added later: ConnectionSocketConnect_Start

				req.qwConnectionCur = qwCxn;
				break;

			case WIO.ConnectionNameResolutionRequest_Start:
				qw = evt.GetAddrValue("DnsQuery"); // DnsQuery = Connection
				hReq = GetHReq(in evt);
				req = requestTable.FindOpenRequestByHReq(hReq, evt.ProcessId);
				if (req == null)
					req = RestoreRequestCxn(in evt, qw);

				AssertImportant(req.FOpen);
				AssertImportant(req.qwConnectionCur == qw);

				// This HostName lacks the leading "https://"
				if (req.strURL == null)
					req.strURL = evt.GetString("HostName"); // Not the entire URL (which is missing), but better than nothing?
				else
					AssertImportant(req.strURL.IndexOf(evt.GetString("HostName"), StringComparison.OrdinalIgnoreCase) > 0);

				if (req.strServer == null)
					req.strServer = ServerNameFromURL(req.strURL);
				else
					AssertImportant(req.strServer.Equals(ServerNameFromURL(req.strURL), StringComparison.OrdinalIgnoreCase));

				break;

			case WIO.ConnectionNameResolutionRequest_Stop:
				qw = evt.GetAddrValue("DnsQuery"); // DnsQuery = Connection
				req = requestTable.FindOpenRequestByConnection(qw, evt.ProcessId);
				if (req == null)
					req = RestoreRequestCxn(in evt, qw);

				AssertImportant(req.FOpen);

				req.strServer = allTables.dnsTable.ConnectNameResolution(in evt, req.strServer);
				break;

			default:
				if ((ThreadAction)evt.Id >= ThreadAction.First && (ThreadAction)evt.Id <= ThreadAction.Last)
				{
					// OverlappedIO records are useful. WorkItem records, too. Timer records are not.

					ActionType actionType = (ActionType)evt.GetUInt32("EtwQueueActionType");
					AssertImportant(actionType == ActionType.OverlappedIO ||
							actionType == ActionType.Timer ||
							actionType == ActionType.WorkItem);

					if (actionType != ActionType.Timer)
						allTables.httpTable.DoDispatchEvent(in evt, actionType);
				}
				break;
			} // switch
		} // Dispatch
	} // WebIOTable
} // NetBlameCustomDataSource.WebIO

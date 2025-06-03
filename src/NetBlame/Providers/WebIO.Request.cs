// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using Microsoft.Windows.EventTracing.Events;
using Microsoft.Windows.EventTracing.Symbols;

using NetBlameCustomDataSource.Link;

using static NetBlameCustomDataSource.Util;

using TimestampETW = Microsoft.Windows.EventTracing.TraceTimestamp; // struct
using TimestampUI = Microsoft.Performance.SDK.Timestamp; // struct

using IDVal = System.Int32; // type of Event.pid/tid / ideally: System.UInt32
using QWord = System.UInt64;


namespace NetBlameCustomDataSource.WebIO
{
	public class Request : Gatherable, IGraphableEntry
	{
		public readonly TimestampETW timeRef;

		public readonly TimestampUI timeOpen;
		public TimestampUI timeClose;

		public QWord qwRequest;
		public QWord qwHandle;
		public readonly QWord qwSession;
		public readonly QWord hSession;
		public QWord qwConnectionCur; // Corresponds to: rgConnection[^1].qwConnection

		public uint iSession;

		public readonly IDVal pid;

		public IDVal tidStack; // ThreadID of the callstack, else of the request.
		public IStackSnapshot stack;
		public XLink xlink;

		public string strServer;
		public string strURL;
		public string strMethod; // GET/POST

		public List<Connection> rgConnection;

		public Request(in IGenericEvent evt)
		{
			this.qwRequest = evt.GetAddrValue("Request");
			this.qwHandle = evt.GetUInt64("RequestHandle");
			this.qwSession = evt.GetAddrValue("Session");
			this.hSession = evt.GetUInt64("SessionHandle");
			this.strURL = evt.GetString("URI");
			this.strServer = ServerNameFromURL(this.strURL);
			this.strMethod = evt.GetString("Method");
			this.pid = evt.ProcessId;
			this.tidStack = evt.ThreadId;
			this.stack = evt.Stack;
			this.timeRef = evt.Timestamp;
			this.timeOpen = evt.Timestamp.ToGraphable();
			this.timeClose.SetMaxValue();
		}

		// Use this partial constructor when the RequestCreate record was missed (such as near the beginning of the trace).
		public Request(in IGenericEvent evt, QWord hReq)
		{
			this.qwHandle = hReq;
			this.timeRef = evt.Timestamp;
			this.timeOpen = evt.Timestamp.ToGraphable();
			this.timeClose.SetMaxValue();
			this.pid = evt.ProcessId;
		}

		public bool FOpen => this.timeClose.HasMaxValue();

		public bool FShared { get; set; }

		public bool HasCurrentConnection =>
				this.qwConnectionCur != 0 &&
				this.qwConnectionCur == this.rgConnection?[^1].qwConnection;

		public bool HasCurrentOpenConnection =>
				this.HasCurrentConnection && this.FOpen;

		// Implement IGraphableEntry
#if DEBUG || AUX_TABLES
		public IDVal Pid => this.pid;
		public IDVal TidOpen => this.tidStack;
#else
		public IDVal Pid => 0;
		public IDVal TidOpen => 0;
#endif // DEBUG || AUX_TABLES
		public TimestampETW TimeRef => this.timeRef;
		public TimestampUI TimeOpen => this.timeOpen;
		public TimestampUI TimeClose => this.timeClose;
		public IStackSnapshot Stack => this.stack;
		public XLinkType LinkType => this.xlink.typeNext;
		public uint LinkIndex => this.xlink.IFromNextLink;
	} // Request


	public class RequestTable : List<Request>
	{
		// TODO: smart capacity?
		public RequestTable(int capacity) : base(capacity) { }

		public Request RequestFromI(uint iReq) => (iReq != 0) ? this[(int)iReq - 1] : null;

		public uint IFromRequest(in Request request) => (uint)(this.LastIndexOf(request) + 1); // 1-based


		public uint IFindRequest(QWord qwRequest, TimestampUI timeStamp)
		{
			return (uint)(this.FindLastIndex(r => r.qwRequest == qwRequest && timeStamp.Between(in r.timeOpen, in r.timeClose)) + 1); //1-based
		}

		/*
			Return the most recent Request with the given ID, ensuring it encloses the given timeStamp.
		*/
		public Request FindRequest(QWord qwRequest, IDVal pid, in TimestampUI timeStamp)
		{
			Request req = this.FindLast(r => r.qwRequest == qwRequest && r.pid == pid);
			if (req == null) return null;
			return timeStamp.Between(in req.timeOpen, in req.timeClose) ? req : null;
		}

		/*
			Return the most recent Request with the given handle and process ID.
			This Request might be closed.
		*/
		public Request FindRequestByHReq(QWord hReq, IDVal pid)
		{
			AssertImportant(hReq != 0);
			AssertImportant(pid != 0);
			Request req = this.FindLast(r => r.pid == pid && r.qwHandle == hReq);
#if DEBUG
			if (req != null)
			{
				// Confirm that there are no other open Requests with this process ID and handle,
				// which would require reworking this function.
				List<Request> rgReq = this.FindAll(r => r.pid == pid && r.qwHandle == hReq);
				foreach (Request reqT in rgReq)
					AssertCritical(reqT == req || !reqT.FOpen);
			}
#endif // DEBUG
			return req;
		}

		/*
			Find the most recent, open Request with the given Connection ID and Process ID.
		*/
		public Request FindOpenRequestByConnection(QWord qwCxn, IDVal pid)
		{
			return this.FindLast(r => r.qwConnectionCur == qwCxn && r.pid == pid && r.FOpen);
		}

		/*
			Return the open Request with the given handle and process ID.
		*/
		public Request FindOpenRequestByHReq(QWord hReq, IDVal pid)
		{
			Request req = FindRequestByHReq(hReq, pid);
			return (req != null && req.FOpen) ? req : null;
		}

		/*
			Find the Request and Connection with the given Connection Handle.
			This can return null Connection with non-null Request.
			This can return a Connection which doesn't (yet) belong to the Request (but should).
			A returned Connection is assumed to have a non-null Socket.
		*/
		public Connection FindConnectionByHandle_OLD(in IGenericEvent evt, out Request req)
		{
			Connection cxn = null;

			req = null;

			IDVal pid = evt.ProcessId;

			QWord qwCxn = evt.TryGetAddrValue("Connection"); // not ConnectionSocketClose

			if (qwCxn != 0)
			{
				// In some cases (ConnectionSocketReceive) we're looking for a Request which can be technically closed.

				QWord hreq = WebIOTable.GetHReq(in evt);

				req = FindRequestByHReq(hreq, pid);

				// Check all Connections of the expected Request.

				if (req != null && req.HasCurrentConnection)
					cxn = req.rgConnection.FindLast(c => c.qwConnection == qwCxn);
			}

			if (cxn == null)
			{
				if (qwCxn == 0)
					qwCxn = evt.GetAddrValue("Endpoint"); // ConnectionSocketClose

				AssertCritical(qwCxn != 0);

				// Check all Requests for their current Connection.
				// The request may already be closed for ConnectionSocketClose.

				req = this.FindLast(r => r.qwConnectionCur == qwCxn && r.pid == pid);

				if (req != null)
				{
					if (req.HasCurrentConnection)
						cxn = req.rgConnection[^1]; // current connection
				}
				else
				{
					// Check all Connections of all Requests, most recent first.

					Request reqT = this.FindLast(r =>
					{
						if (r.pid != pid) return false;
						cxn = r.rgConnection?.FindLast(c => c.qwConnection == qwCxn);
						return cxn != null;
					});
				}
			}

#if DEBUG
			// Verify that the Connection has the expected Socket.

			if (cxn != null)
			{
				QWord qwSocket = evt.TryGetUInt64("SocketHandle"); // ConnectSocketStop, etc.
				if (qwSocket == 0)
					qwSocket = evt.TryGetUInt64("Socket"); // ConnectSocketClose

				AssertCritical(qwSocket != 0);
				AssertCritical(cxn.socket != null);

				AssertImportant(cxn.socket.qwSocket == qwSocket || (cxn.socket.FClosed && qwSocket == QWord.MaxValue));
				AssertImportant(cxn.socket.qwConnection == qwCxn);
				AssertImportant(cxn.qwConnection == qwCxn);
			}
#endif // DEBUG

			return cxn;
		}


		public Connection FindConnectionByHandle(in IGenericEvent evt, out Request req)
		{
			Connection cxn = null;

			req = null;

			IDVal pid = evt.ProcessId;

			QWord qwCxn = evt.TryGetAddrValue("Connection"); // not ConnectionSocketClose

			if (qwCxn != 0)
			{
				// In some cases (ConnectionSocketReceive) we're looking for a Request which can be technically closed.

				QWord hreq = WebIOTable.GetHReq(in evt);

				req = FindRequestByHReq(hreq, pid);

				// Check all Connections of the expected Request.
				// The request may already be closed for ConnectionSocketClose.

				if (req?.qwConnectionCur == qwCxn)
				{
					if (req.HasCurrentConnection)
						cxn = req.rgConnection[^1];
					// else later add a new Connection to this Request.
				}
				else
				{
					// SocketReceive events are allowed to operate on a non-current Connection.

					if (WebIOTable.IsSocketReceiveStopClose(in evt))
						cxn = req?.rgConnection.FindLast(c => c.qwConnection == qwCxn);

					if (cxn == null)
						req = null;
				}
			}

			if (req == null || !req.FOpen)
			{
				if (qwCxn == 0)
					qwCxn = evt.GetAddrValue("Endpoint"); // ConnectionSocketClose (HReq is invalid)

				// The Request handle that we derived from the instruction's ActivityId is not always accurate.
				// Check all Requests for their current Connection, preferring a recent Open request.

				Request reqT = this.FindLast(r => r.HasCurrentOpenConnection && r.qwConnectionCur == qwCxn && r.pid == pid);

				if (reqT == null && req?.qwConnectionCur != qwCxn)
				{
					req = null;

					// Find a Closed request, sometimes needed for ConnectionSocketReceive/Close.

					if (WebIOTable.IsSocketReceiveStopClose(in evt))
						reqT = this.FindLast(r => r.HasCurrentConnection && r.qwConnectionCur == qwCxn && r.pid == pid);
				}

				if (reqT != null)
				{
					req = reqT;
					cxn = reqT.rgConnection[^1]; // current connection
#if DEBUG
					AssertImportant(reqT.qwConnectionCur == qwCxn);
					AssertImportant(FImplies(!WebIOTable.IsSocketReceiveStopClose(in evt), !cxn.fOutdated));
#endif // DEBUG
				}
			}
#if DEBUG
			// Verify that the Connection has the expected Socket.

			if (cxn != null)
			{
				QWord qwSocket = evt.TryGetUInt64("SocketHandle"); // ConnectSocketStop, etc.
				if (qwSocket == 0)
					qwSocket = evt.TryGetUInt64("Socket"); // ConnectSocketClose

				AssertCritical(qwSocket != 0);
				AssertCritical(cxn.socket != null);

				AssertImportant(cxn.socket.qwSocket == qwSocket || (cxn.socket.FClosed && qwSocket == QWord.MaxValue));
				AssertImportant(cxn.socket.qwConnection == qwCxn);
				AssertImportant(cxn.qwConnection == qwCxn);

				AssertImportant(!cxn.fOutdated || WebIOTable.IsSocketReceiveStopClose(in evt));
			}
#endif // DEBUG

			return cxn;
		}


		/*
			The Socket is probably there in the Request, created by ConnectionSocketConnect records.
			But the Request may be reusing a Socket, probably referenced by another Request.
			Or we may need to create a default Socket if we missed its creation.
		*/
		public Connection GetConnection(in IGenericEvent evt)
		{
			Connection cxn = this.FindConnectionByHandle(in evt, out Request req);

			QWord hReq = WebIOTable.GetHReq(in evt);
			if (req == null || (req.qwHandle != hReq && (!req.FOpen || req.FShared)))
			{
				Request reqT = null;
				QWord qwCxn = (cxn != null) ? cxn.qwConnection : evt.GetAddrValue("Connection");

				switch ((WebIOTable.WIO)evt.Id)
				{
				case WebIOTable.WIO.ConnectionSocketSend_Start:
				case WebIOTable.WIO.ConnectionSocketReceive_Start:
					// PATTERN 4B: Sharing a Connection with a closed Request!?
					// Or this handle might be used to send data for: Request.Header

					AssertImportant(req == null || !req.FOpen || req.FShared);
					if (req != null && req.FOpen && req.qwConnectionCur == qwCxn)
						break;

					// Here we don't completely trust hReq, but we also don't completely trust reqT.qwConnectionCur.
					// We do see: Microsoft-Windows-WebIO.RequestWaitingForConnection | Connection = <bogus value>
					// So go back to using the request identified by the request handle, if it has no connections.

					reqT = this.FindOpenRequestByHReq(hReq, evt.ProcessId);
					AssertImportant(reqT == null || !reqT.FShared);
					if (reqT != null && reqT.rgConnection == null)
					{
						req = reqT;
						req.qwConnectionCur = qwCxn;
					}
					else if (req == null)
					{
						// fall through to find the Request and Connection
						goto case WebIOTable.WIO.ConnectionSocketSend_Stop;
					}
					break;

				case WebIOTable.WIO.ConnectionSocketSend_Stop:
				case WebIOTable.WIO.ConnectionSocketReceive_Stop:
					AssertImportant(req == null || req.FOpen);
					AssertImportant(req == null || req.FShared);

					// Empirically we believe that this 'receive' (or even 'send') event is residual from a [soon to be] closed Request.
					// Find that one.
					// Or this handle might be used to receive data for: Request.Header

					IDVal pid = evt.ProcessId;
					reqT = this.FindLast(r => r != req && r.qwConnectionCur == qwCxn && r.pid == pid);
					AssertImportant(reqT == null || reqT.qwConnectionCur == evt.GetAddrValue("Connection"));
					if (reqT != null && (req == null || (reqT.qwHandle == hReq && reqT.FOpen))) // Is it a better match?
					{
						AssertImportant(reqT.HasCurrentConnection);
						if (reqT.HasCurrentConnection)
						{
							req = reqT;
							cxn = req.rgConnection[^1];
#if DEBUG
							AssertImportant(!cxn.fOutdated);
#endif // DEBUG
						}
					}
					break;

				default:
					AssertCritical(false);
					break;
				}
			}

			if (req == null) return null;

			// Hereafter must not return null;

			if (req.HasCurrentConnection)
			{
				AssertImportant(cxn != null);
				AssertImportant(cxn == req.rgConnection?.Find(c => c == cxn)); // cxn is in req.rgConnection[]
				return cxn;
			}

			// We didn't get a ConnectionSocketConnect event pair, apparently.
			// We must be reusing a Connection/Socket previously opened (PATTERN 4B).
			// Or this event occurs very near the beginning of the trace.

			req.FShared = true;

			if (req.rgConnection == null)
				req.rgConnection = new List<Connection>(1);
			else
				AssertCritical(!req.HasCurrentConnection); // No duplicates!

			if (cxn == null)
			{
				// The socket might have been created before the trace started. (Rare!)

				Socket socket = new Socket(in evt);

				cxn = new Connection(evt.GetAddrValue("Connection"), socket);
			}
			else
			{
				// Reusing a Connection attached to another Request.

				AssertCritical(cxn.socket != null);
				AssertImportant(!cxn.socket.FClosed);
				AssertImportant(cxn.socket.timeStart.HasValue());

				if (cxn.socket.FStopped)
					cxn.socket.timeStop = cxn.socket.timeClose; // infinity
			}

			req.qwConnectionCur = cxn.qwConnection;
			req.rgConnection.Add(cxn);

			AssertCritical(req.HasCurrentConnection);

			return cxn;
		}


		/*
			Find the Connecton / Socket related to the given TCB / TcpIp record.
		*/
		public Connection EnsureConnectionTCB(IDVal pid, IDVal tid, uint iTCB, in TimestampUI timeStamp, bool fSend)
		{
			for (int iReq = this.Count-1; iReq >= 0; --iReq)
			{
				Request req = this[iReq];
				if (req.pid != pid) continue;
				if (req.rgConnection == null) continue;
#if !DEBUG
				// Optimization. In DEBUG, verify its validity below.
				if (req.timeClose < timeStamp) continue;
#endif // !DEBUG
				Connection cxn = req.rgConnection.FindLast(c => c.tidSend == tid && c.socket.iTCB == iTCB);

				if (cxn == null && iReq == this.Count-1 && req.FShared)
				{
					// Socket (of the most recently created Connection) appears near the beginning of the trace, lacking full context.
					// If the match is nearly certain, apply the TCB context.
					cxn = req.rgConnection[^1];
					if ((fSend ? cxn.tidSend : cxn.tidRecv) == tid && cxn.tidTCB == 0 && cxn.socket.tidConnect == tid && cxn.socket.iTCB == 0 && !cxn.socket.FClosed)
					{
						cxn.tidTCB = tid;
						cxn.socket.iTCB = iTCB;
					}
					else
					{
						cxn = null;
					}
				}

				if (cxn != null)
				{
#if DEBUG
					AssertImportant(!cxn.fOutdated);
					AssertImportant(cxn.socket.timeClose > timeStamp);
					AssertInfo(req.timeClose > timeStamp);
#endif // DEBUG
					return cxn;
				}
			}
			return null;
		}



		/*
			Find the unique Socket created on the given time during the given time interval.
		*/
		public Socket CorrelateByTimeThread(uint iTCB, IDVal pid, IDVal tid, TimestampUI timeCreate, TimestampUI timeConnect)
		{
			AssertCritical(timeCreate.HasValue() && !timeCreate.HasMaxValue());
			AssertCritical(timeConnect.HasValue() && !timeConnect.HasMaxValue());

			Connection cxn = null;
			Request req = this.FindLast(r =>
					r.pid == pid &&
					(((cxn = r.rgConnection?[^1])?.socket.tidConnect ?? -1) == tid) &&
					cxn.socket.timeStart.Between(timeCreate, timeConnect));

			if (req == null)
				return null;
#if DEBUG
			// We should be able to find only one socket created on that thread within that timespan.
			Connection cxnT = null;
			Request reqT = this.FindLast(r =>
					r != req &&
					r.pid == pid &&
					(((cxnT = r.rgConnection?[^1])?.socket.tidConnect ?? -1) == tid) &&
					cxnT.socket.timeStart.Between(timeCreate, timeConnect));
			AssertImportant(reqT == null);

			AssertImportant(req.HasCurrentOpenConnection);
			AssertImportant(!cxn.fTransferred); // transferred sockets were already correlated
			AssertImportant(cxn.socket.iTCB == 0 || cxn.socket.iTCB == iTCB);
#endif // DEBUG
			cxn.socket.iTCB = iTCB;
			return cxn.socket;
		}


		public Socket CorrelateByAddress(uint iTCB, uint iDNS, uint iAddr, IDVal pid, IDVal tid)
		{
			AssertImportant(iTCB != 0);

			// Search the most recent Connection/Socket of each Request.
			Connection cxn = null;
			Request req = this.FindLast(r => r.pid == pid && ((cxn = r.rgConnection?[^1])?.MatchTCB(iTCB, iDNS, iAddr, tid) ?? false));

			if (req != null)
			{
#if DEBUG
				AssertImportant(req.HasCurrentOpenConnection);
				AssertImportant(!cxn.fTransferred); // transferred sockets were already correlated
				AssertImportant(cxn.socket.iTCB == 0 || cxn.socket.iTCB == iTCB);
#endif // DEBUG
				cxn.socket.iTCB = iTCB;
				return cxn.socket;
			}

#if DEBUG
			// Search all Connections/Sockets of each open Request.
			// We shouldn't find a match.
			req = this.FindLast(r => r.FOpen && r.rgConnection?.FindLast(c => c.MatchTCB(iTCB, iDNS, iAddr, tid)) != null);
			AssertImportant(req == null);
#endif // DEBUG

			return null;
		} // CorrelateByAddress


		public void AddHeader(in IGenericEvent evt)
		{
			Request req;
			string strHeader; // multi-line

			// WPA can choke on random unicode Header text and drop the fields, throwing: System.Text.DecoderFallbackException
			try	{
				if (evt.GetUInt16("Length") == 0) return;
				strHeader = evt.GetString("Headers");
				// GetHReq (from the ActivityId) is sometimes invalid for this record. Use the Request ID.
				QWord qwReq = evt.GetAddrValue("Request");
				req = this.FindRequest(qwReq, evt.ProcessId, evt.Timestamp.ToGraphable());
				}
			catch
				{
				// No Fields available.
				strHeader = "[Internal Decoding Error]";
				req = this.FindRequestByHReq(WebIOTable.GetHReq(evt), evt.ProcessId); // not reliable
				}
			if (req?.rgConnection == null || req.rgConnection.Count == 0) return;

			var rgLine = strHeader.Split(rgchEOLSplit, StringSplitOptions.RemoveEmptyEntries);
			if (rgLine.Length == 0) return;

			strHeader = rgLine[0]; // First line only

			// The corresponding Connection recently received data on this thread.
			// But that's only a rule of thumb.

			IDVal tid = evt.ThreadId;
			Connection cxn = req.rgConnection?.FindLast(c => c.tidRecv == tid);
			if (cxn == null)
				cxn = req.rgConnection[^1]; // last

			// There can be multiple headers per Connection: "200 Connection Extablished" then "403 Forbidden"
			// Last one wins, if it begins with: HTTP
			AssertInfo(cxn.strHeader == null);
			if (cxn.strHeader == null || !cxn.strHeader.StartsWith("HTTP", StringComparison.OrdinalIgnoreCase) || strHeader.StartsWith("HTTP", StringComparison.OrdinalIgnoreCase))
				cxn.strHeader = strHeader;
		}


		public void CloseSocket(in IGenericEvent evt)
		{
			// ConnectionSocketClose behaves like Stop in the case of (potentially) multiple Sockets. (See PATTERN 2, elsewhere.)
			// In that case the ActivityID is related to the HRequest (and the Socket's "Remaining Address Count" > 1).
			// Otherwise the ActivityID is not related to the HRequest.

			QWord qwCxn;
			Socket socket;
			TimestampUI timeStamp = evt.Timestamp.ToGraphable();
			Connection cxn = this.FindConnectionByHandle(in evt, out Request reqT);

			if (cxn != null)
			{
				cxn.tidSend = 0;
				cxn.tidRecv = 0;
				cxn.tidTCB = 0;

				socket = cxn.socket;
#if DEBUG
				qwCxn = evt.GetAddrValue("Endpoint"); // "Connection"
				AssertImportant(cxn.qwConnection == qwCxn);
				AssertImportant(socket.qwConnection == qwCxn);
				AssertImportant(!socket.FClosed);
#endif // DEBUG
				if (!socket.FStopped)
					socket.timeStop = timeStamp;

				socket.timeClose = timeStamp;
				return;
			}

			// All we know is the Connection and Socket, so we must search.

			qwCxn = evt.GetAddrValue("Endpoint"); // "Connection"

			for (int iReq = this.Count-1; iReq >= 0; --iReq)
			{
				Request req = this[iReq];
				if (req.pid != evt.ProcessId) continue;
				if (req.rgConnection == null) continue;

				cxn = req.rgConnection.FindLast(cxn => cxn.qwConnection == qwCxn);
				if (cxn == null) continue;

				// There may be some other Connections which share this Socket whose ThreadID values are not cleared here.
				cxn.tidTCB = 0;
				cxn.tidSend = 0;
				cxn.tidRecv = 0;

				socket = cxn.socket;
#if DEBUG
				AssertCritical(socket.qwConnection == qwCxn);
				AssertImportant(socket.qwSocket == evt.GetUInt64("Socket")); // else continue?
				AssertImportant(socket.FStopped);
				AssertImportant(!socket.FClosed);
#endif // DEBUG
				// This Socket instance may be shared across several Connections. This closes them all.
				socket.timeClose = timeStamp;
				break;
			}
		} // CloseSocket


		public void AddRequest(in IGenericEvent evt, in AllTables allTables)
		{
			Request request = new Request(in evt);

			// Confirm: No overlapping Requests with the same ID!
			AssertImportant(IFindRequest(request.qwRequest, request.timeOpen) == 0);

			// Confirm: No other open Requests with the same handle and process!
			AssertImportant(FindOpenRequestByHReq(request.qwHandle, request.pid) == null);

			SessionTable sessionTable = allTables.webioTable.sessionTable;

			IDVal tidOpen = request.tidStack;

			request.iSession = sessionTable.IFindSession(request.qwSession, request.hSession, request.timeOpen);

			// The session has the best, most directly useful call stack.  More so than the request.
			// However, in some cases (OfficeWebServiceApi) the session will be reused for many diverse requests.
			// And sometimes the session was created before the start of this trace.
			// In either case we need to use the call stack of the request itself.

			if (request.iSession != 0)
			{
				Session session = sessionTable.SessionFromI(request.iSession);

				// Use the stack of the Session if this is the first Request, or if the URL paths match (except for the filename).

				if (session.iRequestFirst == 0 || FSameServer(request.strURL, RequestFromI(session.iRequestFirst).strURL))
				{
					session.iRequestFirst = (uint)this.Count + 1; // 1-based index of this new request, added below

					// Use the callstack of the creation of the session.

					if (session.stack != null)
					{
						request.stack = session.stack;

						// The thread must match the callstack.
						request.tidStack = session.tidStack;
					}

					if (session.xlink.HasValue)
						request.xlink.Copy(session.xlink);
				}
			}

			if (!request.xlink.HasValue)
			{
				// Use the callstack of the creation of this request.
				// Perhaps the session was created before the trace started?

				request.xlink.ReGetLink(tidOpen, in request.timeOpen, in allTables.threadTable);
			}

			this.Add(request);
		} // AddRequest


		public Request CloseRequest(QWord qwRequest, QWord qwHandle, IDVal pid, in TimestampUI timeStamp)
		{
			Request request = FindOpenRequestByHReq(qwHandle, pid);
			AssertImportant(request == FindRequest(qwRequest, pid, timeStamp));

			if (request == null) return null;

			AssertCritical(request.pid == pid);
			AssertImportant(request.FOpen);
			AssertImportant(timeStamp.Between(request.timeOpen, request.timeClose));
			request.timeClose = timeStamp;

			if (request.qwRequest == 0)
				request.qwRequest = qwRequest; // for completeness

			if (request.qwHandle == 0)
				request.qwHandle = qwHandle; // for completeness

			AssertImportant(request.qwRequest == qwRequest);
			AssertImportant(request.qwHandle == qwHandle);

			if (request.strURL == null)
				request.strURL = strNA;

			if (request.strServer == null)
				request.strServer = strNA;

			if (request.strMethod == null)
				request.strMethod = strNA;

			return request;
		} // CloseRequest
	} // RequestTable
} // NetBlameCustomDataSource.WebIO

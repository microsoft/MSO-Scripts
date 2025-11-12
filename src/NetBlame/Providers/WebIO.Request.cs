// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;

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
			this.tidStack = RequestTable.tidUnknown;
			this.strServer = string.Empty;
			this.strMethod = string.Empty;
		}

		public bool FOpen => this.timeClose.HasMaxValue();

		public bool FShared { get; set; }

		public bool HasCurrentConnection =>
				this.qwConnectionCur != 0 &&
				this.qwConnectionCur == this.rgConnection?[^1].qwConnection;

		public bool HasCurrentOpenConnection =>
				this.HasCurrentConnection && this.FOpen;

/*
	The Connection value from RequestWaitingForConnection
	is sometimes invalid (never occuring elsewhere in the trace).
	It appears to be the address of a different heap object.
	Empirically, the invalidity appears to be unique to a certain pattern.
	It is these 5 events, all on the same thread, always occuring together:
		WIO.RequestCreate               T1 Request=R# Method=M$
		WIO.RequestWaitingForConnection T1 Request=R# Connection=C1#
		WIO.RequestHeader_Start         T1 Request=R# Headers=H$
		WIO.ConnectionSocketSend_Start  T1 Connection=C2#
		WIO.ConnectionSocketSend_End    T1 Connection=C2#
		...
		AND !H$.StartsWith(M$) OR exception thrown.
	Also, the WIO.RequestHeader_Start event often has fields that throw when parsed. (!?)

	Even so, C1#==C2# sometimes, unless we find the inconsistencies in the header event.
	It appears that when we see those inconsistencies in the above pattern, then C1# != C2#.
*/
		public enum EValidity : int
		{
			Dubious = -1,
			Unknown = 0, // default
			Confirmed = 1
		}

		public EValidity Validity { get; private set; }

		[Conditional("DEBUG")]
		public void ConfirmValidity() => this.Validity = EValidity.Confirmed;

		[Conditional("DEBUG")]
		public void HandleValidity(in IGenericEvent evt, bool fTestHeader)
		{
			AssertImportant(this.strMethod != null);
			AssertCritical(evt.Id == (int)WebIOTable.WIO.RequestHeader_Send);
			// Test the GuessRequest mechanism for the RequestHeader_Send event.
			AssertInfo(WebIOTable.GuessRequest(in evt, this.qwConnectionCur) == this.qwRequest);

			if (this.Validity != EValidity.Unknown)
			{
				AssertImportant(this.Validity != EValidity.Dubious); // how?
				return;
			}

			if (fTestHeader)
			{
				string strHeader = evt.GetString("Headers");
				if (!strHeader.StartsWith(this.strMethod))
					this.Validity = EValidity.Dubious; // rare!
			}
			else
			{
				this.Validity = EValidity.Dubious;
			}
		}

		// Implement IGraphableEntry
#if DEBUG || AUX_TABLES
		public IDVal Pid => this.pid;
		public IDVal TidOpen => this.tidStack;
#else
		public IDVal Pid => pidUnknown;
		public IDVal TidOpen => tidUnknown;
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

		public const IDVal tidUnknown = -1;


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
		public Connection FindConnectionByHandle(in IGenericEvent evt, out Request req)
		{
			req = null;
			Connection cxn = null;
			IDVal pid = evt.ProcessId;

			// Here we test Connection values AGAINST the one provided by RequestWaitingForConnection.
			AssertCritical(evt.Id != (int)WebIOTable.WIO.RequestWaitingForConnection_Stop);
			AssertCritical(evt.Id < (int)WebIOTable.WIO.ConnectionSocketSend_Start || evt.Id > (int)WebIOTable.WIO.ConnectionSocketReceive_Stop);

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

					// The Request's Connection ID matches the event's (qwCxn).
					req.ConfirmValidity();
				}
				else
				{
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

					// Find a Closed request, sometimes needed for ConnectionSocketClose.

					if (evt.Id == (int)WebIOTable.WIO.ConnectionSocketClose)
						reqT = this.FindLast(r => r.HasCurrentConnection && r.qwConnectionCur == qwCxn && r.pid == pid);
				}

				if (reqT != null)
				{
					req = reqT;
					cxn = reqT.rgConnection[^1]; // current connection

					// The Request's Connection ID matches the event's (qwCxn).
					req.ConfirmValidity();
#if DEBUG
					AssertImportant(reqT.qwConnectionCur == qwCxn);
					AssertImportant(FImplies(!WebIOTable.IsSocketReceiveStopClose(in evt), !cxn.fOutdated));
#endif // DEBUG
				}
			}

			cxn?.TestSocket(in evt, qwCxn);

			return cxn;
		} // FindConnectionByHandle


		/*
			Create a new Connection and add it to this Request.
			Base it on an existing Connection from another Request, if possible.
		*/
		public Connection SetRequestConnection(in IGenericEvent evt, Request req, QWord qwCxn, int iReq)
		{
			AssertCritical(req.qwConnectionCur == qwCxn);
			AssertCritical(req.Validity == Request.EValidity.Confirmed);

			// We didn't get a ConnectionSocketConnect event pair, apparently.
			// We must be reusing a Connection/Socket previously opened (PATTERN 4B).
			// Or this event occurs very near the beginning of the trace.

			QWord qwSocket = evt.TryGetUInt64("SocketHandle");
			AssertImportant(qwSocket != 0);
			AssertImportant(qwSocket != QWord.MaxValue);

			if (iReq < 0)
				iReq = this.Count - 1;

			// Find the shareable Connection and Socket with the given ID/handle values.

			Connection cxn = null;
			while (cxn == null && --iReq >= 0)
			{
				Request reqT = this[iReq];

				if (reqT.pid != evt.ProcessId)
					continue;

				cxn = reqT.rgConnection?.FindLast(
					c => c.qwConnection == qwCxn && c.socket.qwSocket == qwSocket && !c.socket.FClosed
					);
			}

			Socket socket;

			if (cxn != null)
			{
				// This Connection and its Socket will get shared with this Request.
				// Both Requests may be open at the same time but Send/Receive operations cannot overlap, respectively.
#if DEBUG
				bool fSend = ((evt.Id + 1) & -2) == (int)WebIOTable.WIO.ConnectionSocketSend_Stop; // ConnectionSocketSent_Start/Stop
				AssertImportant((fSend ? cxn.ctxSend : cxn.ctxRecv) == 0); // no overlapping, cross-Request conflicts!

				cxn.socket.AddRef();
				cxn.fOutdated = true;
#endif // DEBUG
				req.FShared = true;
				socket = cxn.socket;
			}
			else
			{
				socket = new Socket(in evt);
			}

			Connection cxnNew = new Connection(qwCxn, socket);

			cxnNew.fDuplicate = cxn != null;
#if DEBUG
			cxnNew.fTransferred = true;
#endif // DEBUG
			AssertImportant(!cxnNew.socket.FClosed);
			AssertImportant(cxnNew.socket.timeStart.HasValue());
			cxnNew.socket.timeStop.SetMaxValue(); // no longer stopped

			if (req.rgConnection == null)
				req.rgConnection = new List<Connection>(1);
			else
				AssertCritical(!req.HasCurrentConnection); // No duplicates!

			req.qwConnectionCur = qwCxn;
			req.rgConnection.Add(cxnNew);

			AssertCritical(req.HasCurrentConnection);
			req.ConfirmValidity(); // valid qwConnectionCur

			cxnNew.TestSocket(in evt, qwCxn);
			return cxnNew;
		} // SetRequestConnection


		/*
			Return the Connection which matches:
			- Request Handle (hReq) - param hReq not trusted
			- Connection Id (qwCxn) - Request.qwConnectionCur not always trusted
			- Context (ctx) - ctx==0 if Start, ctx!=0 if Stop
			- Send/Recv (fSend) - true = Send, false = Receive
			A Stop event can arrive after the Request has closed.

			Find the most recent Request and its current Connection (if it exists):
			- Matching Request handle, where hReq is not always trusted, particularly for Receive.
			- Matching current Connection ID, where Request.qwConnectionCur is not always trusted.
			- Matching Context: Start context uses 0; Stop context = Start context 

			It mat create a new Connection/Socket via SetRequestConnection.
		*/
		public Connection GetConnection(in IGenericEvent evt)
		{
			AssertCritical(evt.Id >= (int)WebIOTable.WIO.ConnectionSocketSend_Start && evt.Id <= (int)WebIOTable.WIO.ConnectionSocketReceive_Stop);

			bool fSend = ((evt.Id + 1) & -2) == (int)WebIOTable.WIO.ConnectionSocketSend_Stop; // ConnectionSocketSent_Start/Stop
			bool fStart = (evt.Id & 1) != 0;

			QWord ctx = fStart ? 0 : evt.GetAddrValue("Context");

			QWord qwCxn = evt.GetAddrValue("Connection");
			AssertCritical(qwCxn != 0);

			QWord hReq = WebIOTable.GetHReq(in evt);
			AssertImportant(hReq != 0);

			for (int iReq = this.Count-1; iReq >= 0; --iReq)
			{
				Request req = this[iReq];

				if (req.pid != evt.ProcessId)
					continue;

				if (req.qwConnectionCur == 0)
					continue;

				// Check for a current Connection and match the Context value.

				Connection cxn = null;
				if (req.HasCurrentConnection)
				{
					cxn = req.rgConnection[^1];

					AssertCritical(cxn != null);

					// The Connection's Context value MUST match, even 0==0.
					QWord ctxCxn = fSend ? cxn.ctxSend : cxn.ctxRecv;
					if (ctx != ctxCxn)
					{
						if (fStart || ctxCxn != 0)
							continue;

						if (qwCxn != req.qwConnectionCur)
							continue;

						// There's another, earlier Request with the same Connection?
						if (cxn.fDuplicate)
							continue;

						// There is no Start context for this Stop event.
						// This is probably near the beginning of the trace.
						AssertImportant(iReq == 0); // iReq == small number
					}

					// If this is a Stop event then we've found the match and are done.
					if (!fStart)
					{
						AssertImportant(qwCxn == req.qwConnectionCur);
						cxn.TestSocket(in evt, qwCxn);
						return cxn;
					}
				}
				else if (!fStart)
				{
					// Stop event with no current Connection OR Send_Stop event with closed Connection.
					continue;
				}
				else if (!req.FOpen)
				{
					// Closed Request with no current Connection!?
					continue;
				}

				// Here we have only ConnectionSocketSend/Receive_Start events.
				// The Request is not closed.
				// It may have a current Connection, in which case: cxn != null

				AssertCritical(fStart); // only Start events

				if (req.qwConnectionCur == qwCxn)
				{
					AssertImportant(req.Validity != Request.EValidity.Dubious);
					req.ConfirmValidity();

					// The critical parameters match!
					if (cxn != null)
					{
						cxn.TestSocket(in evt, qwCxn);
						return cxn;
					}

					// Here we got a RequestWaitingForConnection event (with valid Connection ID),
					// but no Connection/Socket creation events.
					// That means it's a shared Connection.
				}
				else if (cxn == null)
				{
					// Here we have no current Connection and not a matching Connection ID.
					// It might not be a match, but the Connection ID is not always trustable.
					// This could be the famous case 4B! (See: WebIO.Socket.cs)

					if (hReq != req.qwHandle)
						continue;

					AssertImportant(req.Validity == Request.EValidity.Dubious); // part of condition?
					req.qwConnectionCur = qwCxn;
					req.ConfirmValidity();
				}
				else // Has current Connection, but not a matching Connection ID.
				{
					AssertImportant(req.Validity != Request.EValidity.Dubious);
					continue;
				}

				return SetRequestConnection(in evt, req, qwCxn, iReq);
			} // for iReq

			return null;
		} // GetConnection


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
					if ((fSend ? cxn.tidSend : cxn.tidRecv) == tid && cxn.tidTCB == tidUnknown && cxn.socket.tidConnect == tid && cxn.socket.iTCB == 0 && !cxn.socket.FClosed)
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
			Find the unique Socket created on the given thread during the given time interval.
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


		/*
			Search all the Requests to find the most recent Connection which matches the given parameters.
		*/
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
			req = this.FindLast(r => r.pid == pid && r.FOpen && r.rgConnection?.FindLast(c => c.MatchTCB(iTCB, iDNS, iAddr, tid)) != null);
			AssertImportant(req == null);
#endif // DEBUG

			return null;
		} // CorrelateByAddress


		/*
			Handle the event: RequestHeader_Recv
			Extrace the Header string and add it to the corresponding Connection.
		*/
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
		} // AddHeader


		/*
			Handle the event: ConnectionSocketClose
			Find the corresponding Connection(s) and close the attached Socket.
		*/
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
				cxn.tidSend = tidUnknown;
				cxn.tidRecv = tidUnknown;
				cxn.tidTCB = tidUnknown;

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
				cxn.tidTCB = tidUnknown;
				cxn.tidSend = tidUnknown;
				cxn.tidRecv = tidUnknown;

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


		/*
			Handle the event: RequestCreate
			Create a new Request, attach it to a Session, and apply a Stackwalk.
		*/
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


		/*
			Handle the event: RequestClose_Stop
			Find the corresponding Request, tidy it up, and mark it as closed.
		*/
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

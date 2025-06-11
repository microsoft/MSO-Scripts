// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;

using Microsoft.Windows.EventTracing.Symbols;

using NetBlameCustomDataSource.Link;

using static NetBlameCustomDataSource.Util;

using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using IDVal = System.Int32; // type of Event.pid/tid / ideally: System.UInt32
using QWord = System.UInt64;


namespace NetBlameCustomDataSource.WebIO
{
	public class Session
	{
		public readonly QWord qwSession;
#if DEBUG
		public readonly QWord qwHandle;
#endif // DEBUG

		public readonly TimestampUI timeOpen;
		public TimestampUI timeClose;

		public uint iRequestFirst;

		public IDVal pid;
		public IDVal tidStack;
		public IStackSnapshot stack;
		public XLink xlink;

		public Session(QWord qwSession, QWord qwHandle, in TimestampUI timeStamp)
		{
			this.qwSession = qwSession;
#if DEBUG
			this.qwHandle = qwHandle;
#endif // DEBUG

			this.timeOpen = timeStamp;
			this.timeClose.SetMaxValue();
		}
	}

	public class SessionTable : List<Session>
	{
		// TODO: smarter capacity?
		public SessionTable(int capacity) : base(capacity) { }

		// 1-based index -> session or null
		public Session SessionFromI(uint iSession) => (iSession != 0) ? this[(int)iSession - 1] : null;

		public uint IFindSession(QWord qwSession, QWord qwHandle, in TimestampUI timeStamp)
		{
			for (int iSession = this.Count - 1; iSession >= 0; --iSession)
			{
				Session session = this[iSession];
				if (session.qwSession == qwSession && timeStamp.Between(session.timeOpen, session.timeClose))
				{
#if DEBUG
					AssertImportant(FImplies(qwHandle != 0, qwHandle == session.qwHandle));
#endif // DEBUG
					return (uint)iSession+1;
				}
			}
			return 0;
		}

		public void AddSession(QWord qwSession, QWord qwHandle, IDVal pidT, IDVal tidT, in TimestampUI timeStampUI, in IStackSnapshot stackT, in Thread.ThreadTable threadTable)
		{
			AssertImportant(IFindSession(qwSession, qwHandle, in timeStampUI) == 0);

			Session session = new Session(qwSession, qwHandle, in timeStampUI)
			{
				pid = pidT,
				tidStack = tidT,
				stack = stackT
			};
			session.xlink.GetLink(tidT, timeStampUI, in threadTable);
			this.Add(session);
		}

		public void CloseSession(QWord qwSession, QWord qwHandle, in TimestampUI timeStamp)
		{
			uint iSession = IFindSession(qwSession, qwHandle, timeStamp);
			if (iSession != 0)
			{
				Session session = SessionFromI(iSession);
				AssertImportant(timeStamp.Between(session.timeOpen, session.timeClose));
				AssertImportant(session.timeClose.HasMaxValue());
				session.timeClose = timeStamp;
			}
		}
	} // SessionTable
} // namespace NetBlameCustomDataSource.WebIO
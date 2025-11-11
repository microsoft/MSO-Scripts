// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;

using Microsoft.Windows.EventTracing.Events; // IGenericEvent
using Microsoft.Windows.EventTracing.Symbols; // IStackSnapshot

using TimestampETW = Microsoft.Windows.EventTracing.TraceTimestamp;
using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using AddressETW = Microsoft.Windows.EventTracing.Address;
using AddrVal = System.UInt64;

namespace NetBlameCustomDataSource
{
	[System.Diagnostics.DebuggerStepThrough()]
	public static class Extensions
	{
		public static bool IsNA(this string str) => System.String.IsNullOrWhiteSpace(str) || str == Util.strNA; // "N/A"

		public static bool HasValue(this in TimestampUI time) => (time.ToNanoseconds != 0); // zero-initialized struct

		public static bool HasMaxValue(this in TimestampUI time) => (time == TimestampUI.MaxValue);

		public static void SetMaxValue(this ref TimestampUI time) { time = TimestampUI.MaxValue; }

		public static bool Between(this in TimestampUI tRef, in TimestampUI tFirst, in TimestampUI tLast) => tRef >= tFirst && tRef <= tLast;

		public static TimestampUI ToGraphable(this in TimestampETW time) => new TimestampUI(time.RelativeTimestamp.Nanoseconds);

		public static TimestampETW Zero(this in TimestampETW time) => new TimestampETW(time.Context, time.Context.ReferenceValue);

		public static AddrVal ToValue(this in AddressETW addr) => (AddrVal)addr.Value;

		public static bool Empty(this IPAddress ipAddr) => ipAddr == null || ipAddr.Equals(IPAddress.Any/*0.0.0.0*/) || ipAddr.AddressFamily == System.Net.Sockets.AddressFamily.Unspecified;

		public static bool Empty(this IPEndPoint ipep) => ipep == null || (ipep.Port == 0 && ipep.Address.Empty());

		public static string ToGraphable(this IPAddress ipAddr) => ipAddr?.ToString() ?? String.Empty;

		public static string ToGraphable(this IPEndPoint ipEndPoint) => !ipEndPoint.Empty() ? ipEndPoint.ToString() : String.Empty;
		public static string AddrGraphable(this IPEndPoint ipEndPoint) => !ipEndPoint.Empty() ? ipEndPoint.Address.ToString() : String.Empty;
		public static uint PortGraphable(this IPEndPoint ipEndPoint) => (uint?)ipEndPoint?.Port ?? 0;

		public static bool Empty(this SocketAddress socket) => (socket == null || socket[0]/*family*/ == 0);

		// SocketAddress.Equals may give false negatives when the socket contains random padding bytes!
		public static bool SafeEquals(this SocketAddress socket, SocketAddress comparand)
		{
			if (socket == null || comparand == null)
				return false;

			if (socket.Family != comparand.Family)
				return false;

			if (socket.Port() != comparand.Port())
				return false;

			if (socket.Equals(comparand))
				return true;

			// Expensive!
			return Util.NewEndPoint(socket).Equals(Util.NewEndPoint(comparand));
		}

		public static bool IsAddrZero(this SocketAddress socket)
		{
#if DEBUG
			if (socket.Empty()) return true;

			switch (socket.Family)
			{
			case System.Net.Sockets.AddressFamily.InterNetwork: // IPv4
				return Util.NewEndPoint(socket).Address.Equals(IPAddress.Any);
			case System.Net.Sockets.AddressFamily.InterNetworkV6: // IPv6
				return Util.NewEndPoint(socket).Address.Equals(IPAddress.IPv6Any);
			}
			return false;
#else
			throw new NotImplementedException("IsAddrZero: Not intended for release.");
#endif // DEBUG
		}

		public static ushort Port(this SocketAddress socket) => (ushort)((socket[2] << 8) + socket[3]);

		public static UInt16 GetUInt16(this IGenericEvent evt, string strField) => evt.Fields[strField].AsUInt16;

		public static UInt32 GetUInt32(this IGenericEvent evt, string strField) => evt.Fields[strField].AsUInt32;

		public static UInt64 GetUInt64(this IGenericEvent evt, string strField) => evt.Fields[strField].AsUInt64;

		public static UInt64 TryGetUInt64(this IGenericEvent evt, string strField) => evt.Fields.TryGetValue(strField, out IGenericEventField field) ? field.AsUInt64 : 0;

		public static IReadOnlyList<byte> GetBinary(this IGenericEvent evt, string strField) => evt.Fields[strField].AsBinary;

		public static UInt64 GetAddrValue(this IGenericEvent evt, string strField) => evt.Fields[strField].AsAddress.ToValue();

		public static UInt64 TryGetAddrValue(this IGenericEvent evt, string strField) => evt.Fields.TryGetValue(strField, out IGenericEventField field) ? field.AsAddress.ToValue() : 0;

		public static string GetString(this IGenericEvent evt, string strField) => evt.Fields[strField].AsString;

		public static string GetAddressString(this IGenericEvent evt) => (evt.GetUInt32("AddressLength") > 1) ? evt.GetString("Address") : null; // AddressLength includes 0

		public static SocketAddress GetSocketAddress(this IGenericEvent evt, string strField = "Address") => evt.Fields[strField].AsSocketAddress;

		public static SocketAddress GetLocalAddress(this IGenericEvent evt) => (evt.GetUInt32("LocalAddressLength") != 0) ? evt.GetSocketAddress("LocalAddress") : null;

		public static SocketAddress GetRemoteAddress(this IGenericEvent evt) => (evt.GetUInt32("RemoteAddressLength") != 0) ? evt.GetSocketAddress("RemoteAddress") : null;

		// Simple hash of all addresses in the stack. Returns 0 if none.
		public static int Hash(this IStackSnapshot ss)
		{
			if (ss?.Frames == null) return 0;

			int hash = 0;
			foreach (var frame in ss.Frames)
				hash ^= frame.Address.GetHashCode();

			return hash;
		}
	}

} // NetBlameCustomDataSource.Events

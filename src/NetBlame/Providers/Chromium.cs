// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

// cf. https://github.com/microsoft/MSO-Scripts/issues/50
// cf. https://source.chromium.org/chromium/chromium/src/+/main:net/docs/crash-course-in-net-internals.md
// cf. https://source.chromium.org/chromium/chromium/src/+/main:net/docs/life-of-a-url-request.md
// cf. chrome://net-export  OR  edge://net-export  AND  https://chromium.googlesource.com/catapult/+/HEAD/netlog_viewer/

// This code is intended to be Assert-Clean when trace collection begins before launching any instance of Chrome or Edge.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Windows.EventTracing.Events;
using Microsoft.Windows.EventTracing.Symbols;

using TimestampETW = Microsoft.Windows.EventTracing.TraceTimestamp;
using TimestampUI = Microsoft.Performance.SDK.Timestamp;

using NetBlameCustomDataSource.Link;
using static NetBlameCustomDataSource.Util; // Assert
using static NetBlameCustomDataSource.Chromium.JSON_Util;

using IDVal = System.Int32; // ProcessId, ThreadId
using QWord = System.UInt64;

#if DEBUG
using UIDVal = System.UInt64;
#else
using UIDVal = System.UInt32;
#endif

namespace NetBlameCustomDataSource.Chromium
{
	static class JSON_Util
	{
		public const int jsonIntDefault = 0;

		// Convert a string kind to string, else "".
		public static string MyGetString(in this JsonElement jsonE) => (jsonE.ValueKind == JsonValueKind.String) ? jsonE.GetString() : string.Empty;

		// Convert a number kind to int, else jsonIntDefault=0 (or the given default).
		public static int MyGetNumber(in this JsonElement jsonE, int iDefault = jsonIntDefault) => (jsonE.ValueKind == JsonValueKind.Number) ? jsonE.GetInt32() : iDefault;
		public static uint MyGetUNumber(in this JsonElement jsonE, uint iDefault = (uint)jsonIntDefault) => (jsonE.ValueKind == JsonValueKind.Number) ? jsonE.GetUInt32() : iDefault;
		public static decimal MyGetDecimal(in this JsonElement jsonE, decimal iDefault = (decimal)jsonIntDefault) => (jsonE.ValueKind == JsonValueKind.Number) ? jsonE.GetDecimal() : iDefault;

		// Convert an array kind (of String) to string[]
		public static string[] MyGetStringArray(in this JsonElement jsonE)
		{
			if (jsonE.ValueKind != JsonValueKind.Array || jsonE.GetArrayLength() == 0 || jsonE[0].ValueKind != JsonValueKind.String)
				return Array.Empty<string>();

			string[] rgstr = new string[jsonE.GetArrayLength()];
			for (int isz = 0; isz < jsonE.GetArrayLength(); ++isz)
				rgstr[isz] = jsonE[isz].MyGetString();

			return rgstr;
		}

		// Convert an array kind (of Object) to string[] by extracting the given property from each object: [{"prop1":"string1", "prop2":"string2"}, {"prop1":"string1", "prop2":"string2"}]
		public static string[] MyGetStringArray(in this JsonElement jsonE, string strProp)
		{
			if (jsonE.ValueKind != JsonValueKind.Array || jsonE.GetArrayLength() == 0 || jsonE[0].ValueKind != JsonValueKind.Object)
				return Array.Empty<string>();

			string[] rgstr = new string[jsonE.GetArrayLength()];
			for (int isz = 0; isz < jsonE.GetArrayLength(); ++isz)
				rgstr[isz] = jsonE[isz].TryGetProperty(strProp, out JsonElement jsonT) ? jsonT.MyGetString() : string.Empty;

			return rgstr;
		}

		// Convert an array kind (of String) of the form [":name1: value1", "name2: value2", ...] to a correspondingly ordered array of selected strings { "value1", "value2", ... }
		public static string[] MyGetStringArray(in this JsonElement jsonE, string[] rgstrProp)
		{
			if (jsonE.ValueKind != JsonValueKind.Array || jsonE.GetArrayLength() == 0 || jsonE[0].ValueKind != JsonValueKind.String)
				return Array.Empty<string>();

			string[] rgstr = new string[rgstrProp.Length];

			foreach (JsonElement je in jsonE.EnumerateArray())
			{
				string strKeyVal = je.MyGetString();

				// There should be exactly one colon ':' (property name terminator) before the first space, but accept a leading colon like: ":method:"

				int iColon = strKeyVal.IndexOf(':', 1);
				AssertImportant(iColon > 0);
				if (iColon <= 0) continue;

				int iSpace = strKeyVal.IndexOf(' ', 0, iColon);
				AssertImportant(iSpace < 0); // no ' ' found before the first ':'
				if (iSpace >= 0) continue;

				AssertImportant(iColon+1 < strKeyVal.Length && char.IsWhiteSpace(strKeyVal[iColon+1]));

				// Creating a span is much more efficient than allocating a temporary substring.
				ReadOnlySpan<char> spanKeyVal = strKeyVal.AsSpan(0, iColon);
				int istr = rgstrProp.IndexOf(spanKeyVal);
				if (istr >= 0)
				{
					// Skip the colon and the intervening space(s).
					for (++iColon; iColon < strKeyVal.Length; ++iColon)
						if (!char.IsWhiteSpace(strKeyVal[iColon])) break;

					rgstr[istr] = strKeyVal[iColon..];
				}
			}

			return rgstr;
		}


		static bool FFailedDB() { AssertImportant(false); return false; }

		// Convert a True or False kind boolean, else false.
		public static bool MyGetBool(in this JsonElement jsonE) => (jsonE.ValueKind == JsonValueKind.True) ? true : (jsonE.ValueKind == JsonValueKind.False ? false : FFailedDB());

		// Convert a string kind (or a number kind) to int, else: jsonIntDefault
		public static QWord MyGetStringAsNumber(in this JsonElement jsonE) => (jsonE.ValueKind == JsonValueKind.String) ? (QWord.TryParse(jsonE.GetString(), out QWord value) ? value : jsonIntDefault) : (QWord)jsonE.MyGetNumber();

		// Return true if JSON element kind can be use with: MyGetString, MyGetNumber, or MyGetStringAsNumber
		private static bool IsMyReturnKind(in this JsonElement jsonE) => (jsonE.ValueKind >= JsonValueKind.Array && jsonE.ValueKind < JsonValueKind.Null);

		/*
			Parse a simple JSON string.
			Extract a list (array) of properties where at least is one is nested: "/Nest1/Nest2.../Property"
			Return an array of JsonElement, OR null on failure. An array element may be empty: default(JsonElement)
			Caller resolves using: MyGetString, MyGetNumber, MyGetStringAsNumber, ...
		*/
		public static JsonElement[] ParseSimpleJsonDeepString(string json, string[] rgstrProperty)
		{
			const int depthMax = 4; // max nesting depth

			int cTarget = rgstrProperty.Length;
			JsonElement[] results = new JsonElement[cTarget];
			string[][] targets = new string[cTarget][];

			// Normalize property strings into segment arrays
			//	"url" -> {"url"}
			//	"/source_dependency/id" -> {"source_dependency", "id"}
			for (int iTarget = 0; iTarget < cTarget; iTarget++)
			{
				targets[iTarget] = rgstrProperty[iTarget].Split('/', StringSplitOptions.RemoveEmptyEntries);
				AssertCritical(targets[iTarget].Length <= depthMax);
			}

			// Rent a buffer to avoid allocating a new byte[] on the GC heap for every ETW event.
			int cbRent = Encoding.UTF8.GetByteCount(json);
			byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(cbRent);

			try
			{
				int cbWritten = Encoding.UTF8.GetBytes(json, 0, json.Length, buffer, 0);
				AssertImportant(cbWritten == cbRent);

				var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(buffer, 0, cbWritten));

				int cFound = 0;
				int depthCur = 0;
				string propertyCur = null;
				string[] pathCur = new string[depthMax];

				while (reader.Read())
				{
					switch (reader.TokenType)
					{
					case JsonTokenType.StartObject:
						if (propertyCur != null)
						{
							if (depthCur < depthMax)
								pathCur[depthCur] = propertyCur;

							depthCur++;
							propertyCur = null;
						}
						break;

					case JsonTokenType.EndObject:
						if (depthCur > 0)
							depthCur--;

						break;

					case JsonTokenType.PropertyName:
						bool fPrefix = false;
						int iMatch = -1;

						// Evaluate if the current property matches any of our targets
						for (int iTarget = 0; iTarget < cTarget; iTarget++)
						{
							if (results[iTarget].ValueKind != JsonValueKind.Undefined)
								continue; // already found

							string[] pathTarget = targets[iTarget];
							bool fPathMatch = true;

							// Ensure we are in the correct parent path before evaluating the property name
							if (depthCur > 0)
							{
								if (pathTarget.Length > depthCur)
								{
									for (int j = 0; j < depthCur; j++)
									{
										if (pathCur[j] != pathTarget[j])
										{
											fPathMatch = false;
											break;
										}
									}
								}
								else
								{
									fPathMatch = false;
								}
							}

							if (fPathMatch)
							{
								// ValueTextEquals avoids allocating a string!
								if (reader.ValueTextEquals(pathTarget[depthCur]))
								{
									if (pathTarget.Length == depthCur + 1)
									{
										iMatch = iTarget;
										break; // We found the exact property.
									}
									else
									{
										fPrefix = true; // We are on the right path to a nested property.
									}
								}
							}
						}

						if (iMatch >= 0)
						{
							if (reader.Read())
							{
								// Parse ONLY this specific token/subtree into a JsonDocument.
								using (var doc = JsonDocument.ParseValue(ref reader))
								{
									if (doc.RootElement.IsMyReturnKind())
									{
										results[iMatch] = doc.RootElement.Clone();

										// Early exit once all properties are found.
										if (++cFound == cTarget)
											return results;
									}
								}
							}
						}
						else if (fPrefix)
						{
							// We must record this property name because we will step into its object.
							propertyCur = reader.GetString();
						}
						else
						{
							// Neither a match nor a prefix. Skip the entire subtree.
							reader.Skip();
						}
						break;
					} // switch reader.TokenType
				} // while reader.Read()

				return results;
			}
			catch
			{
				AssertCritical(false);
				return null;
			}
			finally
			{
				System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
			}
		} // ParseSimpleJsonDeepString

		/*
			Efficiently parse a simple JSON string.
			Extract a list (array) of properties, none of which are nested.
			Return an array of JsonElement, OR null on failure. An array element may be empty: default(JsonElement)
			Caller resolves using: MyGetString, MyGetNumber, MyGetStringAsNumber, ...
		*/
		public static JsonElement[] ParseSimpleJsonShallowString(string json, string[] rgstrProperty)
		{
			int cTarget = rgstrProperty.Length;
			JsonElement[] results = new JsonElement[cTarget];

			// Rent a buffer to avoid allocating a new byte[] on the GC heap for every ETW event.
			int cbRent = Encoding.UTF8.GetByteCount(json);
			byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(cbRent);

			try
			{
				int cbWritten = Encoding.UTF8.GetBytes(json, 0, json.Length, buffer, 0);
				AssertImportant(cbWritten == cbRent);

				var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(buffer, 0, cbWritten));

				bool fStarted = false;
				int cFound = 0;

				while (reader.Read())
				{
					switch (reader.TokenType)
					{
					case JsonTokenType.StartObject:
						fStarted = true;
						break;

					case JsonTokenType.EndObject:
						fStarted = false;
						break;

					case JsonTokenType.PropertyName:
						if (!fStarted)
						{
							// exceptional
							reader.Skip();
							break;
						}

						int iMatch = -1;

						// Evaluate if the current property matches any of our targets
						for (int iTarget = 0; iTarget < cTarget; iTarget++)
						{
							if (results[iTarget].ValueKind != JsonValueKind.Undefined)
								continue; // already found

							// ValueTextEquals avoids allocating a string!
							if (reader.ValueTextEquals(rgstrProperty[iTarget]))
							{
								iMatch = iTarget;
								break; // We found the exact property.
							}
						}

						if (iMatch < 0)
						{
							reader.Skip();
							break;
						}

						if (reader.Read())
						{
							// Parse ONLY this specific token/subtree into a JsonDocument.
							using (var doc = JsonDocument.ParseValue(ref reader))
							{
								if (doc.RootElement.IsMyReturnKind())
								{
									results[iMatch] = doc.RootElement.Clone();

									// Early exit once all properties are found.
									if (++cFound == cTarget)
										return results;
								}
							}
						}
						break;
					} // switch reader.TokenType
				} // while reader.Read()

				return results;
			}
			catch
			{
				AssertCritical(false);
				return null;
			}
			finally
			{
				System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
			}
		} // ParseSimpleJsonShallowString

		/*
			Parse the JSON string and return an array of JsonElement corresponding to the array of property names.
			If any of the property names begins with a forward slash (eg. "/source_dependency/id")
			then do a deep parse for nested properties (eg. "source_dependency":{"id":123} ).
			This may return null on failure. An array element may be empty: default(JsonElement)
			Caller resolves using: MyGetString, MyGetNumber, MyGetBool, MyGetStringAsNumber, ...
		*/
		public static JsonElement[] ParseSimpleJsonString(string json, string[] rgstrProperty)
		{
			AssertCritical(rgstrProperty?.Length > 0);

			if (string.IsNullOrWhiteSpace(json))
				return null;

			// Test for any nested properties: "/Nest1/Nest2.../Property"
			foreach (string strProp in rgstrProperty)
			{
				if (strProp[0] == '/')
					return ParseSimpleJsonDeepString(json, rgstrProperty);
			}

			return ParseSimpleJsonShallowString(json, rgstrProperty);
		}
	} // class JSON_Util


	public static class DNSInfo
	{
		public class ResolvedDNS
		{
			public string Domain; // "star-mini.c1q0r.facebook.com"
			public string Alias;  // "www.facebook.com" or null
			public string[] rgAddress; // never null, no missing or white-space elements, no port numbers
		}

		/*
			Parse the JSON string from the 'params' field where: 'source_type' == 'HOST_RESOLVER_IMPL_JOB'

			Simplified Input:
			{
			"results":
			 [
			  {
			  "domain_name":"ax-0001.ax-msedge.net",
			  "endpoints":
			   [
			    { "address":"150.171.27.10" },
			    { "address":"150.171.28.10" }
			   ],
			  "type":"data"
			  },
			  {
			  "alias_target":"c-bing-com.ax-0001.ax-msedge.net",
			  "domain_name":"c.bing.com",
			  "type":"alias"
			  },
			  {
			  "alias_target":"ax-0001.ax-msedge.net",
			  "domain_name":"c-bing-com.ax-0001.ax-msedge.net",
			  "type":"alias"
			  }
			 ],
			}

			Output:
				Domain = "ax-0001.ax-msedge.net"
				Alias =  "c.bing.com"
				rgAddress = { "150.171.27.10", "150.171.28.10" }
		*/
		public static ResolvedDNS ParseHostResolveDNS(string json)
		{
			JsonDocument jd;
			try
			{
				jd = JsonDocument.Parse(json);
			}
			catch
			{
				jd = null;
			}

			AssertCritical(jd != null);

			if (jd == null)
				return null;

			using (jd)
			{
				if (!jd.RootElement.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
					return null;

				bool[] rgAlias = new bool[results.GetArrayLength()];

				string[] rgEndPoints = null;

				string sDomain = null;
				int iRes = -1;
				foreach (var res in results.EnumerateArray())
				{
					++iRes;

					if (!res.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String)
						continue;

					switch (type.GetString())
					{
						case "data":
							break;

						case "alias":
							rgAlias[iRes] = true;
							continue;

					//	case "metadata":
					//	case "error":
						default:
							continue;
					}

					// Only one "data" element!
					AssertImportant(sDomain == null);

					if (!res.TryGetProperty("domain_name", out JsonElement domain_name) || type.ValueKind != JsonValueKind.String)
						continue;

					sDomain = domain_name.GetString();

					if (string.IsNullOrWhiteSpace(sDomain))
					{
						sDomain = null;
						continue;
					}

					if (!res.TryGetProperty("endpoints", out JsonElement endpoints) || endpoints.ValueKind != JsonValueKind.Array || endpoints.GetArrayLength() == 0)
						continue;

					int iEP = 0;
					rgEndPoints = new string[endpoints.GetArrayLength()];
					foreach (var endpoint in endpoints.EnumerateArray())
					{
						if (!endpoint.TryGetProperty("address", out JsonElement address) || address.ValueKind != JsonValueKind.String)
							continue;

						if (string.IsNullOrWhiteSpace(address.GetString()))
							continue;

						rgEndPoints[iEP++] = address.GetString();
					}

					// Continue the loop only to populate: rgAlias[iRes]
				} // foreach res

				if (sDomain == null)
					return null;

				if (rgEndPoints == null)
					return null;

				// Find and trim any trailing null strings. (None have ever been observed.)

				int iEmpty = Array.FindIndex(rgEndPoints, s => s == null);

				if (iEmpty == 0)
					return null;

				if (iEmpty > 0)
					rgEndPoints = rgEndPoints[..iEmpty];

				// Now scan through the "alias" elements, matching alias to domain, and ultimately arriving at a final domain. (Truncated N^2 for small N)

				string target = sDomain;
				bool fAdvance;
				do
				{
					fAdvance = false;
					iRes = -1;
					foreach (var res in results.EnumerateArray())
					{
						if (!rgAlias[++iRes]) continue;

						if (!res.TryGetProperty("alias_target", out JsonElement alias_target) || alias_target.ValueKind != JsonValueKind.String)
							continue;

						if (alias_target.GetString() != target) continue;

						rgAlias[iRes] = false;

						if (!res.TryGetProperty("domain_name", out JsonElement domain_name) || domain_name.ValueKind != JsonValueKind.String)
							continue;

						target = domain_name.GetString();
						fAdvance = true;
					}
				} while (fAdvance);

				if (target == sDomain)
					target = null;

				return new ResolvedDNS
				{
					Domain = sDomain,
					Alias = target,
					rgAddress = rgEndPoints
				};
			} // using (jd)
		} // ParseHostResolveDNS
	} // DNSInfo


	public enum Priority : byte
	{
		THROTTLED = 0,
		IDLE = 1,
		LOWEST = 2,
		LOW = 3,
		MEDIUM = 4,
		HIGHEST = 5,
		Unknown = 42
	}


	static class Util
	{
		public static QWord GetUID64(this IGenericEvent evt) => evt.GetUInt64("Id");
		// Return the low part of the "Id" field, which is stored as a 64-bit number.
		public static UIDVal GetUID(this IGenericEvent evt) => (UIDVal)evt.GetUID64();

		public static string GetParams(this IGenericEvent evt) => evt.GetString("params");

		static bool CheckPhase(this IGenericEvent evt, string strPhase) => evt.GetString("Phase").Equals(strPhase);
		public static bool IsBeginPhase(this IGenericEvent evt) => CheckPhase(evt, "Begin");
		public static bool IsEndPhase(this IGenericEvent evt) => CheckPhase(evt, "End");
		public static bool IsInstantPhase(this IGenericEvent evt) => CheckPhase(evt, "Instant");

		// Only when evt.IsBeginPhase()
		public static string GetSourceType(this IGenericEvent evt) => evt.GetString("source_type");
		public static bool CheckSourceType(this IGenericEvent evt, string strType) => evt.GetSourceType().Equals(strType);

		public static bool TestResolverSourceType(this IGenericEvent evt)
		{
			switch (evt.GetSourceType())
			{
			case "SSL_CONNECT_JOB":
			case "TRANSPORT_CONNECT_JOB":
			case "QUIC_SESSION_POOL_DIRECT_JOB":
			case "NETWORK_SERVICE_HOST_RESOLVER":
				return true;
#if DEBUG
			case "NETWORK_QUALITY_ESTIMATOR":
			case "PAC_FILE_DECIDER":
			case "CERT_VERIFIER_JOB":
			case "SOCKS_CONNECT_JOB":
			case "HTTP_PROXY_CONNECT_JOB":
				return false;
#endif // DEBUG
			default:
				AssertImportant(false); // add to approved cases?
				return false;
			}
		}

		public static Priority GetPriority(this string strPri) => Priority.TryParse(strPri, out Priority pri) ? pri : Priority.Unknown;

		public static int GetJSONNumber(string strJSON, string[] rgstrNumber)
		{
			JsonElement[] rgje = JSON_Util.ParseSimpleJsonString(strJSON, rgstrNumber);
			if (rgje == null) return jsonIntDefault;
			return rgje[0].MyGetNumber();
		}

		public static int GetJSONNumber(this IGenericEvent evt, string[] rgstrNumber) => GetJSONNumber(evt.GetParams(), rgstrNumber);

		// Returns { source_dependency: id } or jsonIntDefault
		public static int TryGetSourceId(this IGenericEvent evt)
		{
			return evt.GetJSONNumber(ChromiumTable.rgstrSourceId);
		}

		public static int GetSourceId(this IGenericEvent evt)
		{
			int srcdep = evt.TryGetSourceId();
			AssertCritical(srcdep != jsonIntDefault);
			return srcdep;
		}

		public static readonly string[] rgstrNetError = { "net_error" };
		public static readonly string[] rgstrQuicError = { "quic_error" };
		
		public static int GetNetError(this IGenericEvent evt) => evt.GetJSONNumber(rgstrNetError);
		public static int GetQuicError(this IGenericEvent evt) => evt.GetJSONNumber(rgstrQuicError);

		public static string ScrubAnonKey(string anonkey)
		{
#if !DEBUG
			if (anonkey?.Equals("null") ?? true)
				return string.Empty;
#endif // !DEBUG
			return anonkey;
		}

		public static JsonElement[] ParseSimpleJsonString(this IGenericEvent evt, string[] rgstr) => JSON_Util.ParseSimpleJsonString(evt.GetParams(), rgstr);


		// Implement: Array.IndexOf(string[], ReadOnlySpan<char>)
		public static int IndexOf(this string[] rgstr, ReadOnlySpan<char> span)
		{
			for (int i = 0; i < rgstr.Length; ++i)
			{
				if (span.SequenceEqual(rgstr[i].AsSpan()))
					return i;
			}
			return -1;
		}


		// Efficiently compare two base URLs, either of which may or may not end with '/'.
		public static bool Equal2(this string url1, string url2)
		{
			int cch1 = url1.Length;
			int cch2 = url2.Length;

			// The length difference must be: -1, 0, 1
			if ((uint)(cch1 - cch2 + 1) > 2) return false; // common

			if (url1[^1] == '/')
				--cch1;

			if (url2[^1] == '/')
				--cch2;

			if (cch1 != cch2) return false;

			return string.Compare(url1, 0, url2, 0, cch1, StringComparison.Ordinal) == 0;
		}


		/*
			Parse strings such as these:
				"https://www.google.com \u003Chttps://google.com same_site>"
				"pm/https://fonts.gstatic.com \u003Chttps://example.com cross_site>"
			And return base URLs:
				"https://www.google.com"
				"https://fonts.gstatic.com"
		*/
		public static string BaseURLFromGroupId(this string strURL)
		{
			int t1Start = -1, t1Len = 0;

			int len = strURL.Length;
			for (int i = 0; i < len;)
			{
				// Skip separators
				while (i < len && (strURL[i] == '/' || strURL[i] == ' '))
					i++;

				if (i >= len) break;

				// Find the length of the current token
				int start = i;
				while (i < len && strURL[i] != '/' && strURL[i] != ' ')
					i++;

				int lenCur = i - start;

				// Track tokens and evaluate
				if (t1Start == -1)
				{
					// First valid token found; store its bounds
					t1Start = start;
					t1Len = lenCur;
				}
				else
				{
					// We have a previous token (t1) and a current token (domain).
					// Check if t1 meets the scheme criteria: ends with ':' and starts with a letter.
					if (strURL[t1Start + t1Len - 1] == ':' && char.IsLetter(strURL[t1Start]))
						return strURL[t1Start..(start+lenCur)];

					// Shift the current token back to become the new t1
					t1Start = start;
					t1Len = lenCur;
				}
			}

			// What URL string is this!?
			AssertImportant(false);
			return strURL;
		}

		/*
			Given a URL determine if it is of the type "https://123.4.56.789:9876/..."
			Returns false if not.
			Else use uri.Host or uri.DnsSafeHost (no [IPv6] brackets), and url.Port
		*/
		public static Uri CreateURI(this string strURL, bool fIPOnly = false)
		{
			if (!Uri.TryCreate(strURL, UriKind.Absolute, out Uri uri))
				return null;

			if (fIPOnly)
			{
				UriHostNameType type = uri.HostNameType;
				if (type != UriHostNameType.IPv4 && type != UriHostNameType.IPv6)
					return null;
			}

			return uri;
		}

		/*
			Parse "Host:Port" into a string and a ushort.
		*/
		public static string GetHostAndPort(this string strHostPort, out ushort port)
		{
			port = 0;
			if (string.IsNullOrWhiteSpace(strHostPort)) return null;

			ReadOnlySpan<char> span = strHostPort;
			int iPort = span.IndexOf(':');
			AssertCritical(iPort > 0); // host:port
			if (iPort <= 0) return null;

			if (!ushort.TryParse(span[(iPort+1)..], out port)) return null;

			return span[0..iPort].ToString();
		}

		/*
			Headers: {"headers":["HTTP/1.1 304 Not Modified",...]}
			Extract: "304 Not Modified"
		*/
		public static string GetStatusJSON(this string strJSON)
		{
			AssertImportant(strJSON.StartsWith("{\"headers\":[\"HTTP/"));

			int iStart = strJSON.IndexOf(":[\"");
			if (iStart < 0)
				return string.Empty;

			iStart = strJSON.IndexOf(' ', iStart+3);
			if (iStart < 0)
				return string.Empty;

			int iEnd = strJSON.IndexOf('\"', iStart);
			if (iEnd <= iStart)
				return string.Empty;

			return strJSON[(iStart+1)..iEnd];
		}


		// net_error_list.h   (negative numbers)
		// quic_error_codes.h (positive numbers)
		public static string ErrorFromI(int iError)
		{
			if (iError == 0)
				return string.Empty;

			if (iError > 0)
				return iError.ToString();

			string strError = iError switch
			{
				  -2 =>   "-2: FAILED",
				  -3 =>   "-3: ABORTED",
				 -21 =>  "-21: NETWORK_CHANGED",
				-100 => "-100: CONNECTION_CLOSED",
				-101 => "-101: CONNECTION_RESET",
				-103 => "-103: CONNECTION_ABORTED",
				-105 => "-105: NAME_NOT_RESOLVED",
				-106 => "-106: ERR_INTERNET_DISCONNECTED",
				-109 => "-109: ERR_ADDRESS_UNREACHABLE",
				-118 => "-118: CONNECTION_TIMED_OUT",
				-173 => "-173: WS_UPGRADE",
				-400 => "-400: CACHE MISS",
				   _ => null
			};

			if (strError != null)
				return strError;

			int uError = (-iError) / 100;
			strError = iError.ToString();

			return uError switch
			{
				0 => strError + ": System Error",       //   1- 99
				1 => strError + ": Connecton Error",    // 100-199
				2 => strError + ": Certificate Error",  // 200-299
				3 => strError + ": HTTP Error",         // 300-399
				4 => strError + ": Cache Error",        // 400-499
				5 => strError + ": Misc Error",         // 500-599
				6 => strError + ": FTP Error",          // 600-699
				7 => strError + ": Cert Manager Error", // 700-799
				8 => strError + ": DNS Resolver Error", // 800-899
				9 => strError + ": Blob Error",         // 900-999
				_ => strError
			};
		}


		/*
			Garbage Collect
			Make a list of all List items which are 'Gone' and therefore garbage collectable.
			Use the new list to delete items from the main list.
		*/
		public static void DoGC<K, V>(this Hash<K, V> hash) where V : class, IGCollectable
		{
			List<K> keyRemove = null;

			foreach (var kvp in hash)
			{
				if (kvp.Value?.Gone ?? true)
				{
					if (keyRemove == null)
						keyRemove = new List<K>(hash.Count / 2);

					keyRemove.Add(kvp.Key);
				}
			}

			if (keyRemove == null) return;

			if (keyRemove.Count == hash.Count)
			{
				hash.Clear();
			}
			else
			{
				foreach (var key in keyRemove)
					hash.Remove(key);
			}
		}
	} // Util


	/*
		For GarbageCollect / DoGC()
	*/
	public interface IGCollectable
	{
		bool Gone { get; set; }
	}


	public class ResolverManager : IGCollectable
	{
		public string host;           // "https://www.google.com"
		public string anon_key;       // "https://google.com same_site"
		public string[] rgstrAddress; // { "XX.XX.XX.XX", ... }
		public string[] rgstrCanon;   // { "www.google.com" } // or alternate DNS name

		public bool Gone { get; set; }
	} // ResolverManager


	public class Socket : IGCollectable
	{
		// If Type==TCP at the end then it's probably HTTP1.1. If it's HTTP2 then the event SSL_CONNECT would set it.
		public StreamType Type { get; set; }

		readonly public IDVal pid;
		readonly public IDVal tid;

		public IPEndPoint addrLocal;
		public IPEndPoint addrRemote;

		public TimestampUI timeStampCreate; // SOCKET_ALIVE
		public TimestampUI timeStampConnect; // TCP_CONNECT.Begin
		public TimestampUI timeStampClose; // TCP_CONNECT.End & SOCKET_CLOSED.Instant

		public WinsockAFD.Connection cxn;

		public int iError;

		public bool fSSL;
		public bool fCanceled;
		public bool fGathered;
#if DEBUG
		public bool fTCP;

		public int cref; // attached to how many HTTP2/3 Sessions?
		public int crefH1; // attached to HTTP1 Session(s)

		public QWord uidDB; // for debugging
#endif // DEBUG
		public UIDVal uidBound; // from SOCKET_POOL_BOUND_TO_SOCKET

		public Socket(StreamType type, in IGenericEvent evt)
		{
			AssertCritical(type != StreamType.Unknown && type != StreamType.CACHE);
			this.Type = type;
			this.pid = evt.ProcessId;
			this.tid = evt.ThreadId;
			this.timeStampCreate = evt.Timestamp.ToGraphable();
			this.timeStampConnect = this.timeStampCreate; // may be overwritten later
			this.timeStampClose.SetMaxValue();
			this.fSSL = (type == StreamType.QUIC || type == StreamType.HTTP2); // if HTTP1, set it later
#if DEBUG
			this.uidDB = evt.GetUID();
			this.fTCP = (type != StreamType.QUIC);
#endif // DEBUG
		}

		public string Error()
		{
			if (this.iError != 0)
				return Util.ErrorFromI(this.iError);

			if (this.fCanceled)
				return "Canceled";

			return string.Empty; // ""
		}

		public ushort WSSocket()
		{
			ushort wssocket = (ushort)this.addrLocal.PortGraphable();
			if (wssocket == 0 && this.cxn != null)
				wssocket = this.cxn.socket;

			AssertImportant(this.cxn == null || this.cxn.socket == wssocket);
			return wssocket;
		}

		public void SetAddrLocalRemote(string strLocal, string strRemote)
		{
			ushort port;
			IPAddress ipAddress;

			AssertImportant(this.addrLocal.Empty());

			if (strLocal != null && DNSClient.DNSTable.TryParseWithPort(strLocal, out ipAddress, out port))
				this.addrLocal = new IPEndPoint(ipAddress, port);

			AssertImportant(FImplies(strLocal != null, !this.addrLocal.Empty()));

			if (strRemote != null && DNSClient.DNSTable.TryParseWithPort(strRemote, out ipAddress, out port))
			{
				if (this.addrRemote.Empty())
					this.addrRemote = new IPEndPoint(ipAddress, port);
				else
					AssertImportant(this.addrRemote.Equals(new IPEndPoint(ipAddress, port)));
			}

			AssertImportant(!this.addrRemote.Empty());
		}

		public bool Closed => !this.timeStampClose.HasMaxValue();

		public void Close(in TimestampUI timeStamp)
		{
			if (this.Closed) return;

			this.timeStampClose = timeStamp;

			this.Gone = true;
		}

		// Implement IGCollectable
		public bool Gone { get; set; }
	} // Socket


	/*
		Stream Types:
		- CACHE : filesystem
		- TCP : HTTP1 or HTTP2 TBD
		- HTTP1 / TCP : HTTP/1.1 probably
		- HTTP2 / TCP
		- HTTP3 / QUIC / UDP
	*/
	public enum StreamType { Unknown = 0, CACHE, TCP, HTTP1, HTTP2, QUIC }

	/*
		Placeholder Sessions have placeholder Streams wth special IDs.
	*/
	public enum IStreamSpecial : int
	{
		HTTP1 = -1,
		CACHE = -2,
		UNKNOWN = -3,
		MIN = UNKNOWN
	};


	public class Request : Gatherable, IGraphableEntry, IGCollectable
	{
		public StreamType Type { get; set; } // default: Unknown

		public StreamType TypeTCP { get; set; } // If the Type of the bound socket/session is a TCP type (HTTP1/2) then it will be this.

		readonly public IDVal pid;
		readonly public IDVal tid;

		private string _domain;
		private string _canon = string.Empty; // canonical server name (if different from domain)
		private string _url;
		private string _urlScrub; // #fragment identifier stripped

		public string method;

		public string anon_key; // network_anonymization_key
		public string strTaskStash; // StashStalledRequest

		// Alternates to what's in the Socket
		public string ipAddr;
		public ushort port;

		public int hidGroup; // hash of group_id string: TCP_CLIENT_SOCKET_POOL_REQUESTED_SOCKET & SOCKET_POOL_CONNECT_JOB_CREATED
		public int iError; // "net_error":-173

		// Corresponding xfer byte counts are in the Stream, but these may be posted before the Stream is created.
		public uint cbUpload, cbDownload;
		public bool fChunkedUpload;

		public Priority priority;

		public UIDVal uidQUIC; // UID of HTTP_STREAM_JOB: use_quic:true
		public UIDVal uidTCP;  // UID of HTTP_STREAM_JOB: use_quic:false

		private readonly QWord uidCreate64;

		public UIDVal uidRequest; // 64-bit ID of URL_REQUEST_START_JOB

		public TimestampUI timeStampBeginJob; // IRL_REQUEST_START_JOB.Begin ETW Timestamp
		public TimestampUI timeStampEndJob;   // IRL_REQUEST_START_JOB.End ETW Timestamp
		public TimestampETW timeRef;

		public IStackSnapshot stack;
#if DEBUG
		// For HTTP1:
		private Socket _socketTCP; // HTTP1 / TCP
		public Socket SocketTCP
		{
			get => _socketTCP;
			set
			{
				AssertCritical(value?.fTCP ?? true);
				if (_socketTCP == null || value == null)
					_socketTCP = value;
				else
					AssertImportant(_socketTCP == value);
			}
		}
#endif // DEBUG

		private Session _sessionQUIC; // QUIC / HTTP3 / UDP
		private Session _sessionHTTP2; // HTTP2 / TCP
		private Session _sessionOther; // HTTP1 / CACHE

		public Session SessionQUIC
		{
			get => this._sessionQUIC;

			set
			{
				AssertCritical(value == null || value.Type == StreamType.QUIC);
				if (this._sessionQUIC == null || value == null)
					this._sessionQUIC = value;
				else
					AssertImportant(this._sessionQUIC == value);
			}
		}

		public Session SessionHTTP2
		{
			get => this._sessionHTTP2;

			set
			{
				AssertCritical(value == null || value.Type == StreamType.HTTP2);
				if (this._sessionHTTP2 == null || value == null)
					this._sessionHTTP2 = value;
				else
					AssertImportant(this._sessionHTTP2 == value);
			}
		}

		public Session SessionOther
		{
			get => this._sessionOther;
		}

		// CHROMIUM regularly creates or reuses two simultaneous Sessions: HTTP2/TCP and QUIC/HTTP3/UDP
		// In most cases one or the other is chosen by the event: HTTP_STREAM_JOB_BOUND_TO_REQUEST
		// This is the Session which contains the Stream which is linked to this Request.
		public Session Session
		{
			get
			{
				AssertImportant(FImplies(!this.IsSessionEmpty, this.Type != StreamType.Unknown && this.Type != StreamType.TCP));

				Session sessionRet;
				switch (this.Type)
				{
				case StreamType.QUIC:
					sessionRet = this._sessionQUIC;
					break;

				case StreamType.HTTP2:
					sessionRet = this._sessionHTTP2;
					break;

				case StreamType.HTTP1:
				case StreamType.CACHE:
					sessionRet = this._sessionOther;
					break;

				default:
					return null;
				}

				// If there is a Stream, it must be attached to the new Session.
				AssertCritical(this.stream == null || (sessionRet.rgStream?.ContainsValue(this.stream) ?? false));
				return sessionRet;
			}

			set
			{
				StreamType type = value?.Type ?? this.Type;
				AssertImportant(FImplies(value != null, type != StreamType.Unknown && type != StreamType.TCP));

				switch (type)
				{
				case StreamType.QUIC:
					this.SessionQUIC = value;
					break;

				case StreamType.HTTP2:
					this.SessionHTTP2 = value;
					break;

				case StreamType.HTTP1:
				case StreamType.CACHE:
					AssertImportant(this._sessionOther == null || this._sessionOther == value);
					this._sessionOther = value;
					break;

				default:
					AssertCritical(false);
					break;
				}

				DEBUG(this.Session); // Do the 'get' DEBUG checks.
			}
		}

		/*
			Set both this.Type and this.Session
			this.Session will afterward return the set value (non-null).
		*/
		public Session SessionSet
		{
			set
			{
				AssertCritical(value != null);
				if (value == null) return;

				AssertCritical(this.Type == value.Type || this.Type == StreamType.Unknown || this.Type == StreamType.TCP);

				this.Type = value.Type;
				this.Session = value; // Do the 'set' and 'get' DEBUG checks.
			}
		}

		public void SessionReset()
		{
			this.Type = StreamType.Unknown;
			this.TypeTCP = StreamType.Unknown;

			this._sessionQUIC = null;
			this._sessionHTTP2 = null;
			this._sessionOther = null;
			this.stream = null;
#if DEBUG
			this._socketTCP = null;
#endif // DEBUG
		}

		public bool IsSessionEmpty => this._sessionQUIC == null && this._sessionHTTP2 == null && this._sessionOther == null;

		public Session.Stream stream;

		public XLink xlink;

		public bool fWebSocket; // WEBSOCKET_ALIVE
		public bool fSSL;
		public bool fRedirect;
		public bool fCanceled;

		public string URL
		{
			get => this._url;

			set
			{
				this._url = value;
				int iHash = value.IndexOf('#');
				this._urlScrub = (iHash < 0) ? value : value.Substring(0, iHash);

				// Convert: https://123.456.78.90:432/
				Uri uri = this._urlScrub.CreateURI(true);
				if (uri != null)
				{
					this.ipAddr = uri.Host;
					this.port = (ushort)uri.Port;
				}
			}
		}

		public string URLScrub => this._urlScrub;

		public string Canon
		{
			get => this._canon;

			set
			{
				AssertImportant(!string.IsNullOrWhiteSpace(this.Domain));
				AssertImportant(!string.IsNullOrWhiteSpace(value));
				AssertImportant(!value.IsNA());

				if (!value.Equals(this.Domain))
					this._canon = value;
			}
		}

		public string Domain
		{
			get
			{
				if (this._domain != null) return this._domain;
				if (this._urlScrub == null) return null;
				return this._urlScrub.CreateURI()?.Host;
			}

			set => this._domain = value;
		}

		public string AddressAndPort()
		{
			Socket soc = this.Session?.socket;
			if (!(soc?.addrRemote).Empty())
				return soc.addrRemote.ToGraphable();

			string strAddr = NetBlameCustomDataSource.Util.strNA;

			if (!string.IsNullOrWhiteSpace(this.ipAddr))
			{
				strAddr = this.ipAddr;
				if (this.port != 0)
					strAddr += ":" + this.port.ToString();
			}

			return strAddr;
		}

		public static readonly string strPreconnect = "preconnect";

		public bool IsPreconnect => string.ReferenceEquals(this.method, strPreconnect);

		public bool IsSpeculative => this.IsPreconnect && this.PreKey() == 0; // See PreconnectRequest()

		public QWord WSConnection => this.Session?.socket?.cxn?.qwEndpoint ?? 0; // Winsock Connection ID

		public bool FAttachedToStream => this.stream != null;

		public int PreKey()
		{
			AssertCritical(this.IsPreconnect);
#if DEBUG
			switch (this.Type)
			{
				case StreamType.QUIC:
				case StreamType.HTTP2:
				case StreamType.HTTP1:
					AssertImportant(this.Session != null);
					break;

				case StreamType.TCP:
					AssertImportant(this.hidGroup != 0);
					break;

				case StreamType.Unknown:
					AssertImportant(this.TypeTCP != StreamType.Unknown || this.stream == null);
					break;

				default:
					AssertImportant(false);
					break;
			}
#endif // DEBUG
			// May return 0 (IsSpeculative)
			return this.Session?.PreKey ?? this.hidGroup;
		} // PreKey


#if DEBUG
		public List<UIDVal> rguidDB = new(16);

		public List<int> rgsrcdepDB = new(8);

		public void AddSrcDep(int srcdep)
		{
			if (!this.rgsrcdepDB.Contains(srcdep))
				this.rgsrcdepDB.Add(srcdep);
		}

		private static QWord ReconstructUID(/*QWord*/UIDVal uid) => uid;
#else // !DEBUG

		/*[Conditional("DEBUG")]*/
		public List<UIDVal> rguidDB;

		// The Event Id originates as: QWord Id = (DWord)ProcessEventGroupCounter ^ (QWord)ProcessRandomKey;
		private QWord ReconstructUID(/*DWord*/UIDVal uid) => (QWord)uid | (this.uidCreate64 & 0xFFFFFFFF00000000);
#endif // !DEBUG

		[Conditional("DEBUG")]
		public void AddUID(UIDVal uid)
		{
			if (!this.rguidDB.Contains(uid))
				this.rguidDB.Add(uid);
		}

		// Final UI rendering of the Event ID value for the WPA Request Table.
		public QWord UIDRequest => ReconstructUID(this.uidRequest);
		public QWord UIDSession => (this.Session != null) ? ReconstructUID(this.Session.uidVal) : 0;


		public Request(in IGenericEvent evt)
		{
			this.pid = evt.ProcessId;
			this.tid = evt.ThreadId;
			this.timeRef = evt.Timestamp;
			this.stack = evt.Stack;
			this.priority = Priority.Unknown;
			this.timeStampEndJob.SetMaxValue();
			this.uidCreate64 = evt.GetUID64();
		}

		public Request(string strURL, in IGenericEvent evt) : this(in evt)
		{
			this.URL = strURL;
		}

		// URL_REQUEST_START_JOB: The Request may close and reopen when redirected. Preconnect Requests don't really close.
		public bool Closed => !this.timeStampEndJob.HasMaxValue() && !this.IsPreconnect;

		// CORS_REQUEST.End: The Request is truly closed.
		public bool Gone { get; set; }

		public string Error()
		{
			if (this.fRedirect)
				return "Redirected";

			if (this.iError != 0)
				return Util.ErrorFromI(this.iError);

			if (this.fCanceled)
				return "Canceled";

			if (this.IsSpeculative)
				return "Speculative";

			if (this.Session?.socket != null)
				return this.Session.socket.Error();

			if (this.Session?.FMigrated ?? false)
				return "Migrated";

			return string.Empty; // ""
		}

		public string Transport()
		{
			if (!this.fWebSocket)
			{
				if (this.Type == StreamType.Unknown)
					return string.Empty;

				return this.Type.ToString();
			}
			else
			{
				if (this.Type == StreamType.Unknown)
					return "WebSocket";

				return "WebSocket/" + this.Type.ToString();
			}
		}

		/*
			HTTP_STREAM_JOB_BOUND_TO_REQUEST / HTTP_STREAM_JOB
			The ID is of a HTTP_STREAM_JOB event,
			which had the attribute: use_quic: true or false
			The ID that matches determines the type of stream chosen.
		*/
		public void SetStreamType(UIDVal uid)
		{
			StreamType type;

			AssertCritical(uid != 0);
			AssertImportant(this.uidQUIC != this.uidTCP);

			if (uid == this.uidQUIC)
				type = StreamType.QUIC;
			else if (uid == this.uidTCP)
				type = (this.TypeTCP == StreamType.HTTP2) ? StreamType.HTTP2 : StreamType.HTTP1;
			else
				type = this.Type; // probably Unknown

			AssertImportant(FImplies(this.Type != StreamType.Unknown, this.Type == type));
			this.Type = type;
		}

		/*
			Set the ID of the HTTP_STREAM_JOB for use later by SetStreamType when one of them 'wins'.
			A previous HTTP_STREAM_JOB could have been canceled, so overwrites happen.
		*/
		public void SetStreamUID(UIDVal uid, bool fQuic)
		{
			if (fQuic)
			{
				AssertImportant(this.uidTCP != uid);
				this.uidQUIC = uid;
			}
			else
			{
				AssertImportant(this.uidQUIC != uid);
				this.uidTCP = uid;
			}
		}


		/*
			Return an IP address associated with this Request.
			(In reality there may be multiple associated addresses.)
		*/
		public IPAddress IPAddress(Socket soc)
		{
			if (soc != null && !soc.addrRemote.Empty())
				return soc.addrRemote.Address;

			if (string.IsNullOrWhiteSpace(this.ipAddr))
				return null;

			if (System.Net.IPAddress.TryParse(this.ipAddr, out IPAddress ipAddr))
				return ipAddr;

			return null;
		}

		/*
			Get the "net_error" from this event, if any, and assign it to this Request.
		*/
		public void SetNetError(in IGenericEvent evt)
		{
			int error = evt.GetNetError();
			if (error == jsonIntDefault) return; // common

			if (this.iError == 0)
				this.iError = error;
		}

		/*
			Create a placeholder Session/Stream for HTTP1 code simplicity.
			If true then caller invokes: sessionTable.Add(req.session);
		*/
		public bool FAttachPlaceholderSessionAndStream(Socket soc, in IGenericEvent evt)
		{
			AssertImportant(soc.Type != StreamType.Unknown && soc.Type != StreamType.QUIC);

			soc.fSSL |= this.fSSL;

			// If the Socket is SSL then its type was set by: SSL_CONNECT.End
			// If it's not SSL then it MUST be HTTP1.
			StreamType type = soc.fSSL ? soc.Type : StreamType.HTTP1;
			AssertImportant(this.Type == soc.Type || this.Type == type || this.Type == StreamType.Unknown);

			if (type == StreamType.TCP) return false; // handle this later

			AssertImportant(type == StreamType.HTTP1 || type == StreamType.HTTP2); // else what?

			// The this.Type may eventually be QUIC/HTTP3/UDP, but if it's TCP then it's this TypeTCP: HTTP1 or HTTP2
			this.TypeTCP = type;
			soc.Type = type;
#if DEBUG
			AssertCritical(soc.fTCP);

			// In Preconnect scenarios, multiple Sockets may spin up within the context of this Request.
			// See: TCP_CLIENT_SOCKET_POOL_REQUESTED_SOCKETS, SOCKET_POOL_CONNECTING_N_SOCKETS ("num_sockets":2+)

			if (this.SocketTCP == null || this.SocketTCP.iError != 0)
				this.SocketTCP = soc;
			else
				AssertCritical(FImplies(!this.IsPreconnect, this.SocketTCP == soc));
#endif // DEBUG

			if (type != StreamType.HTTP1) return false;

			// If it already has a Session, and not an error-Socket, then nothing more to do.
			if (this.Session != null && (this.Session.socket?.iError ?? 0) == 0) return false;

			// HTTP1 does not have a Session/Stream.
			// Create a dummy Session/Stream for this Request for simpler code overall.

			Session session = new Session(StreamType.HTTP1, in evt)
			{
				domain = this.Domain,
				port = (ushort)soc.addrRemote.PortGraphable(),
				uidVal = evt.GetUID()
			};

			Session.Stream stream = session.EnsureStream((int)IStreamSpecial.HTTP1, evt.Timestamp.ToGraphable());
			stream.strURL = this.URLScrub;
			stream.strMethod = this.method;
			stream.strDomain = this.Domain;
			stream.Attach(this);

			session.Attach(soc);
			this.SessionSet = session;

			AssertCritical(this.Type == StreamType.HTTP1);
			AssertCritical(stream.request.Session == session);

			return true;
		}

		/*
			Use the Request to create a placeholder Session when no Socket is available (for code simplicity).
			Maybe tracing started too late to capture the real Session.
			A CACHE Request has no Socket, since data comes from the filesystem.
			If true then caller invokes: sessionTable.Add(req.Session<TYPE>);
		*/
		public bool FAttachPlaceholderSessionAndStream(StreamType type, in IGenericEvent evt)
		{
			if (this.Session != null)
				return false;

			// If this Request is already a different type, don't create a new placeholder Session.

			AssertImportant(this.Type == type || this.Type == StreamType.Unknown);
			if (this.Type != type && this.Type != StreamType.Unknown)
				return false;

			// CACHE type does not have a Session/Stream.
			// Create a dummy Session/Stream for this Request for simpler code overall.

			Session session = new Session(type, in evt)
			{
				domain = this.Domain,
				port = this.port,
				uidVal = evt.GetUID()
			};

			IStreamSpecial iStream = type switch
			{
				StreamType.CACHE => IStreamSpecial.CACHE,
				StreamType.HTTP1 => IStreamSpecial.HTTP1,
				_ => IStreamSpecial.UNKNOWN
			};

			Session.Stream stream = session.EnsureStream((int)iStream, evt.Timestamp.ToGraphable());
			stream.strURL = this.URLScrub;
			stream.strMethod = this.method;
			stream.strDomain = this.Domain;
			stream.Attach(this);

			this.SessionSet = session;

			AssertCritical(this.Type == type);
			AssertCritical(stream.request.Session == session);

			return true;
		}

		/*
			Create a place holder Session based on this Request.
			Caller: sessionTable.Add(session)
		*/
		public Session NewPlaceholderSession(StreamType type, in IGenericEvent evt)
		{
			AssertCritical(type == StreamType.HTTP2 || type == StreamType.QUIC); // else FAttachPlaceholderSessionAndStream

			return new Session(type, in evt)
			{
				domain = this.Domain,
				port = this.port,
				uidVal = evt.GetUID(),
				fRecovered = true
			};
		}


		/*
			The corresponding Session/Stream was migrated, and activity happened both before and after.
			This Request followed new Stream, but we also need a Request that references the old Stream.
			Return a copy of the Request with the necessary adjustments. Also adjust 'this'.
			** The caller must set request2.session / .stream **
		*/
		public Request Migrate(in TimestampUI timeMigrate)
		{
			AssertCritical(timeMigrate.Between(this.timeStampBeginJob, this.timeStampEndJob));

			Request request2 = (Request)this.MemberwiseClone();  // shallow

			this.timeStampBeginJob = request2.timeStampEndJob = timeMigrate;

			request2.SessionReset();
#if DEBUG
			AssertImportant(!request2.fGathered);
#endif // DEBUG
			return request2;
		}


		/*
			Set the close time, and fill in missing fields.
		*/
		public void Close(in TimestampUI timeStamp)
		{
			// Close the Request.
			if (this.timeStampEndJob.HasMaxValue())
				this.timeStampEndJob = timeStamp;
		} // Close

		public void Close(ResolverManager resolver, DNSClient.DNSTable dnsTable, in TimestampUI timeStamp)
		{
			Close(in timeStamp);

			AssertCritical(!string.IsNullOrWhiteSpace(this.URL));

			Socket socket = this.Session?.socket;

			if (socket != null)
			{
				// These values are columns in the WPA Network Table.

				if (this.port == 0 && !socket.addrRemote.Empty())
					this.port = (ushort)socket.addrRemote.PortGraphable();
			}

			if (resolver != null)
			{
				if (string.IsNullOrWhiteSpace(this.Canon) && resolver.rgstrCanon?.Length > 0)
					this.Canon = resolver.rgstrCanon[0];

				if (string.IsNullOrWhiteSpace(this.ipAddr) && resolver.rgstrAddress?.Length > 0)
					this.ipAddr = resolver.rgstrAddress[0];
			}

			if (!string.IsNullOrWhiteSpace(this.ipAddr) && !string.IsNullOrWhiteSpace(this.Canon)) return;

			// Resort to the DNS Table to fill in missing information.

			IPAddress ipAddress = this.IPAddress(socket);

			if (ipAddress.Empty())
			{
				uint iDNS = dnsTable.IFindDNSEntryByServer(this.Domain);

				// NOTE: There may be multiple addresses for this domain. Choose the first one since we don't know otherwise.
				ipAddress = dnsTable.AddressFromI(iDNS, 1);
				if (!ipAddress.Empty())
					this.ipAddr = ipAddress.ToString();
			}

			if (!ipAddress.Empty())
			{
				string strServerAlt = NetBlameCustomDataSource.Util.strNA;
				string strServer = dnsTable.DNSNameAndAlt(ipAddress, ref strServerAlt);

				AssertImportant(!string.IsNullOrWhiteSpace(this.Domain));
				AssertInfo(this.Domain.Equals(strServer));

				if (!strServerAlt.IsNA())
					this.Canon = strServerAlt;
			}
		} // Close


		// Implement IGraphableEntry for the Chromium Requests graph/table.
		public IDVal Pid => this.pid;
		public IDVal TidOpen => this.tid;
		public TimestampETW TimeRef => this.timeRef;
		public TimestampUI TimeOpen => this.timeStampBeginJob;
		public TimestampUI TimeClose => this.timeStampEndJob;
		public IStackSnapshot Stack => this.stack;
		public XLinkType LinkType => this.xlink.typeNext;
		public uint LinkIndex => this.xlink.IFromNextLink;
	} // Request


	/*
		A Session contains Streams and refers to a Socket and to a ResolverManager.
		A REAL Session can be one of two types:
		- HTTP2 / TCP
		- HTTP3 / QUIC / UDP
		For convenience, we may also fabricate other types:
		- HTTP1 / TCP
		- CACHE
	*/
	public class Session : IGCollectable
	{
		public readonly StreamType Type;

		public string domain;
		public string anon_key; // network_anonymization_key

		public ushort port;

		readonly public IDVal pid;
		readonly public IDVal tid;

		public UIDVal uidVal;
		public int srcdep;

		public int iError;

		public bool fPreMigrate; // This Session is cloned from a migrated Session.
		public bool fRecovered; // Recreated after the original Session was not found.

		public Session sessionPreMigrate; // This references the Session that was cloned.

		public TimestampUI timeMigrate;

		public TimestampETW timeReference;

		public ResolverManager resolver;
	/*
		HTTP2 Sessions can have at most one Socket.
		QUIC Sessions can have multiple Sockets if the current Socket degrades, etc.
		In that case, we'll Clone the Session so that there is still one active Socket per Session.
	*/
		public Socket socket;
		public Socket socketPreMigrate;

		// Requests waiting to attach to a Stream
		public List<Request> rgRequestPending;
#if DEBUG
		public bool fGathered;
#endif // DEBUG

		public bool Gone { get; set; }

		public int PreKey => (int)this.uidVal; // for FindPreconnectRequest

		/*
			QUIC_SESSION.End
			HTTP2_SESSION.End
		*/
		public void Shutdown()
		{
			AssertImportant(this.Closed);

			this.Gone = true;
			if (this.resolver != null)
				this.resolver.Gone = true;
		}

		public class Stream
		{
			public uint cbSend;
			public uint cbRecv;
			public uint cbUpload;   // UPLOAD_DATA_STREAM_INIT
			public uint cbDownload; // URL_REQUEST_JOB_FILTERED_BYTES_READ
			public string strURL;
			public string strMethod;
			public string strOrigin;
			public string strReferer;
			public string strDomain;
			public string strHTTPStatus;

			// The SEND HEADERS event creates the Stream.
			public TimestampUI timeFirst;
			// A Stream closes when there is a "fin":true for both Send and Receive events (Headers or Data).
			// But for now we just capture the timestamp for every event, last one wins.
			public TimestampUI timeLast;

			public Request request;

			public int iError;
			public bool fAbandoned;
			public bool fIgnore;
			public bool fChunkedUpload; // cbUpload is likely invalid

			public uint CbSend() => Math.Max(this.cbSend, this.cbUpload);
			public uint CbRecv() => Math.Max(this.cbRecv, this.cbDownload);
			public bool HasDataTraffic() => (this.cbSend | this.cbRecv | this.cbUpload | this.cbDownload) != 0;

			public Stream Clone() => (Stream)this.MemberwiseClone();

			// Mark the last time of an event of interest on this Stream.
			public void SetLastTime(TimestampUI timeStamp)
			{
				AssertImportant(!this.timeLast.HasMaxValue());
				AssertImportant(this.timeLast.Between(this.timeFirst, timeStamp));
				this.timeLast = timeStamp;
			}

			/*
				Validate the Request and attach it to the Stream with references in both directions.
				The caller should set the Session of the Request.
			*/
			public void Attach(Request req)
			{
				AssertCritical(req != null);

				if (this.request == req)
				{
					AssertCritical(req.stream == this);
					return;
				}
#if DEBUG
				AssertImportant(this.request == null);
				AssertImportant(req.stream == null);
				AssertImportant(req.method?.EndsWith(this.strMethod) ?? false);
				AssertImportant(req.Domain?.Equals(this.strDomain) ?? false);
				AssertImportant(req.URLScrub?.Equals(this.strURL) ?? false);
#endif // DEBUG
				this.request = req;
				req.stream = this;

				// Reset past errors, such as from HTTP_TRANSACTION_RESTART_AFTER_ERROR or HTTP_TRANSACTION_RESTART_MISDIRECTED_REQUEST
				this.iError = 0;
				req.iError = 0;
			}

			/*
				The Stream was speculative, but it didn't work out,
				so mark it as abandoned and null the references.
			*/
			public void Abandon(bool fHard)
			{
				// Let the Request refer to a different Stream in the future.
				if (this.request != null)
					this.request.stream = null;

				// Leave this.request.session for reference in some cases, unless there was no data transferred.
				if (fHard || !this.HasDataTraffic())
				{
					// Fully abandon this Stream!
					this.request = null;
					this.fAbandoned = true;
				}
			}
		} // Stream


		public SortedList<int, Stream> rgStream;

		/*
			Stream indices are sparse.
			Return the Stream, creating a new one (within this Session) if needed.
			Update the timestamp.
		*/
		public Stream EnsureStream(int iStream, in TimestampUI timeStamp)
		{
			AssertCritical(iStream >= (int)IStreamSpecial.MIN);

			if (this.rgStream.TryGetValue(iStream, out Stream stream) && stream != null)
			{
				stream.SetLastTime(timeStamp);
				return stream;
			}

			this.rgStream[iStream] = stream = new Stream()
			{
				timeFirst = timeStamp,
				timeLast = timeStamp
			};

			return stream;
		}

		public Session(StreamType _type, in IGenericEvent evt)
		{
			AssertCritical(_type != StreamType.Unknown);
			this.Type = _type;
			this.pid = evt.ProcessId;
			this.tid = evt.ThreadId;
			this.timeReference = evt.Timestamp;
			this.rgStream = new(8); // In fact, there could be 10s or 100s of Streams.
			if (_type == StreamType.QUIC)
				this.rgRequestPending = new(8); // There should be at most a few pending Requests to attach to Streams.
		}

		/*
			QUIC_SESSION_CLOSED
			QUIC_SESSION_CLOSE_ON_ERROR
			HTTP2_SESSION_CLOSE
		*/
		public bool Closed { get; set; }

		public bool FQuic => this.Type == StreamType.QUIC;

		public bool FMigrated => this.sessionPreMigrate != null;

		/*
			Get the Remote Address:Port either from the Socket
			or by parsing the Address string array[0] from the attached ResolverManager.
			Might return null.
		*/
		public IPEndPoint RemoteAddress()
		{
			IPEndPoint addrRemote;
			if (this.socket == null && this.resolver?.rgstrAddress?.Length > 0 && IPAddress.TryParse(this.resolver.rgstrAddress[0], out IPAddress addrParse))
				addrRemote = new(addrParse, this.port);
			else
				addrRemote = this.socket?.addrRemote;

			return addrRemote;
		}

		/*
			Return an error code string from:
			- the Stream
			- the Stream's Request
			- this Session
			- "Migrated"
		*/
		public string Error(Stream stream)
		{
			// Stream
			if (stream.iError != 0)
				return Util.ErrorFromI(stream.iError);

			// Request
			string status = stream.request?.Error();
			if (!string.IsNullOrEmpty(status))
				return status;

			// Session
			if (this.iError != 0)
				return Util.ErrorFromI(this.iError);

			if (this.FMigrated)
				return "Migrated";

			if (this.fRecovered)
				return "Recovered Session";

			// Socket
			return string.Empty;
		}

		/*
			When is a Stream not 'valid' for processing?
			- fIgnore: It was created only for handshaking overhead.
			- fAbandoned: It was speculatively created, then abandoned.
			- migrated: It was cloned with a migration event, but this copy saw no action.
		*/
		public bool ValidStream(Stream stream)
		{
			AssertImportant(FImplies(stream.fIgnore || stream.fAbandoned, stream.request == null));

			if (stream.fIgnore) return false;
			if (stream.fAbandoned) return false;

			if (this.timeMigrate == stream.timeLast)
			{
				// This Session migrated (was cloned, and Streams reset), and nothing further happened on this Stream.
				AssertImportant(!stream.HasDataTraffic());
				AssertImportant(stream.timeFirst == stream.timeLast);
				return false;
			}

			return true;
		}

		/*
			Validate the Socket and attach it to this Session.
		*/
		public void Attach(Socket sock)
		{
			if (this.socket == sock) return;
#if DEBUG
			AssertCritical(sock.fTCP == !this.FQuic);
			if (sock.Type == StreamType.HTTP1)
			{
				// This is a placeholder HTTP1/TCP Session for algorithmic convenience.
				// There is only one Stream (-1), and the Socket may be reused across related HTTP1 Sessions.
				AssertImportant(this.rgStream.Count == 1);
				AssertImportant(sock.cref == 0);
				++sock.crefH1;
			}
			else
			{
				// This is a real HTTP2/TCP or HTTP3/QUIC Session, with potentially multiple Streams.
				// The Socket is unique per Session (unless 'migrated' under HTTP3).
				AssertImportant(++sock.cref == 1);
			}
#endif // DEBUG
			AssertImportant(sock.Type == this.Type);
			AssertImportant(this.socket == null);
			AssertImportant(this.port == sock.addrRemote.Port);

			this.socket = sock;
		}

		/*
			Add the Request to the QUIC Session's RequestPending list, to later be paired with a Stream.
			But do nothing if this Request is already on the RequestPending list or paired with a Stream.
		*/
		public void AttachQUIC(Request req)
		{
			// Only attach the Request as pending (to link to a QUIC Stream) when the Session is definitively QUIC.
			AssertImportant(req.Type == StreamType.QUIC);

			AssertCritical(req.pid == this.pid);
			AssertCritical(req.tid == this.tid);
			AssertCritical(this.Type == StreamType.QUIC);
			AssertImportant(req.Type == StreamType.Unknown || req.Type == this.Type);
			AssertImportant(FImplies(req.FAttachedToStream, req.Type == this.Type));

			if (req.FAttachedToStream) return;

			if (this.rgRequestPending.FindIndex(r => r == req) >= 0) return;

			this.rgRequestPending.Add(req);

			req.SessionQUIC = this;
		}

		/*
			After a Request has been matched/attached to a Stream, remove it from the Session's list of pending Requests.
			Do session.Finalize with stream.Attach UNLESS the Request came from: session.MatchRequest or this.MatchRequest
		*/
		public void Finalize(Request req)
		{
			AssertCritical((this.Type == StreamType.QUIC) == (this.rgRequestPending != null));

			this.rgRequestPending?.Remove(req);
			AssertImportant(req.stream != null); // from Stream.Attach(Request)

			if (this.Type == StreamType.QUIC)
				req.SessionQUIC = this;
			else
				req.SessionHTTP2 = this;
#if DEBUG
			if (this.socket != null)
				req.AddUID(this.socket.uidDB);
#endif // DEBUG
		}


		static readonly string[] rgstrAttrib =
		{
			":method", // GET, POST, etc.
			":scheme", ":authority", ":path", // Reconstruct the URL from these three.
			"origin",  // https://google.com  (may be absent)
			"referer"  // https://google.com/ (may be absent)
		};

		/*
			Given a string array derived from JSON "headers" attribute,
			reconstruct the original URL from: ':scheme', ':authority', ':path'
		*/
		static string URLFromHeaders(string[] rgstrHeaders)
		{
			// rgstrHeaders[] elements correspond to rgstrAttrib[]
			AssertCritical(rgstrHeaders.Length >= rgstrAttrib.Length);

			if (rgstrHeaders[1] != null && rgstrHeaders[2] != null && rgstrHeaders[3] != null)
			{
				System.Text.StringBuilder sb = new(rgstrHeaders[1].Length + rgstrHeaders[2].Length + rgstrHeaders[3].Length + 4);
				sb.AppendFormat("{0}://{1}{2}", rgstrHeaders[1], rgstrHeaders[2], rgstrHeaders[3]);
				return sb.ToString();
			}
			return null;
		}

		/*
			Certain SEND HEADER events contain information sufficient to construct a new Stream:
			Stream id, URL, Method, etc.
		*/
		public Stream PopulateStreamFromHeader(in IGenericEvent evt)
		{
			string strHeader = evt.GetParams();
			JsonElement[] rgje = ParseSimpleJsonString(strHeader, ChromiumTable.rgstrStreamId_QStreamId_Headers);
			if (rgje == null) return null;

			// stream_id or quic_stream_id or nothing
			int iStream = rgje[0].MyGetNumber((int)IStreamSpecial.UNKNOWN);
			if (iStream < 0)
				iStream = rgje[1].MyGetNumber((int)IStreamSpecial.UNKNOWN);

			Stream stream = this.EnsureStream(iStream, evt.Timestamp.ToGraphable());
			if (stream.strURL == null)
			{
				string[] rgstrHeaders = rgje[2].MyGetStringArray(rgstrAttrib);

				stream.strURL = URLFromHeaders(rgstrHeaders);
				stream.strMethod = rgstrHeaders[0];
				stream.strOrigin = rgstrHeaders[4];
				stream.strReferer = rgstrHeaders[5];
				stream.strDomain = rgstrHeaders[2];
			}
			else
			{
#if DEBUG
				string[] rgstrHeaders = rgje[2].MyGetStringArray(rgstrAttrib);
				AssertImportant(stream.strURL == URLFromHeaders(rgstrHeaders));
				AssertImportant(stream.strMethod == rgstrHeaders[0]);
				AssertImportant(stream.strOrigin == rgstrHeaders[4]);
				AssertImportant(stream.strReferer == rgstrHeaders[5]);
				AssertImportant(stream.strDomain == rgstrHeaders[2]);
				AssertCritical(stream.request?.Session == this);
#endif // DEBUG
			}

			return stream;
		} // PopulateStreamFromHeader

		static readonly string[] rgstrHeaderStatus = { ":status" };

		/*
			JSON: {"headers":["status: 200", ...],"stream_id":3}
			Get the stream_id and the status values.
			Set the Stream's status.
		*/
		public void SetHTTPStatus(in IGenericEvent evt)
		{
			string strJSON = evt.GetParams();
			JsonElement[] rgje = ParseSimpleJsonString(strJSON, ChromiumTable.rgstrStreamId_Headers);
			if (rgje == null) return;

			string[] rgstrStatus = rgje[1].MyGetStringArray(rgstrHeaderStatus);
			AssertImportant(rgstrStatus?.Length == 1);
			if (!(rgstrStatus?.Length > 0)) return;

			int iStream = rgje[0].MyGetNumber(-1);
			AssertCritical(iStream >= 0);
			if (iStream < 0) return;

			Stream stream = this.EnsureStream(iStream, evt.Timestamp.ToGraphable());

			stream.strHTTPStatus = rgstrStatus[0];
		}


		public int LookupPendingRequestByURL(string strURL) => this.rgRequestPending.FindIndex(r => r.URLScrub.Equals(strURL));

		/*
			Given a recently created Stream, find the corresponding Request from the Pending Request list.
			Find it by matching the 'scrubbed' URL (with no #fragment identifier).
			Remove the Request from the list and return it.
		*/
		public Request MatchRequest(Stream stream)
		{
			if (stream.request != null)
				return stream.request;

			int iReq = this.LookupPendingRequestByURL(stream.strURL);
			if (iReq < 0) return null;

			Request req = this.rgRequestPending[iReq];

			AssertCritical(req.pid == this.pid);
			AssertCritical(req.tid == this.tid);
			AssertImportant(req.method.EndsWith(stream.strMethod)); // "REDIRECT/GET" === "GET"

			// It's no longer pending, so remove it.
			this.rgRequestPending.RemoveAt(iReq);

			// Was there also another potential match!?
			AssertImportant(this.LookupPendingRequestByURL(stream.strURL) < 0);

			return req;
		}


		/*
			Create a Deep Clone for migrating the Session.
			A QUIC Session will need to migrate in the rare case when its UDP Socket degrades and is replaced with a new one.
			(In the HTTP2 case the TCP Socket is tied to the Session, so a new one should be created automatically if needed.)
			Create and return a pre-migration clone of the Session.
			Reset the Streams of the original Session and give it the new, post-migration Socket.
		*/
		public Session Migrate(in TimestampUI timeStamp)
		{
			AssertCritical(this.FQuic);
			AssertCritical(this.socketPreMigrate != null);

			Session sessionBefore = (Session)this.MemberwiseClone();  // shallow
			var rgStreamBefore = sessionBefore.rgStream = new SortedList<int, Stream>(this.rgStream); // shallow
		/*
			This Session is picking up where the original left off.
			Reset the byte counts so that the old and new Sockets each get correct xmission attribution.
			Request.stream will continue to refer to the elements of this.rgStream, not rgStreamBefore.
		*/
			foreach (var kvp in this.rgStream)
			{
				Stream stream = kvp.Value;
				rgStreamBefore[kvp.Key] = stream.Clone();

				stream.cbSend = stream.cbRecv = 0;
				stream.cbUpload = stream.cbDownload = 0;
				stream.iError = 0;
				stream.timeFirst = stream.timeLast = timeStamp;
				AssertImportant(!stream.HasDataTraffic());
			}

			this.socket = this.socketPreMigrate;
			this.socketPreMigrate = sessionBefore.socket;
			this.timeMigrate = timeStamp;

			sessionBefore.socketPreMigrate = this.socket;
			sessionBefore.fPreMigrate = true;

			sessionBefore.sessionPreMigrate = this.sessionPreMigrate; // null unless multiple migrations
			this.sessionPreMigrate = sessionBefore;

			return sessionBefore;
		}

		/*
			Do this work after parsing and before 'gathering'.

			When a QUIC Socket's connection degrades, a new one spins up to replace it.
			If the new Socket successfully connects then a Migration event is triggered.
			Since NetBlame is very Socket-oriented, we want to track both the old and the new Sockets.

			We created a copy of this Session (this.sessionPreMigrate) and its Streams.
			Then we updated the Socket on this Session and zeroed cbSend/Recv on its Streams.
			(Note that multiple migrations could theoretically happen on a Session.)

			Now we need to adjust the Requests which refer to one or both of these Sessions/Streams.
			(This is important for the Request-oriented 'Chromium Requests' graph/table.)
			There are three cases:
			- A migrated Request refers back to the pre-migration Stream copy because nothing happened on it post-migration.
			- A migrated Request remains with the post-migration Stream because not much happened on it pre-migration (cbSend/Recv==0).
			- A migrated Request gets cloned to refer to both the pre- and post-migration Streams because there was activity both pre- and post-migration.
		*/
		public void AdjustForMigration(Chromium.ChromiumTable requestTable)
		{
			// this: the Session & Streams (with xfer byte counts) after the migration (about to be 'gathered' for charting)
			// session2: the Session & Streams (with xfer byte counts) before the migration (not yet 'gathered' for charting)
			var session2 = this.sessionPreMigrate;
			AssertCritical(session2.fPreMigrate);
			AssertCritical(this.timeMigrate.HasValue());
			AssertCritical(this.rgStream.Count >= session2.rgStream.Count);
			AssertImportant(this.Type == StreamType.QUIC && session2.Type == StreamType.QUIC);
#if DEBUG
			AssertImportant(!this.fGathered);
			AssertImportant(!session2.fGathered);
#endif // DEBUG
			foreach (var kvp in session2.rgStream)
			{
				// stream2: the Stream (with xfer byte counts) before the migration
				Stream stream2 = kvp.Value;
				AssertImportant(stream2 != null);
				if (stream2 == null) continue;

				if (this.rgStream.TryGetValue(kvp.Key, out Stream stream))
				{
					// stream: the Stream (with xfer byte counts) after the migration
					AssertCritical(stream != stream2);
					AssertCritical(stream.fIgnore == stream2.fIgnore);
					AssertCritical(stream.fAbandoned == stream2.fAbandoned);

					if (stream.fAbandoned || stream.fIgnore) continue;

					// Both the 'before' and 'after' Streams refer to the same Request at this point.
					// But we may or may not need two Streams and two Requests, depending on whether activity happened before the migration, after, or both.
					AssertCritical(stream.request == stream2.request);

					Request request = stream.request;
					if (request == null) continue;
					AssertImportant(request.stream == stream);
					if (!request.FAttachedToStream) continue;

					AssertImportant(request.Session == this);
					AssertImportant(request.Session != session2);
					AssertImportant(stream2.request == request);

					stream2.request = null; // stream2.Attach sets this.

					if (this.timeMigrate >= stream.timeLast)
					{
						// There was no further action on this Stream after the migration.
						// Refer the Request back to the earlier copy of the Stream. Invalidate the later copy.

						AssertCritical(!this.ValidStream(stream));
						AssertImportant(session2.ValidStream(stream2));

						stream.request = null;
						stream.fAbandoned = true;

						request.SessionReset();
						stream2.Attach(request);
						request.SessionSet = session2;
					}
					else if (!stream2.HasDataTraffic() && stream.HasDataTraffic())
					{
						// The earlier copy of the Stream had no data transfer, but the migrated one did.
						// Just leave the Request referencing the migrated Stream.

						AssertImportant(this.ValidStream(stream));
						AssertCritical(!session2.ValidStream(stream2));

						stream2.fAbandoned = true;
					}
					else
					{
						// Both copies of the Stream saw activity, before and after the Migration.
						// There will need to be separate Requests to reference each Stream.
						// WPA's Chromium Requests table does random access of the Requests array, which needs to reference every active Stream. 

						AssertImportant(this.ValidStream(stream));
						AssertImportant(session2.ValidStream(stream2));
#if DEBUG
						AssertImportant(!stream.request.fGathered); // else this is too late!
#endif // DEBUG
						// Clone the original Request and adjust some values.
						Request request2 = request.Migrate(this.timeMigrate);
						requestTable.Add(request2);

						stream2.Attach(request2);
						request2.SessionSet = session2;
					}
				}
				else
				{
					// Every Stream in session2 should have a corresponding Stream in session1.
					AssertImportant(false);
				}
			} // foreach kvp
		} // AdjustForMigration
	} // Session


	public class Hash<K, T> : Dictionary<K, T> where T : class
	{
		public Hash(int c) : base(c) { }
		public bool HasValue(K key) => base.TryGetValue(key, out T t) && (t != default);

		public void Reset(K key) { base[key] = default; }

		// override the indexer for nothrow
		public new T this[K key]
		{
			set => base[key] = value;
			get
			{
				if (base.TryGetValue(key, out T t)) return t;

				return default;
			}
		}
	} // Hash


	/*
		There is a single Chromium pipeline thread which does all of the work of interest to us.
		But several of those threads could show up in one ETW session.
		There is one ThreadLocal object for each of them.
	*/
	public class ThreadLocal
	{
		public Session sessionRecent;

		public Request reqRecent;

		public Socket sockRecent;

		public UIDVal idRecent;

		public string strTaskRecent;

		public string strJSON;

		public List<Request> rgRequestStalled;

		// The ChromiumTable base class is List<Request> 

		readonly Hash<UIDVal, Request> reqFromUID;

		readonly Hash<int, Request> reqFromSrcDep;

		// ChromiumTable.socketTable is List<Socket>

		public readonly Hash<UIDVal, Socket> sockFromUID;

		public readonly Hash<int, Socket> sockFromSrcDep;

		// ChromiumTable.sessionTable is List<Session>

		public readonly Hash<UIDVal, Session> sessionFromUID;

		public readonly Hash<int, Session> sessionFromSrcDep;

		// ResolverManager is not stored as a List.

		public readonly Hash<UIDVal, ResolverManager> managerFromUID;

		public readonly Hash<int, ResolverManager> managerFromSrcDep;

		// IsDNS: HashSet of srcdep values of DNS-related Sockets

		public readonly HashSet<int> IsDNS;

		public ThreadLocal(int c)
		{
			this.rgRequestStalled = new(4);
			this.reqFromSrcDep = new(c);
			this.reqFromUID = new(c);
			this.sockFromSrcDep = new(c / 4);
			this.sockFromUID = new(c / 4);
			this.managerFromSrcDep = new(c / 8);
			this.managerFromUID = new(c / 4);
			this.sessionFromSrcDep = new(c / 8);
			this.sessionFromUID = new(c / 4);
			this.IsDNS = new(c / 8);
		}

		public void SetReqUID(Request req, UIDVal uid)
		{
			this.reqFromUID[uid] = req;
#if DEBUG
			req?.AddUID(uid);
#endif
		}

		public Request ReqFromUID(UIDVal uid) => this.reqFromUID[uid];

		public void SetReqSrcDep(Request req, int srcdep)
		{
			this.reqFromSrcDep[srcdep] = req;
#if DEBUG
			req?.AddSrcDep(srcdep);
#endif // DEBUG
		}

		public Request ReqFromSrcDep(int srcdep) => this.reqFromSrcDep[srcdep];

		/*
			RECENT: There are certain events which are ALWAYS emitted near/adjacent to each other (for a given pipelining thread).
			We can use the RECENT mechanism to pass data across these events, confirming the expected task name.
		*/
		public Request GetRecent(string strTask)
		{
			if (!strTask.Equals(this.strTaskRecent)) return null;

			return this.reqRecent;
		}

		public void SetRecent(Request req, string strTask)
		{
			this.sessionRecent = null;
			this.reqRecent = req;
			this.idRecent = 0;
			this.strTaskRecent = strTask;
		}

		public void SetRecent(Socket sock, string strTask)
		{
			this.sessionRecent = null;
			this.sockRecent = sock;
			this.idRecent = 0;
			this.strTaskRecent = strTask;
		}

		public void SetRecent(Session session, string strTask)
		{
			this.sessionRecent = session;
			this.idRecent = 0;
			this.strTaskRecent = strTask;
		}

		public void SetRecentUID(UIDVal uID, string strTask)
		{
			this.sessionRecent = null;
			this.reqRecent = null;
			this.sockRecent = null;
			this.idRecent = uID;
			this.strTaskRecent = strTask;
		}

		public UIDVal GetRecentUID(string strTask)
		{
			if (!strTask.Equals(this.strTaskRecent)) return 0;

			return this.idRecent;
		}

		public void ResetRecent()
		{
			this.sessionRecent = null;
			this.reqRecent = null;
			this.sockRecent = null;
			this.strTaskRecent = null;
			this.strJSON = null;
			this.idRecent = 0;
		}


#if DEBUG
		// more frequent collection
		private int cGC = 64;
#else // DEBUG
		// less frequent collection
		private int cGC = 1024;
#endif // DEBUG

		/*
			Occasionally remove lookups to closed Requests, ResolverManagers, Sessions, and Sockets
		*/
		public void GarbageCollect()
		{
			// Test cGC against what's probably the largest data structure.
			if (this.reqFromSrcDep.Count < cGC) return;

			this.reqFromSrcDep.DoGC();
			this.reqFromUID.DoGC();

			this.managerFromSrcDep.DoGC();
			this.managerFromUID.DoGC();

			this.sessionFromSrcDep.DoGC();
			this.sessionFromUID.DoGC();

			this.sockFromSrcDep.DoGC();
			this.sockFromUID.DoGC();

			// Do this again when the count gets larger.
			cGC = reqFromSrcDep.Count * 2;
		}

	} // ThreadLocal


	public class ChromiumTable : List<Request>
	{
		readonly AllTables allTables;

		public readonly List<Socket> socketTable;

		public readonly List<Session> sessionTable;

		readonly Hash<uint, ThreadLocal> threadLocal;

		readonly HashSet<string> unhandled;

		public ChromiumTable(int capacity, in AllTables _allTables) : base(capacity)
		{
			this.allTables = _allTables;
			this.socketTable = new(capacity / 4);
			this.sessionTable = new(capacity / 8);
			this.threadLocal = new(8); // a few active Chromium Network threads
			this.unhandled = new(64);
		}


		static uint KeyFromThread(IDVal pid, IDVal tid) => (uint)((pid << 16) ^ pid ^ tid);

		ThreadLocal ThreadLocalFromEvt(in IGenericEvent evt) => this.threadLocal[KeyFromThread(evt.ProcessId, evt.ThreadId)];

		ThreadLocal EnsureThreadLocal(in IGenericEvent evt)
		{
			uint kThread = KeyFromThread(evt.ProcessId, evt.ThreadId);
			ThreadLocal tl = this.threadLocal[kThread]; // no throw
			if (tl == default)
			{
				tl = new ThreadLocal(8192); // a medium-large data-set
				this.threadLocal[kThread] = tl;
			}
			return tl;
		}

		void ResetRecent(IDVal pid, IDVal tid)
		{
			ThreadLocal tl = this.threadLocal[KeyFromThread(pid, tid)];
			if (tl != default)
				tl.ResetRecent();
		}

		void AttachWinsockConnection(in IGenericEvent evt, WinsockAFD.IPPROTO ip)
		{
			Socket soc = this.SocketFromUID(in evt);
			if (soc == null) return;

			AssertImportant(!soc.Closed);
			AssertImportant(soc.timeStampConnect.ToNanoseconds != 0);

			// An AfdCreate (Winsock) event occurred between: TCP_CONNECT.Begin & TCP_CONNECT_ATTEMPT.Begin
			// That created a new Winsock table entry.

			AssertImportant(this.allTables.wsTable.Count > 0);

			IDVal tid = evt.ThreadId;
			WinsockAFD.Connection cxn = this.allTables.wsTable.FindLast(c => c.tidOpen == tid);

			AssertImportant(cxn != null);
			if (cxn == null)
				return;

			AssertImportant(!cxn.FClosed);
			AssertImportant(cxn.timeCreate > soc.timeStampConnect);

			if (cxn.timeCreate < soc.timeStampConnect)
				return;

			AssertImportant(cxn.ipProtocol == ip);
			AssertImportant(cxn.grbitType == (byte)Protocol.Winsock);

			// Once a Winsock Connection is marked with Chromium,
			// its Socket must be 'gathered' via: GatherChromium()

			cxn.grbitType |= (byte)Protocol.Chromium;

			soc.cxn = cxn;
		}

		/**** SOCKETS *****/

		Socket SocketFromUID(in IGenericEvent evt, bool fOpen = true)
		{
			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return null;

			UIDVal id = evt.GetUID();
			Socket sock = tl.sockFromUID[id];
			if (sock == null) return null;

			AssertCritical(sock.pid == evt.ProcessId && sock.tid == evt.ThreadId);

			if (fOpen && sock.Closed) return null;

			tl.SetRecent(sock, evt.TaskName);

			return sock;
		}

		Socket SocketFromSrcDep(int srcdep, in IGenericEvent evt)
		{
			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return null;

			AssertCritical(srcdep != jsonIntDefault);

			Socket sock = tl.sockFromSrcDep[srcdep];
			if (sock == null) return null;

			AssertCritical(sock.pid == evt.ProcessId && sock.tid == evt.ThreadId);

			UIDVal id = evt.GetUID();
			tl.sockFromUID[id] = sock;
			tl.SetRecent(sock, evt.TaskName);

			return sock;
		}

		Socket SocketFromSrcDep(in IGenericEvent evt) => SocketFromSrcDep(evt.GetSourceId(), in evt);

		Socket SocketFromRecent(in IGenericEvent evt, string strTask)
		{
			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return null;

			// Maybe, maybe not...
			AssertImportant(strTask.Equals(tl.strTaskRecent));

			if (!strTask.Equals(tl.strTaskRecent)) return null;

			tl.strTaskRecent = evt.TaskName;

			UIDVal id = evt.GetUID();
			tl.sockFromUID[id] = tl.sockRecent;

			return tl.sockRecent;
		}

		void SocketAttachUID(Socket sock, UIDVal uID, in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
			tl.sockFromUID[uID] = sock;
		}

		void SocketAttachSrcDep(Socket sock, int srcdep, in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
			tl.sockFromSrcDep[srcdep] = sock;
		}

		void SocketAttachUID_SrcDep(Socket sock, int srcdep, in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
#if DEBUG
			Socket sockT = tl.sockFromSrcDep[srcdep];

			// sockT must be null or the same or closed (reused srcdep!?) or error (retrying).
			AssertImportant(sockT == null || sockT == sock || sockT.Closed || sockT.iError != 0);
			AssertCritical(sock != null);
			AssertCritical(sock.pid == evt.ProcessId && sock.tid == evt.ThreadId);
#endif // DEBUG
			if (srcdep != jsonIntDefault)
				tl.sockFromSrcDep[srcdep] = sock;

			UIDVal id = evt.GetUID();
			tl.sockFromUID[id] = sock;
			tl.SetRecent(sock, evt.TaskName);
		}

		void SocketAttachUID_SrcDep(Socket sock, in IGenericEvent evt) => SocketAttachUID_SrcDep(sock, evt.TryGetSourceId(), in evt);

		/*
			RECENT: There are certain events which are ALWAYS emitted near/adjacent to each other (for a given pipelining thread).
			We can use the RECENT mechanism to pass data across these events, confirming the expected task name.
		*/
		UIDVal GetRecentUID(string strTaskName, in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
			return tl.GetRecentUID(strTaskName);
		}

		void SetRecentUID(in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
			tl.SetRecentUID(evt.GetUID(), evt.TaskName);
		}

		/**** REQUESTS ****/

		/*
			Return the most recent Request which likely matches the given Stream.
		*/
		Request MatchRequest(IDVal pid, IDVal tid, Session.Stream stream)
		{
			Request req = this.FindLast(r => r == stream.request || (r.pid == pid && r.tid == tid && r.URLScrub.Equals(stream.strURL)));
			AssertImportant(req?.method?.EndsWith(stream.strMethod) ?? true);
			return req;
		}


		[Conditional("DEBUG")]
		void AssertValidRequest(Request req, in IGenericEvent evt)
		{
			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			Request reqT = tl?.ReqFromUID(evt.GetUID());
			AssertImportant(reqT == null || reqT == req || reqT.Closed);
			AssertCritical(req != null);
			AssertCritical(req.pid == evt.ProcessId && req.tid == evt.ThreadId);
		}

		void RequestAttachUID(Request req, UIDVal uid, in IGenericEvent evt)
		{
			this.AssertValidRequest(req, in evt);

			ThreadLocal tl = EnsureThreadLocal(in evt);
			tl.SetReqUID(req, uid);
			tl.SetRecent(req, evt.TaskName);
		}

		void RequestAttachUID(Request req, in IGenericEvent evt) => RequestAttachUID(req, evt.GetUID(), in evt);

		void RequestAttachSrcDep(Request req, int srcdep, in IGenericEvent evt)
		{
			this.AssertValidRequest(req, in evt);
			AssertCritical(srcdep != jsonIntDefault);

			ThreadLocal tl = EnsureThreadLocal(in evt);
			tl.SetReqSrcDep(req, srcdep);
			tl.SetRecent(req, evt.TaskName);
		}

		void RequestAttachSrcDep(Request req, in IGenericEvent evt) => RequestAttachSrcDep(req, evt.GetSourceId(), in evt);

		void RequestAttachUID_SrcDep(Request req, in IGenericEvent evt)
		{
			this.AssertValidRequest(req, in evt);

			int srcdep = evt.GetSourceId();
			AssertCritical(srcdep != jsonIntDefault);

			ThreadLocal tl = EnsureThreadLocal(in evt);
			tl.SetReqSrcDep(req, srcdep);
			tl.SetReqUID(req, evt.GetUID());
			tl.SetRecent(req, evt.TaskName);
		}

		// Most events should invoke one of these three methods, with:
		// ID (in the event), ID + SrcDep, ID + Task Name to correlate

		Request RequestFromUID(UIDVal uID, in IGenericEvent evt, bool fOpen = true)
		{
			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return null;

			Request req = tl.ReqFromUID(uID);
			if (req != null)
			{
				AssertCritical(req.pid == evt.ProcessId && req.tid == evt.ThreadId);

				if (fOpen && req.Closed)
					req = null;
			}

			tl.SetRecent(req, evt.TaskName);

			return req;
		}

		Request RequestFromUID(in IGenericEvent evt, bool fOpen = true) => RequestFromUID(evt.GetUID(), in evt, fOpen);

		Request RequestFromSrcDep(int srcdep, in IGenericEvent evt)
		{
			AssertCritical(srcdep != jsonIntDefault);

			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return null;

			return tl.ReqFromSrcDep(srcdep);
		}

		Request RequestFromSrcDep(in IGenericEvent evt) => RequestFromSrcDep(evt.GetSourceId(), in evt);

		Request RequestFromUID_SrcDep(int srcdep, in IGenericEvent evt)
		{
			AssertCritical(srcdep != jsonIntDefault);

			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return null;

			Request req = tl.ReqFromSrcDep(srcdep);
			if (req != null)
			{
				AssertCritical(req.pid == evt.ProcessId && req.tid == evt.ThreadId);

				if (req.Closed)
					req = null;
			}

			UIDVal uid = evt.GetUID();

			if (req == null)
			{
				req = tl.ReqFromUID(uid);
				if (req != null)
					tl.SetReqSrcDep(req, srcdep);
			}
			else
			{
#if DEBUG
				Request reqT = tl.ReqFromUID(uid);
				AssertImportant(FImplies(reqT != null, reqT == req));
#endif // DEBUG
				tl.SetReqUID(req, uid);
			}

			tl.SetRecent(req, evt.TaskName);

			return req;
		}

		Request RequestFromUID_SrcDep(in IGenericEvent evt) => RequestFromUID_SrcDep(evt.GetSourceId(), in evt);

		public void StashStalledRequest(Request req, in IGenericEvent evt, string strTask)
		{
			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return;

			req.strTaskStash = strTask;
			tl.rgRequestStalled.Add(req);
		}

		public Request GetStalledRequestGroup(int hidGroup, in IGenericEvent evt, string strTask)
		{
			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return null;

			int iReq = tl.rgRequestStalled.FindIndex(r => r.hidGroup == hidGroup && strTask.Equals(r.strTaskStash));
			if (iReq < 0) return null;

			Request req = tl.rgRequestStalled[iReq];
			req.strTaskStash = null;
			tl.rgRequestStalled.RemoveAt(iReq);
			return req;
		}

		/**** Preconnect Requests ****/

		/*
			Find a Preconnect Request created or chosen by: PreconnectRequest()
			Look it up by URL and Session.
			(This is the best we've got in the case of Preconnect Requests.)
			Also, the Session must match, or pick up a recently created Request with all the right parameters, and the caller gives it this Session.
			session may be null.

			The key is a function of the Session (HTTP2 / HTTP3) or of the hashed group_id (HTTP1).
			Therefore the caller must set (or check) the Session or the hidGroup of the returned Request.

			strURL may or may not have a trailing '/'.
		*/
		Request _FindPreconnectRequest(string strURL, int prekey, in IGenericEvent evt)
		{
			AssertCritical(strURL != null);

			// Typically we will find first a speculative preconnect Request with: .PreKey==0,
			// then an active preconnect Request with: .PreKey=something

			// First find the Request where the Session is probably null (which may be our target).
			IDVal pid = evt.ProcessId;
			IDVal tid = evt.ThreadId;
			int iReq = this.FindLastIndex(r => r.IsPreconnect && r.pid == pid && r.tid == tid && r.URL.Equal2(strURL));
			if (iReq < 0) return null;

			Request req1 = this[iReq];
			int prekeyReq = req1.PreKey();
			if (prekeyReq == prekey) return req1;
			if (prekeyReq != 0) return null;
			if (iReq == 0) return req1;

			// Continue the search
			iReq = this.FindLastIndex(iReq-1, iReq/*count*/, r => r.IsPreconnect && r.pid == pid && r.tid == tid && r.URL.Equal2(strURL));
			if (iReq < 0) return req1; // req1.PreKey == 0

			Request req2 = this[iReq];
			if (req2.PreKey() == prekey) return req2;

			return req1; // req1.PreKey == 0
		}

		Request FindPreconnectRequest(string strURL, int prekey, in IGenericEvent evt)
		{
			Request req = _FindPreconnectRequest(strURL, prekey, in evt);

			// If there is start time (from an earlier PreconnectRequest) then advance the end time.
			if (req?.timeStampBeginJob.HasValue() ?? false)
			{
				TimestampUI timeStamp = evt.Timestamp.ToGraphable();
				AssertImportant(req.timeStampEndJob.HasMaxValue() || req.timeStampEndJob < timeStamp);
				req.timeStampEndJob = timeStamp;
			}

			return req;
		}

		/*
			HTTP_STREAM_JOB_CONTROLLER.Begin : "is_preconnect":true
			Find or create a preconnect placeholder Request.
			Many such events can refer to the same preconnect Request.
			Find it later via: FindPreconnectRequest()

			The Requests found or created here are "unused" because: Request.PreKey==0
			This unused Request gets picked up for use later when the key requested is different.
		*/
		Request PreconnectRequest(string strURL, in IGenericEvent evt, in Thread.ThreadTable threadTable)
		{
			AssertCritical(strURL != null);

			// If the (unused) Request is found, it was previously created here.

			Request req = this.FindPreconnectRequest(strURL, 0, in evt);

			if (req == null)
			{
				req = new Request(strURL, in evt)
				{
					method = Request.strPreconnect,
					priority = Priority.IDLE
				};

				this.Add(req);
			}
			else
			{
				req.timeRef = evt.Timestamp;
				req.stack = evt.Stack;
			}

			// This Request didn't get picked up for use before (via FindPreconnectRequest).
			// Speculatively assume that this time it will.
			// But later, skip 'gathering' this Request if IsSpeculative (it never got used).

			UIDVal uid = evt.GetUID();
			req.uidRequest = uid;
			this.RequestAttachUID(req, uid, in evt);

			if (!req.timeStampBeginJob.HasValue())
			{
				req.timeStampBeginJob = evt.Timestamp.ToGraphable();
				req.xlink.ReGetLink(evt.ThreadId, evt.Timestamp.ToGraphable(), in threadTable);
			}

			return req;
		}


		/**** Recent / Correlate ****/

		Request RequestFromUID_Correlate(in IGenericEvent evt, string strTask)
		{
			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return null;

			UIDVal uid = evt.GetUID();
			Request req = tl.ReqFromUID(uid);
			if (req == null)
			{
				if (!strTask.Equals(tl.strTaskRecent)) return null;

				req = tl.reqRecent;
				tl.SetReqUID(req, uid);
			}
			else
			{
				AssertImportant(tl.reqRecent == null || tl.reqRecent == req || !strTask.Equals(tl.strTaskRecent));
			}

			tl.SetRecent(req, evt.TaskName);

			return req;
		}

		Request RequestFromGroupId_Correlate(in IGenericEvent evt, int hidGroup, string strTask, string strTask2 = null)
		{
			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return null;

			Request req = tl.GetRecent(strTask);
			if (req == null && strTask2 != null)
				req = tl.GetRecent(strTask2);

			if (req == null)
				req = this.GetStalledRequestGroup(hidGroup, in evt, strTask);

			if (req != null && req.hidGroup != hidGroup && req.hidGroup != 0)
				req = null;

			return req;
		}


		void StashJSON(in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
			tl.strJSON = evt.GetParams();
			tl.SetRecent(default(Request), evt.TaskName);
		}

		string UnstashJSON(string strTask, in IGenericEvent evt)
		{
			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return null;

			string strJSON = tl.strJSON;
			tl.strJSON = null;

			string strTaskRecent = tl.strTaskRecent;

			tl.SetRecent(default(Request), evt.TaskName);

			if (!strTask.Equals(strTaskRecent))
				return null;

			return strJSON;
		}

		/**** SESSIONS ****/

		Session SessionFromUID(UIDVal id, in IGenericEvent evt, bool fOpen = true)
		{
			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return null;

			Session session = tl.sessionFromUID[id];
			if (session == null) return null;

			AssertCritical(session.pid == evt.ProcessId && session.tid == evt.ThreadId);

			if (fOpen && session.Closed) return null;

			return session;
		}

		Session SessionFromUID(in IGenericEvent evt, bool fOpen = true) => SessionFromUID(evt.GetUID(), in evt, fOpen);

		Session SessionFromSrcDep(int srcdep, in IGenericEvent evt)
		{
			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return null;

			AssertCritical(srcdep != jsonIntDefault);

			Session session = tl.sessionFromSrcDep[srcdep];
			if (session == null) return null;

			AssertCritical(session.pid == evt.ProcessId && session.tid == evt.ThreadId);
			AssertImportant(!session.Closed);

			UIDVal id = evt.GetUID();
			tl.sessionFromUID[id] = session;

			return session;
		}

		Session SessionFromSrcDep(in IGenericEvent evt) => SessionFromSrcDep(evt.GetSourceId(), in evt);

		Session SessionFromRecent(string taskRecent, in IGenericEvent evt)
		{
			ThreadLocal tl = ThreadLocalFromEvt(in evt);
			if (tl == default) return null;

			if (tl.strTaskRecent != taskRecent) return null;

			return tl.sessionRecent;
		}

		void SessionAddUID(Session session, UIDVal uID, in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
			tl.sessionFromUID[uID] = session;
		}

		void SessionAddUID(Session session, in IGenericEvent evt) => SessionAddUID(session, evt.GetUID(), in evt);

		void SessionAttachUID(Session session, UIDVal uID, in IGenericEvent evt)
		{
			SessionAddUID(session, uID, in evt);
			AssertImportant(session.uidVal == 0 || session.uidVal == uID);
			session.uidVal = uID;
		}

		void SessionAttachUID(Session session, in IGenericEvent evt) => SessionAttachUID(session, evt.GetUID(), in evt);

		void SessionAttachSrcDep(Session session, int srcdep, in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
			AssertCritical(srcdep != jsonIntDefault);
			AssertImportant(session.srcdep == 0 || session.srcdep == srcdep);
			session.srcdep = srcdep;
			tl.sessionFromSrcDep[srcdep] = session;
			tl.SetRecent(session, evt.TaskName);
		}

		void SessionAddSrcDep(Session session, int srcdep, in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
			AssertCritical(srcdep != jsonIntDefault);
			tl.sessionFromSrcDep[srcdep] = session;
		}

		void SessionAddSrcDep(Session session, in IGenericEvent evt) => SessionAddSrcDep(session, evt.GetSourceId(), in evt);

		void SessionAddUID_SrcDep(Session session, int srcdep, UIDVal uid, in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
#if DEBUG
			Session sessionT = tl.sessionFromSrcDep[srcdep];

			// sessionT must be null or the same or closed (reused srcdep!?) or error (retrying).
			AssertImportant(sessionT == null || sessionT == session || sessionT.Closed || sessionT.iError != 0);
			AssertCritical(session != null);
			AssertCritical(session.pid == evt.ProcessId && session.tid == evt.ThreadId);
			AssertCritical(srcdep != jsonIntDefault);
#endif // DEBUG
			tl.sessionFromSrcDep[srcdep] = session;
			tl.sessionFromUID[uid] = session;
			tl.SetRecent(session, evt.TaskName);
		}

		void SessionAddUID_SrcDep(Session session, in IGenericEvent evt) => SessionAddUID_SrcDep(session, evt.GetSourceId(), evt.GetUID(), in evt);

		void SessionAttachUID_SrcDep(Session session, int srcdep, in IGenericEvent evt)
		{
			UIDVal uID = evt.GetUID();
			SessionAddUID_SrcDep(session, srcdep, uID, in evt);
			AssertImportant(session.srcdep == 0 || session.srcdep == srcdep);
			session.srcdep = srcdep;
			AssertImportant(session.uidVal == 0 || session.uidVal == uID);
			session.uidVal = uID;
		}

		void SessionAttachUID_SrcDep(Session session, in IGenericEvent evt) => 	SessionAttachUID_SrcDep(session, evt.GetSourceId(), in evt);


		/**** ResolverManager ****/

		void ResolverManagerAttach(ResolverManager resolver, in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
			UIDVal uid = evt.GetUID();
			tl.managerFromUID[uid] = resolver;
		}

		ResolverManager ResolverManagerAttach(UIDVal uid, in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
			ResolverManager manager = tl.managerFromUID[uid];
			if (manager != null)
			{
				UIDVal uid2 = evt.GetUID();
				tl.managerFromUID[uid2] = manager;
			}
			return manager;
		}

		ResolverManager ResolverManagerAttachSrcDep(int srcdep, in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
			UIDVal uid = evt.GetUID();
			ResolverManager manager = tl.managerFromUID[uid];
			if (manager != null)
				tl.managerFromSrcDep[srcdep] = manager;

			return manager;
		}

		ResolverManager GetResolverManagerFromSrcDep(int srcdep, in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
			return tl.managerFromSrcDep[srcdep];
		}

		ResolverManager GetResolverManager(UIDVal uid, in IGenericEvent evt)
		{
			ThreadLocal tl = EnsureThreadLocal(in evt);
			return tl.managerFromUID[uid];
		}

		ResolverManager GetResolverManager(in IGenericEvent evt) => GetResolverManager(evt.GetUID(), in evt);


		/**** DNS ****/

		// Is the given Source Dependency value related to a DNS Transaction?

		bool IsDNSSrcDep(in IGenericEvent evt, int srcdep) => ThreadLocalFromEvt(in evt)?.IsDNS.Contains(srcdep) ?? false;

		bool AddDNSSrcDep(in IGenericEvent evt, int srcdep) => ThreadLocalFromEvt(in evt)?.IsDNS.Add(srcdep) ?? false;

		/**** GC ****/

		void GarbageCollect(in IGenericEvent evt)
		{
			ThreadLocal tl = this.ThreadLocalFromEvt(in evt);
			tl?.GarbageCollect();
		}

		/**** STRING ARRAYS FOR ParseSimpleJsonString ****/

		public static readonly string[] rgstrSourceId =
		{
			"/source_dependency/id"  // "source_dependency":{"id":123} (number)
		};

		public static readonly string[] rgstrUrl =
		{
			"url"
		};

		static readonly string[] rgstrURL_Method =
		{
			rgstrUrl[0], // url
			"method"
		};

		static readonly string[] rgstrPriority =
		{
			"priority"
		};

		static readonly string[] rgstrURL_Priority =
		{
			rgstrUrl[0],     // url
			rgstrPriority[0] // priority
		};

		static readonly string[] rgstrDestination_SourceId =
		{
			"destination",   // (domain)
			rgstrSourceId[0] // "source_dependency":{"id":123}
		};

		static readonly string[] rgstrSourceId_Type_Destination_Quic =
		{
			rgstrSourceId[0], // "source_dependency":{"id":123}
			"type",
			rgstrDestination_SourceId[0], // "destination" (domain)
			"using_quic"   // (bool)
		};

		static readonly string[] rgstrAddress =
		{
			"address"
		};

		static readonly string[] rgstrAddress_Error =
		{
			rgstrAddress[0],      // "address"
			Util.rgstrNetError[0] // "net_error"
		};

		static readonly string[] rgstrSourceId_Address =
		{
			rgstrSourceId[0], // "source_dependency":{"id":123}
			rgstrAddress[0],  // "address"
		};

		static readonly string[] rgstrLocal_Remote =
		{
			"local_address",
			"remote_address"
		};

		static readonly string[] rgstrCanon_Endpoint =
		{
			"/results/canonical_names",
			"/results/ip_endpoints"
		};

		static readonly string[] rgstrGroupId =
		{
			"group_id"
		};

		static readonly string[] rgstrHost =
		{
			"host"
		};

		static readonly string[] rgstrHost_Key =
		{
			rgstrHost[0], // host
			"network_anonymization_key"
		};

		static readonly string[] rgstrHost_Key_Port =
		{
			rgstrHost_Key[0], // host
			rgstrHost_Key[1], // network_anonymization_key
			"port"
		};

		static readonly string[] rgstrHost_Key_Port_SourceId =
		{
			rgstrHost_Key_Port[0], // "host"
			rgstrHost_Key_Port[1], // "network_anonymization_key"
			rgstrHost_Key_Port[2], // "port"
			rgstrSourceId[0]       // "source_dependency":{"id":123}
		};

		static readonly string[] rgstrSize =
		{
			"size"
		};

		static readonly string[] rgstrPeer_Self =
		{
			"peer_address", // "remote address"
			"self_address"  // "local address"
		};

		static readonly string[] rgstrStreamId =
		{
			"stream_id"
		};

		static readonly string[] rgstrStreamId_Payload =
		{
			rgstrStreamId[0], // "stream_id"
			"payload_length"
		};

		static readonly string[] rgstrStreamId_Size =
		{
			rgstrStreamId[0], // "stream_id"
			rgstrSize[0]      // "size"
		};

		public static readonly string[] rgstrStreamId_QStreamId_Headers =
		{
			rgstrStreamId[0], // "stream_id"
			"quic_stream_id",
			"headers",
		};

		public static readonly string[] rgstrStreamId_Headers =
		{
			rgstrStreamId[0], // "stream_id"
			"headers"
		};

		public static readonly string[] rgstrProto =
		{
			"proto"
		};

		public static readonly string[] rgstrProto2 =
		{
			"next_proto"
		};

		public static readonly string[] rgstrSize_Chunked_Error =
		{
			"total_size",
			"is_chunked",
			Util.rgstrNetError[0] // "net_error"
		};

		public static readonly string[] rgstrBytes =
		{
			"byte_count"
		};

		public static readonly string[] rgstrUrl_Preconnect =
		{
			rgstrUrl[0],      // "url"
			"is_preconnect"   // boolean
		};

		public static readonly string[] rgstrOpcode_Payload =
		{
			"opcode",
			"payload_length"
		};

#if DEBUG
		IDVal s_tidUnique;

		long nsStartDB = 0_000000000;
#endif // DEBUG

		const int FAILED = -2;
		const int QUIC_NETWORK_IDLE_TIMEOUT = 25;
		const int ERR_HTTP2_SERVER_REFUSED_STREAM = -351;

		const int keyword_netlog = 0x80;

		public static Guid[] rgGuid =
		{
			new Guid("{3A5F2396-5C8F-4F1F-9B67-6CCA6C990E61}"), // Microsoft.MSEdgeStable
			new Guid("{BD089BAA-4E52-4794-A887-9E96868570D2}"), // Microsoft.MSEdgeBeta
			new Guid("{C56B8664-45C5-4E65-B3C7-A8D6BD3F2E67}"), // Microsoft.MSEdgeCanary
			new Guid("{D30B5C9F-B58F-4DC9-AFAF-134405D72107}"), // Microsoft.MSEdgeDev
			new Guid("{E16EC3D2-BB0F-4E8F-BDB8-DE0BEA82DC3D}"), // Microsoft.MSEdgeWebView2
			new Guid("{d2d578d9-2936-45b6-a09f-30e32715f42d}")  // Google.Chrome
		};


		/*
			For the ETW correlation schema table, see:
			https://github.com/microsoft/MSO-Scripts/issues/50
		*/
		public void PreDispatch(in IGenericEvent evt)
		{
			if ((evt.Keyword & keyword_netlog) == 0) return;
			if (this.unhandled.Contains(evt.TaskName)) return;
#if DEBUG
			// Confirm that Dispatch remains single-threaded.
			IDVal tidCurrent = Environment.CurrentManagedThreadId;
			if (s_tidUnique == 0)
				s_tidUnique = tidCurrent;
			else
				AssertCritical(s_tidUnique == tidCurrent);

			if (evt.Timestamp.Nanoseconds < nsStartDB) return; // process from a certain timestamp
#endif // DEBUG

			ReadOnlySpan<char> task = evt.TaskName.AsSpan();

			// span is more efficient than string here:
			if (task.StartsWith("HOST_"))        Dispatch_Host(in evt);
			else if (task.StartsWith("HTTP_"))   Dispatch_Http(in evt);
			else if (task.StartsWith("HTTP2_"))  Dispatch_Http2(in evt);
			else if (task.StartsWith("HTTP3_"))  Dispatch_Http3(in evt);
			else if (task.StartsWith("SOCKET_")) Dispatch_Socket(in evt);
			else if (task.StartsWith("TCP_"))    Dispatch_Tcp(in evt);
			else if (task.StartsWith("URL_"))    Dispatch_Url(in evt);
			else if (task.Contains("QUIC_"))     Dispatch_Quic(in evt);
			else if (task.Contains("CONNECT_"))  Dispatch_Connect(in evt); 
			else                                 Dispatch_Misc(in evt);
		} // PreDispatch

		/*
			evt.TaskName must begin with "HOST_"
		*/
		void Dispatch_Host(in IGenericEvent evt)
		{
			UIDVal uID;
			Request req;
			ResolverManager resolver;
			JsonElement[] rgje;

			AssertCritical(evt.TaskName.StartsWith("HOST_"));

			switch (evt.TaskName)
			{
			// ID -> HOST_RESOLVER_MANAGER_CREATE_JOB
			// "host":"<host>", "network_anonymization_key":"<key>"
			case "HOST_RESOLVER_MANAGER_REQUEST":
				if (!evt.IsBeginPhase()) break;
				if (!evt.TestResolverSourceType()) break;

				rgje = evt.ParseSimpleJsonString(rgstrHost_Key);
				if (rgje == null) break;

				resolver = new ResolverManager
				{
					host = rgje[0].GetString(),
					anon_key = rgje[1].GetString()
				};

				this.ResolverManagerAttach(resolver, in evt);

				break;

			// ID -> HOST_RESOLVER_MANAGER_REQUEST
			// NOTE: Relay this ID to: HOST_RESOLVER_MANAGER_JOB
			case "HOST_RESOLVER_MANAGER_CREATE_JOB":
				AssertImportant(evt.IsInstantPhase());

				this.SetRecentUID(in evt);

				break;

			// ID -> HOST_RESOLVER_MANAGER_JOB_REQUEST_ATTACH, HOST_RESOLVER_MANAGER_JOB_STARTED
			case "HOST_RESOLVER_MANAGER_JOB":
				if (!evt.IsBeginPhase()) break;
				AssertImportant(evt.CheckSourceType("HOST_RESOLVER_IMPL_JOB"));

				uID = this.GetRecentUID("HOST_RESOLVER_MANAGER_CREATE_JOB", in evt);
				AssertImportant(uID != 0);
				if (uID == 0) break;

				resolver = this.ResolverManagerAttach(uID, in evt);

				break;

			// SSL_CONNECT_JOB: ID -> CONNECT_JOB, SOCKET_POOL_CONNECT_JOB_CREATED, SSL_CONNECT_JOB_CONNECT, TRANSPORT_CONNECT_JOB_CONNECT, HOST_RESOLVER_MANAGER_REQUEST
			// TRANSPORT_CONNECT_JOB: ID -> CONNECT_JOB, CONNECT_JOB_SET_SOCKET
			// NETWORK_SERVICE_HOST_RESOLVER: ID -> HOST_RESOLVER_MANAGER_REQUEST
			// QUIC_SESSION_POOL_DIRECT_JOB: ID -> QUIC_SESSION_POOL_JOB, QUIC_SESSION_POOL_JOB_BOUND_TO, HOST_RESOLVER_MANAGER_REQUEST
			// "results":{"aliases":["<domain name>",...],"canonical_names":["<domain name>",...],"ip_endoints":[{"endpoint_address":"###.###.###.###","endpoint_port":0},...]}
			// NOTE: This event can connect to a Request via these two events: CONNECT_JOB, HOST_RESOLVER_MANAGER_REQUEST
			case "HOST_RESOLVER_MANAGER_CACHE_HIT":
				AssertImportant(evt.IsInstantPhase());
				if (!evt.TestResolverSourceType()) break;

				rgje = evt.ParseSimpleJsonString(rgstrCanon_Endpoint);
				if (rgje == null) break;

				resolver = this.GetResolverManager(in evt);
				AssertImportant(resolver != null);
				if (resolver == null) break;

				// There are usually two copies of this event. Ignore the 2nd.
				if (resolver.rgstrAddress != null) break;

				resolver.rgstrAddress = rgje[1].MyGetStringArray("endpoint_address"); // exclude "endpoint_port":0
				resolver.rgstrCanon = rgje[0].MyGetStringArray();
				AssertImportant(resolver.rgstrCanon.Length == 1); // else what?

				req = this.RequestFromUID(in evt);
				if (req == null) break;

				// There may well be multiple IP Addresses. Take the first.
				req.ipAddr = (resolver.rgstrAddress?.Length > 0) ? resolver.rgstrAddress[0] : string.Empty;

				req.Canon = (resolver.rgstrCanon?.Length > 0) ? resolver.rgstrCanon[0] : string.Empty;

				break;

			// ID -> HOST_RESOLVER_MANAGER_JOB, HOST_RESOLVER_DNS_TASK, DNS_TRANSACTION
			// "results":[{"domain_name":"<name>","endpoints":["address":"###.###.###.###","port":0},...],"type":"data"}, ...]
			// NOTE: Added to the DNS list, not immediately attached to a ResolverManager, etc.
			case "HOST_RESOLVER_DNS_TASK_EXTRACTION_RESULTS":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HOST_RESOLVER_IMPL_JOB"));

				DNSInfo.ResolvedDNS rdns = DNSInfo.ParseHostResolveDNS(evt.GetParams());

				if (rdns != null)
					this.allTables.dnsTable.AddServerAndAddress(rdns.Domain, rdns.Alias, rdns.rgAddress);

				break;

			default:
				// Remember this event as unhandled.
				this.unhandled.Add(evt.TaskName);
				break;
			} // switch evt.TaskName
		} // Dispatch_Host

		/*
			evt.TaskName must begin with "HTTP_"
		*/
		void Dispatch_Http(in IGenericEvent evt)
		{
			int srcdep;
			string strJSON;
			Request req;
			Socket soc;
			Session session;
			JsonElement[] rgje;

			AssertCritical(evt.TaskName.StartsWith("HTTP_"));

			switch (evt.TaskName)
			{
			// ID -> HTTP_STREAM_JOB_INIT_CONNECTION, HTTP_STREAM_JOB_BOUND_TO_REQUEST, etc.
			// src_dep: HTTP_STREAM_JOB_CONTROLLER_BOUND, and the other HTTP_STREAM_JOB for "use_quic":true/false
			// "destination":<host>, "use_quic":true/false
			case "HTTP_STREAM_JOB":
				if (!evt.IsBeginPhase())
					break;

				AssertImportant(evt.CheckSourceType(evt.TaskName));

				rgje = evt.ParseSimpleJsonString(rgstrSourceId_Type_Destination_Quic);
				if (rgje == null) break;

				srcdep = rgje[0].MyGetNumber();
				AssertCritical(srcdep > 0);

				req = this.RequestFromUID_SrcDep(srcdep, in evt);
				AssertImportant(req != null);
				if (req == null) break;

				bool fQuic = rgje[3].MyGetBool();
				req.SetStreamUID(evt.GetUID(), fQuic);
#if DEBUG
				string strType = rgje[1].MyGetString();

				if (fQuic)
					AssertImportant(strType.Equals("dns_alpn_h3") || strType.Equals("alternative")); // else what?
				else
					AssertImportant(strType.Equals("main"));

				AssertImportant(req.Type == StreamType.Unknown);
#endif // DEBUG
				// Strip "https://"
				Uri uri = rgje[2].MyGetString().CreateURI();
				if (uri != null)
				{
					AssertImportant(req.Domain == null || req.Domain == uri.Host);
					req.Domain = uri.Host;
				}

				break;

			// ID -> HTTP_STREAM_JOB_CONTROLLER
			// is_preconnect:bool, url:string
			// NOTE: Preconnect Stream Jobs begin here, in which case we create a placeholder Request.
			case "HTTP_STREAM_JOB_CONTROLLER":
				if (!evt.IsBeginPhase()) break;

				AssertImportant(evt.CheckSourceType(evt.TaskName));

				rgje = evt.ParseSimpleJsonString(rgstrUrl_Preconnect);
				if (rgje == null) break;

				if (rgje[1].MyGetBool()) // is_preconnect:true
				{
					// Find or synthesize a placeholder Request for preconnect activity.
					req = this.PreconnectRequest(rgje[0].MyGetString(), in evt, in this.allTables.threadTable);
				}
				else // is_preconnect:false
				{
					// This event is followed by: HTTP_STREAM_REQUEST and HTTP_STREAM_JOB_CONTROLLER_BOUND/URL_REQUEST
					// Stash the JSON params to link with the Request.
					this.StashJSON(in evt);
				}

				break;

			// ID -> REQUEST_ALIVE, URL_REQUEST_START_JOB, HTTP_STREAM_REQUEST_BOUND_TO_JOB  OR  HTTP_STREAM_REQUEST_STARTED_JOB, HTTP_STREAM_JOB_BOUND_TO_REQUEST
			// source_type: URL_REQUEST or HTTP_STREAM_JOB_CONTROLLER
			case "HTTP_STREAM_JOB_CONTROLLER_BOUND":
				AssertImportant(evt.IsInstantPhase());

				if (evt.CheckSourceType("URL_REQUEST"))
				{
					// This is usually unused, but we have to grab it now.
					strJSON = this.UnstashJSON("HTTP_STREAM_JOB_CONTROLLER", in evt);

					// Assign the srcdep given the event's ID.
					req = this.RequestFromUID_SrcDep(in evt);
					if (req != null) break; // success

					// rare fallback: synthesize a Request

					rgje = ParseSimpleJsonString(strJSON, rgstrUrl);
					if (rgje == null) break;

					string strURL = rgje[0].MyGetString();

					req = new Request(strURL, in evt)
					{
						method = "restored"
					};

					req.xlink.GetLink(evt.ThreadId, evt.Timestamp.ToGraphable(), in this.allTables.threadTable);

					this.Add(req);
					this.RequestAttachUID_SrcDep(req, in evt);
				}
				else
				{
					AssertImportant(evt.CheckSourceType("HTTP_STREAM_JOB_CONTROLLER"));

					// Adjacent tasks with the same name, different source type.
					req = this.RequestFromUID_Correlate(in evt, evt.TaskName);

					if (req != null)
						this.RequestAttachSrcDep(req, in evt);
				}
				break;

			// ID -> HTTP_STREAM_JOB_CONTROLLER_BOUND, HTTP_STREAM_JOB_BOUND_TO_REQUEST
			// srcdep -> HTTP_STREAM_REQUEST_BOUND_TO_JOB, QUIC_SESSION_POOL_ATTACH_HTTP_STREAM_JOB_TO_EXISTING_SESSION
			case "HTTP_STREAM_REQUEST_STARTED_JOB":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP_STREAM_JOB_CONTROLLER"));

				// null when the previous HTTP_STREAM_JOB event, params field, type property is not "main"
				req = this.RequestFromUID_Correlate(in evt, "HTTP_STREAM_JOB");
				if (req == null) break;

				this.RequestAttachSrcDep(req, in evt);

				break;

			/*
				Adjacent Events:
				HTTP_STREAM_REQUEST_PROTO / HTTP_STREAM_JOB (non-QUIC-only, not always available)
				HTTP_STREAM_REQUEST_BOUND_TO_JOB / URL_REQUEST     / srcdep -> HTTP_STREAM_REQUEST_STARTED_JOB, SOCKET_IN_USE, HTTP2_SESSION_SEND_HEADERS
				HTTP_STREAM_JOB_BOUND_TO_REQUEST / HTTP_STREAM_JOB / srcdep -> HTTP_STREAM_JOB_CONTROLLER_BOUND x N
				HTTP_STREAM_JOB_BOUND_TO_REQUEST / HTTP_STREAM_JOB_CONTROLLER / "
				HTTP_STREAM_JOB_ORPHANED         / HTTP_STREAM_JOB (QUIC-only)
				A 'losing' QUIC connection can be 'orphaned' and possibly reused. A TCP connection will be canceled rather than orphaned.)
			*/

			// ID -> REQUEST_ALIVE
			// SrcDep -> HTTP_STREAM_REQUEST_STARTED_JOB, QUIC_SESSION_POOL_ATTACH_HTTP_STREAM_JOB_TO_EXISTING_SESSION, SOCKET_IN_USE
			case "HTTP_STREAM_REQUEST_BOUND_TO_JOB":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				// Associate the Request, event ID, source_dependency id
				req = this.RequestFromUID_SrcDep(in evt);
				if (req == null) break;

				soc = this.SocketFromSrcDep(in evt);
				if (soc == null) break;

				// If soc==null then consider a placeholder Session when we know the type in: HTTP_STREAM_JOB_BOUND_TO_REQUEST
				// This will attach a placeholder Session (with Socket) only if it appears to be needed.
				if (req.FAttachPlaceholderSessionAndStream(soc, in evt))
				{
					this.sessionTable.Add(req.Session);
					this.SessionAttachUID(req.Session, in evt); // sets: session.uidVal
				}

				break;

			// ID -> HTTP_STREAM_REQUEST_STARTED_JOB, HTTP_STREAM_JOB_CONTROLLER_BOUND
			// srcdep -> HTTP_STREAM_JOB_CONTROLLER_BOUND
			// NOTE: This event (when source_type==HTTP_STREAM_JOB) determines whether QUIC or HTTP2 won the race of the HTTP Stream Jobs.
			case "HTTP_STREAM_JOB_BOUND_TO_REQUEST":
				AssertImportant(evt.IsInstantPhase());

				req = this.RequestFromUID_SrcDep(in evt);
				AssertImportant(req != null);
				if (req == null) break;

				if (evt.CheckSourceType("HTTP_STREAM_JOB"))
				{
					UIDVal uid = evt.GetUID();

					// This event links back to HTTP_STREAM_JOB & HTTP_STREAM_JOB_INIT_CONNECTION
					// There may have been two HTTP_STREAM_JOB events, one QUIC, the other not.
					req.SetStreamType(uid);

					session = this.SessionFromUID(uid, in evt);
					if (session != null)
					{
						AssertImportant(req.Type == session.Type);

						if (req.Session == null)
							req.SessionSet = session;
						else
							AssertImportant(req.Session == session);

						if (req.Type == StreamType.QUIC)
							session.AttachQUIC(req);
#if DEBUG
						Session sessionT = req.stream?.request?.Session;
						AssertImportant(sessionT == null || sessionT == session);
#endif // DEBUG
					}
					else if (req.Type == StreamType.HTTP1)
					{
						// The Socket is missing, but fill in with a placeholder Session and Stream.
						if (req.FAttachPlaceholderSessionAndStream(StreamType.HTTP1, in evt))
						{
							session = req.SessionOther; // HTTP1 Session
							this.sessionTable.Add(session);
							this.SessionAttachUID(session, uid, in evt); // sets: session.uidVal
						}
					}
				}
				// Else this event links back to HTTP_STREAM_JOB_CONTROLLER/_BOUND & HTTP_STREAM_REQUEST_STARTED_JOB
				break;

			// ID -> HTTP_STREAM_JOB
			// NOTE: Indicates whether the non-QUIC HTTP Stream Job is HTTP/2 (common) or HTTP/1.1 (rare)
			// NOTE: Followed by: HTTP_STREAM_REQUEST_BOUND_TO_JOB
			case "HTTP_STREAM_REQUEST_PROTO":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP_STREAM_JOB"));

				rgje = evt.ParseSimpleJsonString(rgstrProto);
				if (rgje == null) break;

				string strProto = rgje[0].MyGetString();
				AssertImportant(strProto.Equals("h2") || strProto.Equals("http/1.1"));

				StreamType type = (strProto.Equals("h2")) ? StreamType.HTTP2 : StreamType.HTTP1;

				req = this.RequestFromUID(in evt);
				AssertImportant(req != null);
				if (req != null)
				{
					AssertImportant(req.uidTCP == evt.GetUID());
					req.TypeTCP = type;
				}
#if DEBUG
				soc = this.SocketFromUID(in evt);
				if (soc != null)
					AssertImportant(soc.Type == req.TypeTCP);
#endif // DEBUG

				break;

			// ID -> HTTP_STREAM_JOB, etc.
			// SrcDep -> HTTP2_SESSION_POOL_IMPORTED_SESSION_FROM_SOCKET, HTTP2_SESSION_POOL_FOUND_EXISTING_SESSION
			case "HTTP_STREAM_JOB_HTTP2_SESSION_AVAILABLE":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP_STREAM_JOB"));

				req = this.RequestFromUID(in evt);
				AssertImportant(req != null);
				if (req == null) break;

				AssertImportant(req.TypeTCP == StreamType.HTTP2 || req.TypeTCP == StreamType.Unknown);
				req.TypeTCP = StreamType.HTTP2; // provisional

				if (req.SessionHTTP2 != null) break;

				srcdep = evt.GetSourceId();
				session = this.SessionFromSrcDep(srcdep, in evt);
				if (session == null)
				{
					session = req.NewPlaceholderSession(StreamType.HTTP2, in evt);
					this.sessionTable.Add(session);
					this.SessionAttachSrcDep(session, srcdep, in evt);
				}

				req.SessionHTTP2 = session;

				break;

			// ID -> URL_REQUEST_START_JOB, etc.
			// NOTE: A Chromium optimization did not work out. Disconnect the Request and abandon the Stream.
			// NOTE: The underlying Session/Socket is probably still usable.
			case "HTTP_TRANSACTION_RESTART_MISDIRECTED_REQUEST":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				req = this.RequestFromUID(in evt);
				if (req == null) break;

				if (req.FAttachedToStream)
				{
					req.stream.iError = FAILED; // -2 = generic failure
					req.stream.Abandon(true); // Hard abandon the Stream since the Session will retry.

					AssertImportant(!req.FAttachedToStream);
				}

				// Other Requests may refer to those Sessions. This one no longer does.
				AssertImportant(req.Type != StreamType.HTTP1);
				req.SessionReset();

				break;

			// ID -> URL_REQUEST_START_JOB, etc.
			// NOTE: Disconnect and abandon the broken Stream.
			// NOTE: The underlying Session/Socket is probably broken as well.
			case "HTTP_TRANSACTION_RESTART_AFTER_ERROR":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				req = this.RequestFromUID(in evt);
				if (req == null) break;

				// This Request error will later reset in: Session.Attach
				int iError = evt.GetNetError(); // commonly -351 = ERR_HTTP2_SERVER_REFUSED_STREAM
				AssertImportant(FImplies(iError == ERR_HTTP2_SERVER_REFUSED_STREAM, req.Type == StreamType.HTTP2));

				req.iError = iError;
				if (req.Session != null)
				{
					req.Session.iError = iError;
					if (req.Session.socket != null)
						req.Session.socket.iError = iError;
				}
#if DEBUG
				if (req.SocketTCP != null)
				{
					req.SocketTCP.iError = iError;
					req.SocketTCP = null;
				}
#endif // DEBUG
				if (req.FAttachedToStream)
				{
					req.stream.iError = iError;
					req.stream.Abandon(false); // Hard abandon the Stream only if no data transferred.

					AssertImportant(!req.FAttachedToStream);
				}

				// Other Requests may refer to those Sessions. This one no longer does.
				req.SessionReset();
	
				break;

			// ID -> REQUEST_ALIVE, HTTP_STREAM_REQUEST_BOUND_TO_JOB, etc.
			// quic_stream_id:#, "headers":<string>
			// source_type: URL_REQUEST
			// NOTE: Links to a URL_REQUEST, while containing the (redundant) parameters of a Stream.
			// NOTE: This UID gets picked up by QUIC_CHROMIUM_CLIENT_STREAM_SEND_REQUEST_HEADERS
			case "HTTP_TRANSACTION_QUIC_SEND_REQUEST_HEADERS": // SIMILAR, with "quic_stream_id":#
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				this.SetRecentUID(in evt);

				break;

			// ID -> REQUEST_ALIVE, etc.
			// SrcDep -> QUIC_SESSION_CREATED
			case "HTTP_STREAM_REQUEST_BOUND_TO_QUIC_SESSION":	
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				req = this.RequestFromUID(in evt);
				AssertImportant(req != null);
				if (req == null) break;

				if (req.SessionQUIC != null) break;

				session = this.SessionFromSrcDep(in evt);
				AssertInfo(session != null);
				if (session == null)
				{
					// The QUIC_SESSION event must have happened before tracing started.
					session = req.NewPlaceholderSession(StreamType.QUIC, in evt);
					this.sessionTable.Add(session);
					this.SessionAttachUID_SrcDep(session, in evt); // sets: session.uidVal/.srcdep
				}

				session.AttachQUIC(req); // add to the Request Pending list

				break;

			// ID -> REQUEST_ALIVE, etc.
			// NOTE: Get the HTTP Status from the headers string.
			case "HTTP_TRANSACTION_READ_RESPONSE_HEADERS":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				req = this.RequestFromUID(in evt);
				AssertInfo(req != null);
				if (req == null) break;

				AssertImportant(req.stream != null);
				if (req.stream == null) break;

				string strHTTPStatus = evt.GetParams().GetStatusJSON();
				AssertImportant(string.IsNullOrEmpty(req.stream.strHTTPStatus) || strHTTPStatus.StartsWith(req.stream.strHTTPStatus));
				req.stream.strHTTPStatus = strHTTPStatus;

				break;

			// ID -> REQUEST_ALIVE, URL_REQUEST_START_JOB, etc.
			// NOTE: Reading data (such as an image file) from the filesystem cache rather than via network transactions.
			case "HTTP_CACHE_READ_DATA":
				if (!evt.IsBeginPhase()) break;
				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				req = this.RequestFromUID(in evt, false); // Request might be closed
				AssertImportant(req != null);
				if (req == null) break;

				// If it has gotten this far with StreamType.Unknown and no Session
				// then it has done no network transaction, and is of type CACHE.

				if (!(req.Type == StreamType.CACHE || req.Type == StreamType.Unknown)) break;

				req.Type = StreamType.CACHE;

				if (req.FAttachPlaceholderSessionAndStream(StreamType.CACHE, in evt))
				{
					session = req.SessionOther; // Session with CACHE type
					this.sessionTable.Add(session);
					this.SessionAttachUID(session, in evt); // sets: session.uidVal
				}

				break;

			default:
				// Remember this event as unhandled.
				this.unhandled.Add(evt.TaskName);
				break;
			} // switch evt.TaskName
		} // Dispatch_Http

		/*
			evt.TaskName must begin with "HTTP2_"
		*/
		void Dispatch_Http2(in IGenericEvent evt)
		{
			uint cb;
			int srcdep;
			Request req;
			Socket soc;
			Session session;
			Session.Stream stream;
			ResolverManager resolver;
			JsonElement[] rgje;

			AssertCritical(evt.TaskName.StartsWith("HTTP2_"));

			switch (evt.TaskName)
			{
			// ID -> first; HTTP2_SESSION_INITIALIZED, HTTP2_SESSION_SEND/RECV_HEADERS, HTTP2_SESSION_SEND/RECV_DATA, HTTP2_SESSION_CLOSE, HTTP2_SESSION_POOL_REMOVE_SESSION
			// "host":"<domain>:<port>"
			// source_type: HTTP2_SESSION
			case "HTTP2_SESSION":
				if (evt.IsEndPhase())
				{
					session = this.SessionFromUID(in evt, false);
					AssertInfo(session != null);
					session?.Shutdown();

					break;
				}

				AssertImportant(evt.CheckSourceType(evt.TaskName));

				rgje = evt.ParseSimpleJsonString(rgstrHost);
				if (rgje == null) break;

				string strHost = rgje[0].ToString().GetHostAndPort(out ushort port);
				if (strHost == null) break;

				session = new Session(StreamType.HTTP2, in evt)
				{
					domain = strHost,
					port = port
				};

				this.sessionTable.Add(session);
				this.SessionAttachUID(session, in evt); // sets: session.uidVal

				break;

			// ID -> HTTP2_SESSION, etc.
			// SrcDep -> TRANSPORT_CONNECT_JOB_CONNECT_ATTEMPT, CONNECT_JOB_SET_SOCKET, SOCKET_POOL_BOUND_TO_SOCKET
			// source_type: HTTP2_SESSION
			// NOTE: Links to the TCP socket.
			case "HTTP2_SESSION_INITIALIZED":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP2_SESSION"));

				session = this.SessionFromUID(in evt);
				AssertImportant(session != null);
				if (session == null) break;

				srcdep = evt.GetSourceId();

				soc = this.SocketFromSrcDep(srcdep, in evt);
				AssertImportant(soc != null);
				if (soc != null)
				{
					session.Attach(soc);

					// HTTP2_SESSION_POOL_IMPORTED_SESSION_FROM_SOCKET will attach via this event Id.
					AssertImportant(soc.uidBound != 0);
					if (soc.uidBound != 0)
						this.SessionAddUID(session, soc.uidBound, in evt);
				}

				resolver = this.GetResolverManagerFromSrcDep(srcdep, in evt);
				session.resolver = resolver;

				// Set session.srcdep and recent Session for: HTTP2_SESSION_POOL_IMPORTED_SESSION_FROM_SOCKET
				this.SessionAttachSrcDep(session, srcdep, in evt);

				break;

			// ID -> HTTP2_SESSION/_INITIALIZED
			// source_type: HTTP2_SESSION
			// "net_error":#
			case "HTTP2_SESSION_CLOSE":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP2_SESSION"));

				session = this.SessionFromUID(in evt);
				if (session == null) break;

				if (session.iError == 0)
					session.iError = evt.GetNetError();

				AssertImportant(!session.FQuic);
				session.Closed = true;

				break;

			// ID -> HTTP_STREAM_JOB, TCP_CLIENT_SOCKET_POOL_REQUESTED_SOCKET, SOCKET_POOL_BOUND_TO_SOCKET
			// srcdep -> HTTP_STREAM_JOB_HTTP2_SESSION_AVAILABLE, HTTP2_SESSION_POOL_FOUND_EXISTING_SESSION/_FROM_IP_POOL
			// NOTE: A new HTTP2 Session has recently been created. So attach the Session to the Request.
			// NOTE: Also add the source_dependency id to the Session so that other Requests can also attach.
			case "HTTP2_SESSION_POOL_IMPORTED_SESSION_FROM_SOCKET":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP_STREAM_JOB"));

				// This event was preceeded by: HTTP2_SESSION_INITIALIZED
				session = this.SessionFromRecent("HTTP2_SESSION_INITIALIZED", in evt);

				// This event Id was attached to the recently created Session via HTTP_STREAM_REQUEST_PROTO(proto:h2)
				if (session == null)
					session = this.SessionFromUID(in evt);
				else
					AssertImportant(session == this.SessionFromUID(in evt) || null == this.SessionFromUID(in evt));

				if (session == null)
				{
					session = new Session(StreamType.HTTP2, in evt)
					{
						fRecovered = true
					};
					this.sessionTable.Add(session);
					this.SessionAttachUID(session, in evt); // sets session.uidVal
				}
				else
				{
					AssertImportant(session.socket != null);
				}

				// Other events reference this Session using the source_dependency id:
				// HTTP_STREAM_JOB_HTTP2_SESSION_AVAILABLE, HTTP2_SESSION_POOL_FOUND_EXISTING_SESSION/_FROM_IP_POOL
				AssertImportant(session.Type == StreamType.HTTP2);
				this.SessionAddSrcDep(session, in evt);

				req = this.RequestFromUID(in evt);
				AssertImportant(req != null);
				if (req == null) break;

				AssertImportant(req.Type == StreamType.Unknown);
				AssertImportant(req.TypeTCP == StreamType.HTTP2);
				AssertImportant(req.port != 0); // else req.port = (soc?.addrRemote).PortGraphable()
				AssertImportant(req.Domain != null); // else look it up via allTables.dnsTable ?

				if (req.SessionHTTP2 == null)
					req.SessionHTTP2 = session;
				else
					AssertImportant(req.SessionHTTP2 == session);

				break;

			/*
				Adjacent:
				HTTP_STREAM_JOB_INIT_CONNECTION.Begin
				HTTP2_SESSION_POOL_FOUND_EXISTING_SESSION
				...
				HTTP_STREAM_JOB_INIT_CONNECTION.End
			*/

			// ID -> HTTP_STREAM_JOB/_INIT_CONNECTION, HTTP_STREAM_JOB_BOUND_TO_REQUEST
			// srcdep -> HTTP2_SESSION_POOL_IMPORTED_SESSION_FROM_SOCKET, HTTP_STREAM_JOB_HTTP2_SESSION_AVAILABLE, HTTP2_SESSION_POOL_FOUND_EXISTING_SESSION_FROM_IP_POOL
			// NOTE: These occur before HTTP_STREAM_JOB_BOUND_TO_REQUEST and mark the TCP (non-QUIC) Stream Job as HTTP2 rather than HTTP1.
			case "HTTP2_SESSION_POOL_FOUND_EXISTING_SESSION":
			case "HTTP2_SESSION_POOL_FOUND_EXISTING_SESSION_FROM_IP_POOL":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP_STREAM_JOB"));

				req = this.RequestFromUID(in evt);
				if (req != null)
				{
					AssertImportant(req.TypeTCP == StreamType.HTTP2 || req.TypeTCP == StreamType.Unknown);
					req.TypeTCP = StreamType.HTTP2; // provisional

					// This Request _might_ get picked up (below) for processing a Preconnect Request.
					this.RequestAttachSrcDep(req, in evt);

					if (req.SessionHTTP2 != null) break;

					srcdep = evt.GetSourceId();
					session = this.SessionFromSrcDep(in evt);
					if (session == null)
					{
						session = req.NewPlaceholderSession(StreamType.HTTP2, in evt);
						this.sessionTable.Add(session);
						this.SessionAttachSrcDep(session, srcdep, in evt);
					}

					req.SessionHTTP2 = session;

					break;
				}

				// Get the HTTP2 Session to attach to a Preconnect Request.

				req = this.RequestFromSrcDep(in evt);
				session = this.SessionFromSrcDep(in evt);
				if (session != null)
				{
					AssertCritical(session.Type == StreamType.HTTP2);
					AssertImportant(req == null || req.SessionHTTP2 == session);
				}
				else
				{
					if (req == null) break;
					session = req.SessionHTTP2;
					AssertImportant(req.TypeTCP == StreamType.HTTP2);
					if (session == null) break;
				}

				// A preconnect HTTP Stream Job has a placeholder Request that connects in a different way.
				// See: HTTP_STREAM_JOB_CONTROLLER
				//  cf. QUIC_SESSION_POOL_ATTACH_HTTP_STREAM_JOB_TO_EXISTING_SESSION

				AssertImportant(!session.Closed);
				AssertImportant(session.Type == StreamType.HTTP2);

				Uri uri = req?.URL.CreateURI();
				if (uri == null) break;

				req = this.FindPreconnectRequest(uri.GetLeftPart(UriPartial.Authority), session.PreKey, in evt);
				if (req == null) break;

				AssertCritical(req.IsPreconnect);

				// A Preconnect Request has no Stream, so it simply refers to a Session, and not vice-versa.
				req.SessionSet = session;

				break;

			// ID -> HTTP2_SESSION, etc.
			// SrcDep -> HTTP_STREAM_REQUEST_STARTED_JOB & SOCKET_IN_USE & HTTP_STREAM_REQUEST_BOUND_TO_JOB
			// "fin":true/false, "stream_id":#, "headers":<string>
			// source_type: HTTP2_SESSION
			// NOTE: This data is for a specific Stream within a specific Session.
			// NOTE: When both Send and Recv have "fin":true from either _DATA or _HEADERS events then the stream is closed.
			// NOTE: HTTP2_SESSION_RECV_HEADERS does not have the SourceId/SrcDep
			case "HTTP2_SESSION_SEND_HEADERS":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP2_SESSION"));

				req = this.RequestFromSrcDep(in evt);
				if (req == null) break;

				session = this.SessionFromUID(evt);
				AssertInfo(session != null);
				if (session == null)
					session = req.SessionHTTP2;
				else
					AssertImportant(session == req.SessionHTTP2 || req.SessionHTTP2 == null);

				if (session == null)
				{
					session = req.NewPlaceholderSession(StreamType.HTTP2, in evt);
					this.sessionTable.Add(session);
					this.SessionAttachUID(session, in evt);
				}

				AssertCritical(session.Type == StreamType.HTTP2);

				stream = session.PopulateStreamFromHeader(in evt);
				AssertCritical(stream != null);
				if (stream == null) break;

				AssertImportant(!stream.HasDataTraffic());
				stream.cbUpload = req.cbUpload;
				stream.cbDownload = req.cbDownload;

				AssertImportant(req.method?.EndsWith(stream.strMethod) ?? false);
				AssertImportant(req.Domain?.Equals(stream.strDomain) ?? false);

				// If there's a Request error then its Stream should have been cleared but not its Session. See: HTTP_TRANSACTION_RESTART_AFTER_ERROR
				AssertImportant(FImplies(req.SessionHTTP2 != null, req.SessionHTTP2 == session || req.iError < 0));
				AssertImportant(FImplies(req.Session != null, req.Session == session || req.iError < 0));

				// Attach the Request to the Stream and to the Session.
				stream.Attach(req);  // Request <-> Stream
				session.Finalize(req); // Request  -> Session

				AssertCritical(stream.request.Session == session);

				this.SessionAddUID(session, req.uidTCP, in evt);

				break;

			// ID -> HTTP2_SESSION, etc.
			// "fin":true/false, "size":##, "stream_id":#
			// NOTE: "fin" = finished (No more data will be sent/received on this stream.)
			case "HTTP2_SESSION_SEND_DATA":
			case "HTTP2_SESSION_RECV_DATA":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP2_SESSION"));

				session = this.SessionFromUID(evt);
				AssertInfo(session != null);
				if (session == null)
				{
					session = new Session(StreamType.QUIC, in evt);
					this.sessionTable.Add(session);
					this.SessionAttachUID(session, in evt);
				}

				rgje = evt.ParseSimpleJsonString(rgstrStreamId_Size);
				if (rgje == null) break;

				cb = rgje[1].MyGetUNumber();
				AssertCritical((int)cb >= 0);
				if (cb == 0) break;

				int iStream = rgje[0].MyGetNumber(-1);
				AssertCritical(iStream >= 0);
				if (iStream < 0) break;

				stream = session.EnsureStream(iStream, evt.Timestamp.ToGraphable());

				if (evt.TaskName.Equals("HTTP2_SESSION_SEND_DATA"))
					stream.cbSend += cb;
				else
					stream.cbRecv += cb;

				break;

			// ID -> HTTP2_SESSION
			// NOTE: Get the HTTP Status from the Header text
			case "HTTP2_SESSION_RECV_HEADERS":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP2_SESSION"));

				session = this.SessionFromUID(evt);
				AssertInfo(session != null);
				if (session == null)
				{
					session = new Session(StreamType.HTTP2, in evt);
					this.sessionTable.Add(session);
					this.SessionAttachUID(session, in evt);
				}

				AssertCritical(session.Type == StreamType.HTTP2);

				session.SetHTTPStatus(in evt);

				break;

			default:
				// Remember this event as unhandled.
				this.unhandled.Add(evt.TaskName);
				break;
			} // switch evt.TaskName
		} // Dispatch_Http2

		/*
			evt.TaskName must begin with "HTTP3_"
		*/
		void Dispatch_Http3(in IGenericEvent evt)
		{
			uint cb;
			int iStream;
			Request req;
			Socket soc;
			Session session;
			Session.Stream stream;
			JsonElement[] rgje;

			AssertCritical(evt.TaskName.StartsWith("HTTP3_"));

			switch (evt.TaskName)
			{
			// ID -> QUIC_SESSION, etc.
			// "stream_id":#, "headers":<string>
			// source_type: QUIC_SESSION
			// NOTE: This data is for a specific Stream within a specific Session.
			// NOTE: cf. QUIC_CHROMIUM_CLIENT_STREAM_SEND_REQUEST_HEADERS (same event, different layer)
			case "HTTP3_HEADERS_SENT":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("QUIC_SESSION"));

				session = this.SessionFromUID(evt);
				AssertImportant(session != null);
				if (session == null) break;

				AssertCritical(session.Type == StreamType.QUIC);

				// A Socket belongs to the Session. A Request belongs to a Stream.

				soc = this.SocketFromUID(in evt);
				if (soc != null)
					session.Attach(soc);

				stream = session.PopulateStreamFromHeader(in evt);
				AssertCritical(stream != null);
				if (stream == null) break;

				// Get a matching pending (or recent) Request.
				req = session.MatchRequest(stream);
				AssertImportant(req != null); // normally shouldn't use the fallback option
				if (req == null)
					req = this.MatchRequest(session.pid, session.tid, stream);

				AssertImportant(req != null);
				if (req == null) break;

				// Attach the Request to the Stream, or confirm.
				stream.Attach(req);
				// Not needed here: session.Finalize(req)
				// But there could be another pending Request queued up with the same URL.
				AssertInfo(session.LookupPendingRequestByURL(stream.strURL) < 0);

				AssertCritical(req.Session == session); 
				req.SessionSet = session; // just in case

				AssertCritical(stream.request?.Session == session);

				break;

			// ID -> QUIC_SESSION
			// "payload_length":#, "stream_id":#
			case "HTTP3_DATA_FRAME_RECEIVED":
			case "HTTP3_DATA_SENT":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("QUIC_SESSION"));

				session = this.SessionFromUID(evt);
				AssertInfo(session != null);
				if (session == null)
				{
					session = new Session(StreamType.QUIC, in evt);
					this.sessionTable.Add(session);
					this.SessionAttachUID(session, in evt);
				}

				AssertCritical(session.Type == StreamType.QUIC);

				rgje = evt.ParseSimpleJsonString(ChromiumTable.rgstrStreamId_Payload);
				if (rgje == null) break;

				cb = rgje[1].MyGetUNumber();
				AssertCritical((int)cb >= 0);
				if (cb == 0) break;

				iStream = rgje[0].MyGetNumber(-1);
				AssertCritical(iStream >= 0);
				if (iStream < 0) break;

				stream = session.EnsureStream(iStream, evt.Timestamp.ToGraphable());

				if (evt.TaskName.Equals("HTTP3_DATA_SENT"))
					stream.cbSend += cb;
				else
					stream.cbRecv += cb;

				AssertCritical(FImplies(stream.request != null, stream.request?.Session == session));

				break;

			// ID -> QUIC_SESSION
			// NOTE: Get the HTTP Status from the Header text
			case "HTTP3_HEADERS_DECODED":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("QUIC_SESSION"));

				session = this.SessionFromUID(evt);
				AssertInfo(session != null);
				if (session == null) break;

				AssertCritical(session.Type == StreamType.QUIC);

				session.SetHTTPStatus(in evt);

				break;

			// ID -> QUIC_SESSION
			// NOTE: Mark these streams as ignorable overhead.
			case "HTTP3_LOCAL_CONTROL_STREAM_CREATED":
			case "HTTP3_LOCAL_QPACK_DECODER_STREAM_CREATED":
			case "HTTP3_LOCAL_QPACK_ENCODER_STREAM_CREATED":
			case "HTTP3_PEER_CONTROL_STREAM_CREATED":
			case "HTTP3_PEER_QPACK_DECODER_STREAM_CREATED":
			case "HTTP3_PEER_QPACK_ENCODER_STREAM_CREATED":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("QUIC_SESSION"));

				session = this.SessionFromUID(evt);
				AssertImportant(session != null);
				if (session == null) break;

				AssertCritical(session.Type == StreamType.QUIC);

				rgje = evt.ParseSimpleJsonString(ChromiumTable.rgstrStreamId);
				if (rgje == null) break;

				iStream = rgje[0].MyGetNumber(-1);
				AssertCritical(iStream >= 0);
				if (iStream < 0) break;

				stream = session.EnsureStream(iStream, evt.Timestamp.ToGraphable());
				stream.fIgnore = true;

				break;

			default:
				// Remember this event as unhandled.
				this.unhandled.Add(evt.TaskName);
				break;
			} // switch evt.TaskName
		} // Dispatch_Http3

		/*
			evt.TaskName must contain "QUIC_"
		*/
		void Dispatch_Quic(in IGenericEvent evt)
		{
			ushort port;
			int srcdep;
			int err;
			string strURL;
			UIDVal uID;
			Request req;
			Socket soc;
			Session session;
			Session.Stream stream;
			ResolverManager resolver;
			IPAddress ipAddress;
			JsonElement[] rgje;

			// QUIC
			// QUIC = Quick UDP Internet Connections, a UDP-based alternative to TCP
			// Chromium may launch both QUIC and TCP connections, and use the one which responds first.

			AssertCritical(evt.TaskName.Contains("QUIC_"));

			switch (evt.TaskName)
			{
			// ID -> QUIC_SESSION_*, HTTP3_LOCAL_CONTROL_STREAM_CREATED, UDP_BYTES_SENT/RECEIVED, etc.
			// SrcDep -> BOUND_TO_QUIC_SESSION_POOL_JOB, SOCKET_ALIVE
			// "host":"<domain>", "network_anonymization_key":"<domain_url etc>", "port":#
			case "QUIC_SESSION":
				if (evt.IsEndPhase())
				{
					session = this.SessionFromUID(in evt, false);
					AssertImportant(session != null);
					session?.Shutdown();

					break;
				}

				AssertImportant(evt.CheckSourceType("QUIC_SESSION"));

				rgje = evt.ParseSimpleJsonString(rgstrHost_Key_Port_SourceId);
				if (rgje == null) break;

				session = new Session(StreamType.QUIC, in evt)
				{
					domain = rgje[0].MyGetString(), // host server name (no http://)
					anon_key = rgje[1].MyGetString(),
					port = (ushort)rgje[2].MyGetUNumber()
				};

				this.sessionTable.Add(session);

				srcdep = rgje[3].MyGetNumber();
				soc = this.SocketFromSrcDep(srcdep, in evt);
#if DEBUG
				AssertImportant(soc != null && !soc.fTCP);
#endif // DEBUG
				session.Attach(soc);

				this.SessionAttachUID_SrcDep(session, srcdep, in evt); // sets session.uidVal and .srcdep

				break;

			// ID -> QUIC_SESSION, etc.
			// SrcDep -> QUIC_SESSION_POOL_JOB_RESULT, HTTP_STREAM_REQUEST_BOUND_TO_QUIC_SESSION
			case "QUIC_SESSION_CREATED":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("QUIC_SESSION_POOL_DIRECT_JOB"));

				// Get the Session of the previous event.
				session = this.SessionFromRecent("QUIC_SESSION", in evt);
				AssertImportant(session != null);
				if (session == null) break;

				AssertCritical(session.Type == StreamType.QUIC);

				resolver = this.GetResolverManager(in evt);
				session.resolver = resolver;

				this.SessionAddUID_SrcDep(session, in evt); // sets: session.srcdep

				// Also assign to this Session the UID (event ID) of: HTTP_STREAM_JOB(use_quic:true)

				// QUIC_SESSION_POOL_JOB_BOUND_TO assigned this UID to a Request.
				req = this.RequestFromUID(in evt);
				if (req == null) break;
				if (req.uidQUIC == 0) break;

				this.SessionAddUID(session, req.uidQUIC, in evt);

				break;

			/*
				ID -> QUIC_SESSION_POOL_JOB, QUIC_SESSION_POOL_JOB_BOUND_TO
				1. ID1 QUIC_SESSION_POOL_JOB host, key, port, ...
				2. ID1 QUIC_SESSION_POOL_JOB_BOUND_TO srcdep-C
				3. ID2 BOUND_TO_QUIC_SESSION_POOL_JOB srcdep-D
				Events 1,2,3 usually occur together.
				When Event 1 does not precede Event 2, then Events 2,3 likely refer to a previous Request.
			*/
			case "QUIC_SESSION_POOL_JOB":
				// This event usually can't be directly associated with a Request.
				// Stash its JSON and pick it up with the next event.
				if (!evt.IsBeginPhase()) break;
				AssertImportant(evt.CheckSourceType("QUIC_SESSION_POOL_DIRECT_JOB"));

				this.StashJSON(in evt);

				break;

			// ID -> QUIC_SESSION_POOL_JOB
			// SrcDep -> HTTP_STREAM_REQUEST_STARTED_JOB, HTTP_STREAM_REQUEST_BOUND_TO_JOB
			// NOTE: Adjacent to QUIC_SESSION_POOL_JOB & BOUND_TO_QUIC_SESSION_POOL_JOB
			case "QUIC_SESSION_POOL_JOB_BOUND_TO":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("QUIC_SESSION_POOL_DIRECT_JOB"));

				string strJSON = this.UnstashJSON("QUIC_SESSION_POOL_JOB", in evt);
				if (strJSON == null)
				{
					// Disable a subsequent: BOUND_TO_QUIC_SESSION_POOL_JOB
					this.ResetRecent(evt.ProcessId, evt.ThreadId);
					break;
				}

				// Look up the Request from the srcdep and assign the UID.
				// If null then this is either near the start of the trace, or it's a Preconnect QUIC Session.
				req = this.RequestFromUID_SrcDep(in evt);
				if (req == null) break;

				rgje = ParseSimpleJsonString(strJSON, rgstrHost_Key_Port);
				if (rgje == null) break;

				port = (ushort)rgje[2].MyGetUNumber();
				string strAnonKey = rgje[1].MyGetString(); // network_anonymization_key
				string strHost = rgje[0].MyGetString(); // host server name (no http://)

				AssertImportant(req.Domain == null || req.Domain.Equals(strHost));
				req.Domain = strHost;

				AssertImportant(req.anon_key == null || req.anon_key.Equals(strAnonKey));
				req.anon_key = strAnonKey;

				AssertImportant(req.port == 0 || req.port == port);
				req.port = port;

				break;

			// ID -> HTTP_STREAM_JOB
			// srcdep -> QUIC_SESSION, SOCKET_ALIVE
			case "BOUND_TO_QUIC_SESSION_POOL_JOB":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP_STREAM_JOB"));

				// Look up the Request and assign the srcdep.
				req = this.RequestFromSrcDep(in evt);
				if (req == null)
					req = this.RequestFromUID_Correlate(in evt, "QUIC_SESSION_POOL_JOB_BOUND_TO");

				AssertImportant(req != null || this.RequestFromUID(in evt) == null); // else what did we miss?
				AssertImportant(req == null || req.uidQUIC == 0 || req.uidQUIC == evt.GetUID());

				if (req != null && req.uidQUIC == 0)
					req.uidQUIC = evt.GetUID();

				session = this.SessionFromSrcDep(in evt);
				if (session == null)
				{
					session = req?.SessionQUIC;
					if (session == null) break; // common
				}

				AssertCritical(session.Type == StreamType.QUIC);

				// Associate the HTTP_STREAM_JOB with the Session.
				this.SessionAddUID(session, in evt);

				break;

			// ID -> QUIC_SESSION, etc.
			// "quic_error":#
			case "QUIC_SESSION_CLOSED":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("QUIC_SESSION"));

				session = this.SessionFromUID(in evt, false);
				if (session == null) break;

				// Timeout (25) is a default reason to close.
				err = evt.GetQuicError();
				if (err == QUIC_NETWORK_IDLE_TIMEOUT/*25*/)
					err = 0;

				if (session.iError == 0)
					session.iError = err;

				AssertImportant(session.FQuic);
				session.Closed = true;

				// Error: close the Socket immediately
				if (err != 0)
					session.socket?.Close(evt.Timestamp.ToGraphable());

				break;

			// ID -> QUIC_SESSION, etc.
			// "net_error":#
			case "QUIC_SESSION_CLOSE_ON_ERROR":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("QUIC_SESSION"));

				session = this.SessionFromUID(in evt, false);
				if (session == null) break;
				AssertImportant(session.FQuic);

				err = evt.GetNetError();

				if (session.iError == 0)
					session.iError = err;

				// Error: close the Socket immediately
				if (err != 0)
					session.socket?.Close(evt.Timestamp.ToGraphable());

				session.Closed = true;

				break;

			// ID -> QUIC_SESSION
			// source_type: QUIC_SESSION
			// NOTE: The Session's Socket has 'degraded' and a new one is being spun up (which may or may not succeed).
			// NOTE: The .End event is preceeded by UDP_LOCAL_ADDRESS.
			case "QUIC_PORT_MIGRATION_TRIGGERED":
				AssertImportant(!evt.IsInstantPhase());
				if (evt.IsBeginPhase())
				{
					AssertImportant(evt.CheckSourceType("QUIC_SESSION"));
					break;
				}

				soc = this.SocketFromRecent(in evt, "UDP_LOCAL_ADDRESS");
				AssertImportant(soc != null);
				if (soc == null) break;

				session = this.SessionFromUID(in evt);
				AssertImportant(session != null);
				if (session == null) break;

				AssertCritical(session.Type == StreamType.QUIC);

				session.socketPreMigrate = soc;

				break;

			// ID -> QUIC_SESSION
			// source_type: QUIC_SESSION
			// NOTE: The QUIC Session has successfully done a connectivity probe with a challenge frame, etc. on the new Socket.
			case "QUIC_PORT_MIGRATION_SUCCESS":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("QUIC_SESSION"));

				session = this.SessionFromUID(in evt);
				AssertImportant(session != null);
				if (session == null) break;

				AssertCritical(session.Type == StreamType.QUIC);

				AssertImportant(session.socketPreMigrate != null);
				if (session.socketPreMigrate == null) break;
			/*
				Since NetBlame is very Socket-based, we'll clone/migrate the current Session:
				Create a copy of the Session with the original Socket and all of its original Streams.
				The original Session gets the new Socket (session.socketPreMigrate).
				All the Streams of this original Session get reset with cbSend/Recv = 0.
				All of the Requests linked to these original Streams remain linked,
				as these Session/Streams are now using the new Socket.

				NOTE: In this case TWO sets of Streams will refer to ONE set of Requests,
				while that ONE set of Requests refers to just ONE set of Streams.
				This is all handled in the "gather" phase via: Session.AdjustForMigration
			*/
				this.sessionTable.Add(session.Migrate(evt.Timestamp.ToGraphable()));

				break;

			case "QUIC_CONNECTION_MIGRATION_TRIGGERED":
				AssertImportant(false); // Test This
				goto case "QUIC_PORT_MIGRATION_TRIGGERED";

			case "QUIC_CONNECTION_MIGRATION_SUCCESS":
				AssertImportant(false); // Test This
				goto case "QUIC_PORT_MIGRATION_SUCCESS";

			// ID -> QUIC_SESSION, etc.
			// quic_stream_id:#, "headers":<string>
			// source_type: QUIC_SESSION
			// NOTE: This data is for a specific Stream within a Session.
			// NOTE: The previous event provides the Request connection: HTTP_TRANSACTION_QUIC_SEND_REQUEST_HEADERS
			// NOTE: cf. HTTP3_HEADERS_SENT (same event, different layer)
			case "QUIC_CHROMIUM_CLIENT_STREAM_SEND_REQUEST_HEADERS":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("QUIC_SESSION"));

				uID = this.GetRecentUID("HTTP_TRANSACTION_QUIC_SEND_REQUEST_HEADERS", in evt);
				AssertImportant(uID != 0);

				req = this.RequestFromUID(uID, in evt);
				AssertImportant(req != null);
				if (req == null) break;

				session = this.SessionFromUID(evt);
				AssertImportant(session != null);
				if (session == null)
				{
					session = req.SessionQUIC;
					if (session == null) break;
				}

				AssertCritical(session.Type == StreamType.QUIC);

				// A Socket belongs to the Session. A Request belongs to a Stream.

				soc = this.SocketFromUID(in evt);
				if (soc != null)
					session.Attach(soc);

				stream = session.PopulateStreamFromHeader(in evt);
				AssertCritical(stream != null);
				if (stream == null) break;

				// If there's a Request error then its Stream should have been cleared but not its Session. See: HTTP_TRANSACTION_RESTART_AFTER_ERROR
				AssertImportant(FImplies(req.SessionQUIC != null, req.SessionQUIC == session || req.iError < 0));
				AssertImportant(FImplies(req.Session != null, req.Session == session || req.iError < 0));

				// Attach the Request to the Stream and to the Session.
				stream.Attach(req);  // Request <-> Stream
				session.Finalize(req); // Request -> Session & remove the lookup

				AssertCritical(stream.request?.Session == session);

				break;

			// ID -> same events
			// SrcDep -> QUIC_SESSION_CREATED, HTTP_STREAM_REQUEST_BOUND_TO_QUIC_SESSION, QUIC_SESSION_POOL_USE_EXISTING_SESSION
			case "QUIC_SESSION_POOL_MATCHING_IP_SESSION_FOUND":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("QUIC_SESSION_POOL"));

				session = this.SessionFromSrcDep(in evt);
				if (session != null)
					this.SessionAddUID(session, in evt);

				break;

			/*
				Adjacent:
				HTTP_STREAM_JOB_INIT_CONNECTION.Begin
				QUIC_SESSION_POOL_USE_EXISTING_SESSION : SrcDep -> QUIC_SESSION_CREATED
				QUIC_SESSION_POOL_ATTACH_HTTP_STREAM_JOB_TO_EXISTING_SESSION : SrcDep -> HTTP_STREAM_REQUEST_STARTED_JOB
				...
				HTTP_STREAM_JOB_INIT_CONNECTION.End
			*/

			// ID -> HTTP_STREAM_JOB_INIT_CONNECTION
			// SrcDep -> QUIC_SESSION_CREATED, QUIC_SESSION_POOL_JOB_RESULT, HTTP_STREAM_REQUEST_BOUND_TO_QUIC_SESSION
			// "destination":"<url>", "source_dependency":...
			case "QUIC_SESSION_POOL_USE_EXISTING_SESSION":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP_STREAM_JOB"));

				// Pass the JSON on to the next event: QUIC_SESSION_POOL_ATTACH_HTTP_STREAM_JOB_TO_EXISTING_SESSION
				this.StashJSON(in evt);

				break;

			// ID -> QUIC_SESSION, etc.
			// SrcDep -> HTTP_STREAM_REQUEST_STARTED_JOB, HTTP_STREAM_REQUEST_BOUND_TO_JOB
			case "QUIC_SESSION_POOL_ATTACH_HTTP_STREAM_JOB_TO_EXISTING_SESSION":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("QUIC_SESSION"));

				strJSON = this.UnstashJSON("QUIC_SESSION_POOL_USE_EXISTING_SESSION", in evt);
				rgje = ParseSimpleJsonString(strJSON, rgstrDestination_SourceId);

				req = this.RequestFromSrcDep(in evt);
				if (req != null)
				{
					AssertImportant(req.Type == StreamType.Unknown); // not yet determined

					if (req.SessionQUIC != null) break;

					session = this.SessionFromUID(in evt);
					AssertInfo(session != null);
					if (session == null)
					{
						// The QUIC_SESSION event must have happened before tracing started.
						session = req.NewPlaceholderSession(StreamType.QUIC, in evt);
						this.sessionTable.Add(session);
						this.SessionAttachUID_SrcDep(session, in evt); // sets: session.uidVal/.srcdep
					}

					// Also associate the SrcDep of the paired event: QUIC_SESSION_POOL_USE_EXISTING_SESSION
					if (rgje != null)
					{
						srcdep = rgje[1].MyGetNumber();
						this.SessionAddSrcDep(session, srcdep, in evt);
					}

					req.SessionQUIC = session;

					break;
				}

				// This is probably a "preconnect" Stream Job.
				// Reference a preconnect Request and Session, if available.

				if (rgje == null) break;

				srcdep = rgje[1].MyGetNumber();
				session = this.SessionFromSrcDep(srcdep, in evt);
				if (session == null)
					session = this.SessionFromUID(in evt);
				else
					AssertImportant(session == this.SessionFromUID(in evt));

				AssertImportant(session != null);
				if (session == null)
				{
					session = new Session(StreamType.QUIC, in evt);
					this.sessionTable.Add(session);
					this.SessionAttachUID_SrcDep(session, in evt);
				}

				strURL = rgje[0].MyGetString();
				req = this.FindPreconnectRequest(strURL, session.PreKey, in evt);
				if (req == null) break;

				// A preconnect HTTP Stream Job has a placeholder Request that connects in a different way.
				// See: HTTP_STREAM_JOB_CONTROLLER
				//  cf. HTTP2_SESSION_POOL_FOUND_EXISTING_SESSION

				AssertCritical(req.IsPreconnect);
				AssertCritical(req.URL.StartsWith(strURL)); // "destination" string

				// A Preconnect Request has no Stream, so it simply refers to a Session, and not vice-versa.
				AssertImportant(req.Session == session || req.Session == null);
				req.SessionSet = session;

				AssertImportant(req.Type == StreamType.QUIC);

				if (!(session.socket?.addrRemote).Empty() && req.port == 0)
				{
					req.ipAddr = session.socket.addrRemote.Address.ToString();
					req.port = (ushort)session.socket.addrRemote.PortGraphable();
				}

				break;

			// ID -> QUIC_SESSION
			// peer_address:##.##.##.##, self_address:##.##.##.##, size:##7215
			case "QUIC_SESSION_PACKET_RECEIVED":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("QUIC_SESSION"));

				session = this.SessionFromUID(in evt);
				AssertInfo(session != null);
				if (session == null) break;

				soc = session.socket;
				AssertInfo(soc != null);
				if (soc == null)
				{
					// Reconstruct the missing Socket.

					AssertImportant(session.socketPreMigrate == null); // else how!?

					rgje = evt.ParseSimpleJsonString(rgstrPeer_Self);
					if (rgje == null) break;

					if (!DNSClient.DNSTable.TryParseWithPort(rgje[0].MyGetString(), out ipAddress, out port)) break;

					soc = new Socket(StreamType.QUIC, in evt);

					soc.addrRemote = new IPEndPoint(ipAddress, port);
					AssertImportant(session.port == 0 || session.port == port);
					session.port = port;

					if (DNSClient.DNSTable.TryParseWithPort(rgje[1].MyGetString(), out ipAddress, out port))
						soc.addrLocal = new IPEndPoint(ipAddress, port);

					session.Attach(soc);

					break;
				}
#if DEBUG
				// Confirm the addresses.

				rgje = evt.ParseSimpleJsonString(rgstrPeer_Self);
				if (rgje == null) break;

				if (DNSClient.DNSTable.TryParseWithPort(rgje[0].MyGetString(), out ipAddress, out port))
				{
					IPEndPoint ipep = new IPEndPoint(ipAddress, port);
					AssertImportant(ipep.Equals(soc.addrRemote));
					AssertImportant(session.port == port);
				}

				if (DNSClient.DNSTable.TryParseWithPort(rgje[1].MyGetString(), out ipAddress, out port))
				{
					IPEndPoint ipep = new IPEndPoint(ipAddress, port);
					AssertImportant(ipep.Equals(soc.addrLocal) || ipep.Equals(session.socketPreMigrate?.addrLocal));
				}
#endif // DEBUG
				break;

			default:
				// Remember this event as unhandled.
				this.unhandled.Add(evt.TaskName);
				break;
			} // switch evt.TaskName
		} // Dispatch_Quic

		/*
			evt.TaskName must begin with "SOCKET_"
		*/
		void Dispatch_Socket(in IGenericEvent evt)
		{
			int srcdep;
			int err;
			UIDVal uID;
			Request req;
			Socket soc;
			JsonElement[] rgje;

			AssertCritical(evt.TaskName.StartsWith("SOCKET_"));

			switch (evt.TaskName)
			{
			// ID -> SSL_CONNECT_JOB_CONNECT, TRANSPORT_CONNECT_JOB_CONNECT_ATTEMPT, CONNECT_JOB_SET_SOCKET, HOST_RESOLVER_MANAGER_CACHE_HIT
			// source_type: SSL_CONNECT_JOB, TRANSPORT_CONNECT_JOB
			case "SOCKET_POOL_CONNECT_JOB_CREATED":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("SSL_CONNECT_JOB") || evt.CheckSourceType("TRANSPORT_CONNECT_JOB"));

				rgje = evt.ParseSimpleJsonString(rgstrGroupId);
				if (rgje == null) break;

				srcdep = rgje[0].MyGetString().GetHashCode(); // pseudo-srcdep: hash of group_id string

				// Common sequence:
				// TCP_CLIENT_SOCKET_POOL_REQUESTED_SOCKET/S
				// SOCKET_POOL*.Begin // ignored?
				// SOCKET_POOL_STALLED_MAX_SOCKETS_PER_GROUP // occasionally
				// CONNECT_JOB.Begin // ignored
				// SOCKET_POOL_CONNECT_JOB_CREATED // << this event

				req = this.RequestFromGroupId_Correlate(in evt, srcdep, "TCP_CLIENT_SOCKET_POOL_REQUESTED_SOCKET", "TCP_CLIENT_SOCKET_POOL_REQUESTED_SOCKETS");
				if (req == null)
				{
					req = this.RequestFromSrcDep(srcdep, in evt);
					if (req == null) break;
				}

				AssertImportant(this.RequestFromUID(in evt) == null);
				this.RequestAttachUID(req, in evt);

				break;

			// ID -> SOCKET_ALIVE
			case "SOCKET_CLOSED":
			case "SOCKET_POOL_CLOSING_SOCKET":
				AssertImportant(evt.IsInstantPhase());

				if (!evt.CheckSourceType("SOCKET")) break;

				soc = this.SocketFromUID(in evt, false);
				AssertInfo(soc != null);
				if (soc == null) break;

				soc.Close(evt.Timestamp.ToGraphable());

				break;

			// Socket / WinSock-related Events
			// These four events are adjacent within the trace for a given thread (except when Phase = End).

			// source_type:
			//	SOCKET: ID -> SSL_CONNECT, TCP_CONNECT, TCP_CONNECT_ATTEMPT, SOCKET_BYTES_SENT/RECEIVED, SOCKET_IN_USE, SOCKET_CLOSED
			//  UDP_SOCKET: ID -> UDP_CONNECT, UDP_LOCAL_ADDRESS, UDP_BYTES_SENT/RECEIVED
			//  UDP_CLIENT_SOCKET: ID -> SOCKET_OPEN, SOCKET_CONNECT
			case "SOCKET_ALIVE":
				if (!evt.IsBeginPhase())
					break;

				switch (evt.GetSourceType())
				{
				/*
					TCP: Six adjacent Chromium events:
					- SOCKET_ALIVE / SOCKET:        Create a new Socket. (The next event is correlated/adjacent.)
					- TRANSPORT_CONNECT_JOB_CONNECT_ATTEMPT: Get the IP Address and attach the Socket to a Request. (The next event is correlated/adjacent.)
					- TCP_CONNECT / SOCKET:         Set the time span for capturing the Winsock Connection.
					- TCP_CONNECT_ATTEMPT / SOCKET: Capture the intervening Winsock Connection creation.
					- TCP_CONNECT_ATTEMPT.End:      No operation.
					- TCP_CONNECT.End:              Get the Local and Remote IP Addresses.
				*/
				case "SOCKET":
					soc = new Socket(StreamType.TCP, in evt);

					this.socketTable.Add(soc);

					// Also sets the recent Socket for: TRANSPORT_CONNECT_JOB_CONNECT_ATTEMPT
					this.SocketAttachUID_SrcDep(soc, in evt);

					break;

				/*
					UDP: Three UDP_CLIENT_SOCKET events with interspersed UDP_SOCKET events:
					x SOCKET_ALIVE / UDP_SOCKET: (redundant?)
					- SOCKET_ALIVE / UDP_CLIENT_SOCKET: Determine that it's a QUIC Socket, create a new Socket and attach the UID to the Request.
					- SOCKET_OPEN  / UDP_CLIENT_SOCKET: Capture the intervening Winsock Connection creation.
					x UDP_CONNECT / UDP_SOCKET: IP Address (redundant? UDP_CONNECT.End carries "net_error")
					- SOCKET_CONNECT/UDP_CLIENT_SOCKET: Get the IP Address and confirm (from the port) that it's not DNS.
					x UDP_LOCAL_ADDRESS / UDP_SOCKET: LocalAddress:Socket
					x UDP_BYTES_SENT/RECEIVED / UDP_SOCKET
					The UDP_SOCKET events are seemingly redundant, but the ID captures later SENT/RECEIVED events.
				*/
				case "UDP_SOCKET":
					this.SetRecentUID(in evt); // This event's uID will be linked to the associated Socket.
					break;

				case "UDP_CLIENT_SOCKET":
					srcdep = evt.GetSourceId();

					// Ignore events associated with a DNS_TRANSACTION.
					if (this.IsDNSSrcDep(in evt, srcdep)) break;

					soc = new Socket(StreamType.QUIC, in evt);

					this.socketTable.Add(soc);

					// Attach the UID from the SOCKET_ALIVE/UDP_SOCKET event to this Request & Socket.
					uID = this.GetRecentUID(evt.TaskName, in evt);
					if (uID != 0)
						this.SocketAttachUID(soc, uID, in evt);

					this.SocketAttachUID_SrcDep(soc, in evt);

					// This source dependency id should also match: BOUND_TO_QUIC_SESSION_POOL_JOB & QUIC_SESSION
					// Otherwise this is not the UDP Socket that we want. (It's probably for DNS.)
					req = this.RequestFromSrcDep(in evt);
					if (req == null) break; // common

					if (uID != 0)
						req.AddUID(uID);

					req.AddUID(evt.GetUID());

					break;

				default:
					AssertImportant(false); // else what?
					break;
				}
				break;

			// ID -> SOCKET_ALIVE
			case "SOCKET_IN_USE":
				AssertImportant(!evt.IsInstantPhase());
				if (!evt.IsBeginPhase()) break;

				AssertImportant(evt.CheckSourceType("SOCKET"));

				soc = this.SocketFromUID(in evt);
				if (soc == null) break;

				this.SocketAttachSrcDep(soc, evt.GetSourceId(), in evt);

				break;

			// ID -> SOCKET_ALIVE/UDP_CLIENT_SOCKET
			case "SOCKET_OPEN":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("UDP_CLIENT_SOCKET")); // Must be a UDP Socket.

				this.AttachWinsockConnection(in evt, WinsockAFD.IPPROTO.UDP);

				break;

			// ID -> SOCKET_ALIVE/UDP_CLIENT_SOCKET
			// "address":"#.#.#.#:#", "net_error":#
			// source_type: UDP_CLIENT_SOCKET
			case "SOCKET_CONNECT":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("UDP_CLIENT_SOCKET")); // Must be a UDP Socket.

				soc = this.SocketFromUID(in evt);
				if (soc == null) break;

				AssertImportant(!soc.Closed);

				rgje = evt.ParseSimpleJsonString(rgstrAddress_Error);
				if (rgje == null) break;

				soc.SetAddrLocalRemote(null, rgje[0].MyGetString());

				err = rgje[1].MyGetNumber();
				if (err != jsonIntDefault)
					soc.iError = err;

				break;

			// ID -> HTTP_STREAM_JOB, etc.
			// SrcDep -> TRANSPORT_CONNECT_JOB_CONNECT_ATTEMPT, CONNECT_JOB_SET_SOCKET, HTTP2_SESSION_INITIALIZED
			// NOTE: Link the TCP Stream Job to its TCP Socket and set the type.
			case "SOCKET_POOL_BOUND_TO_SOCKET":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP_STREAM_JOB"));

				req = this.RequestFromUID(in evt);
				AssertImportant(req != null);
				if (req == null) break;

				soc = this.SocketFromSrcDep(in evt);
				AssertImportant(soc != null);
				if (soc == null) break;

				soc.uidBound = evt.GetUID();

				if (req.FAttachPlaceholderSessionAndStream(soc, in evt))
				{
					this.sessionTable.Add(req.Session);
					this.SessionAttachUID(req.Session, in evt); // sets: session.uidVal
				}

				break;

			case "SOCKET_POOL_STALLED_MAX_SOCKETS_PER_GROUP":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP_STREAM_JOB"));

				const string strTaskLink = "TCP_CLIENT_SOCKET_POOL_REQUESTED_SOCKET";

				req = this.RequestFromUID_Correlate(in evt, strTaskLink);

				if (req != null)
					this.StashStalledRequest(req, in evt, strTaskLink);

				break;

			default:
				// Remember this event as unhandled.
				this.unhandled.Add(evt.TaskName);
				break;
			} // switch evt.TaskName
		} // Dispatch_Socket

		/*
			evt.TaskName must begin with "TCP_"
		*/
		void Dispatch_Tcp(in IGenericEvent evt)
		{
			int srcdep, hidGroup;
			Request req;
			Socket soc;
			TimestampUI timeStamp;
			JsonElement[] rgje;

			AssertCritical(evt.TaskName.StartsWith("TCP_"));

			switch (evt.TaskName)
			{
			// ID -> HTTP_STREAM_JOB, HTTP_STREAM_JOB_BOUND_TO_REQUEST, CANCELLED
			// srcdep -> (hashed "group_id") SOCKET_POOL_CONNECT_JOB_CREATED
			case "TCP_CLIENT_SOCKET_POOL_REQUESTED_SOCKET":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP_STREAM_JOB"));

				rgje = evt.ParseSimpleJsonString(rgstrGroupId);
				if (rgje == null) break;

				// Look up the Request and attach the hash of the group_id string (hidGroup) as a srcdep.

				hidGroup = srcdep = rgje[0].MyGetString().GetHashCode();

				req = this.RequestFromUID(in evt);
				if (req == null) break;

				if (req.hidGroup == 0)
					req.hidGroup = hidGroup;
				else
					AssertImportant(req.hidGroup == hidGroup);

				this.RequestAttachSrcDep(req, srcdep, in evt); // reattach/replace

				break;

			// ID -> HTTP_STREAM_JOB, HTTP_STREAM_JOB_BOUND_TO_REQUEST, CANCELLED
			// srcdep -> (hashed "group_id") SOCKET_POOL_CONNECT_JOB_CREATED
			// NOTE: This refers to PRECONNECT sockets. cf. HTTP_STREAM_JOB_CONTROLLER is_preconnect:true
			case "TCP_CLIENT_SOCKET_POOL_REQUESTED_SOCKETS":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("HTTP_STREAM_JOB"));

				rgje = evt.ParseSimpleJsonString(rgstrGroupId);
				if (rgje == null) break;

				string strGroupId = rgje[0].MyGetString();
				if (string.IsNullOrWhiteSpace(strGroupId)) break;

				// Look up the Request and attach the hash of the group_id string (hidGroup) as a srcdep.

				hidGroup = srcdep = strGroupId.GetHashCode();

				AssertImportant(this.RequestFromUID(in evt) == null);

				req = this.RequestFromSrcDep(srcdep, in evt);

				if (req == null || !req.IsPreconnect)
				{
					string strURLBase = strGroupId.BaseURLFromGroupId();

					req = this.FindPreconnectRequest(strURLBase, hidGroup, in evt);

					if (req == null) break;
				}

				// req.FAttachPlaceholderSessionAndStream will be done when a Socket becomes available in: CONNECT_JOB_SET_SOCKET

				AssertCritical(req.TypeTCP != StreamType.QUIC);
				if (req.TypeTCP == StreamType.Unknown)
					req.TypeTCP = StreamType.TCP;

				if (req.hidGroup == 0)
					req.hidGroup = hidGroup;
				else
					AssertImportant(req.hidGroup == hidGroup);

				this.RequestAttachSrcDep(req, srcdep, in evt); // reattach/replace

				break;

			// "address_list":["#.#.#.#:#", ...]
			// .End: "local_address":"#.#.#.#:#", "remote_addess":"#.#.#.#:#", "net_error":#
			case "TCP_CONNECT":
				soc = this.SocketFromUID(in evt);
				if (soc == null) break;

				AssertImportant(!soc.Closed);

				if (!evt.IsEndPhase())
				{
					AssertImportant(evt.CheckSourceType("SOCKET"));

					timeStamp = evt.Timestamp.ToGraphable();
					soc.timeStampConnect = timeStamp;

					break;
				}

				rgje = evt.ParseSimpleJsonString(rgstrLocal_Remote);
				if (rgje == null) break;

				soc.SetAddrLocalRemote(rgje[0].MyGetString(), rgje[1].MyGetString());

				break;

			// "address":"#.#.#.#:#"
			// .End: "os_error":#
			case "TCP_CONNECT_ATTEMPT":
				if (evt.IsEndPhase())
					break;

				AssertImportant(evt.CheckSourceType("SOCKET"));

				this.AttachWinsockConnection(in evt, WinsockAFD.IPPROTO.TCP);

				break;

			default:
				// Remember this event as unhandled.
				this.unhandled.Add(evt.TaskName);
				break;
			} // switch evt.TaskName
		} // Dispatch_Tcp

		/*
			evt.TaskName must begin with "URL_"
		*/
		void Dispatch_Url(in IGenericEvent evt)
		{
			uint cb;
			Request req;
			TimestampUI timeStamp;
			JsonElement[] rgje;

			AssertCritical(evt.TaskName.StartsWith("URL_"));

			switch (evt.TaskName)
			{
			// ID -> REQUEST_ALIVE; HTTP_STREAM_REQUEST, HTTP_STREAM_JOB_CONTROLLER_BOUND, HTTP_STREAM_REQUEST_BOUND_TO_JOB, HTTP_STREAM_REQUEST_BOUND_TO_QUIC_SESSION
			// "initiator":"<domain url>", "method","<METHOD>", "network_isolation_key":"<domain url or null> <domain url or null>", "url","<url>"
			// .End: "net_error":#
			// NOTE: A Redirect (301 or 302) will create multiple URL_REQUEST_START_JOB for a single REQUEST_ALIVE.
			case "URL_REQUEST_START_JOB":
				timeStamp = evt.Timestamp.ToGraphable();

				req = this.RequestFromUID(in evt, false);

				if (!evt.IsBeginPhase())
				{
					if (req == null) break;

					req.SetNetError(in evt);

					AssertImportant(!req.Closed);
					req.Close(in timeStamp);

					break;
				}

				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				Priority priRedir = Priority.Unknown;
				bool fRedirect = false;
				if (req != null && req.Closed)
				{
					priRedir = req.priority;
					fRedirect = req.fRedirect;
					req = null;
				}

				if (req == null)
				{
					req = new Request(in evt)
					{
						priority = priRedir
					};

					req.xlink.GetLink(evt.ThreadId, timeStamp, in this.allTables.threadTable);

					this.Add(req);
					this.RequestAttachUID(req, in evt);
				}

				AssertImportant(!req.fRedirect);
				AssertImportant(req.method == null);

				req.uidRequest = evt.GetUID();
				req.timeStampBeginJob = timeStamp; // Overwrite the time from: REQUEST_ALIVE

				rgje = evt.ParseSimpleJsonString(rgstrURL_Method);
				if (rgje == null) break;

				req.method = rgje[1].MyGetString();

				if (fRedirect)
					req.method = "REDIRECT/" + req.method;

				if (req.URL == null)
				{
					req.URL = rgje[0].MyGetString();
				}
				else
				{
					AssertImportant(req.URL.Equals(rgje[0].MyGetString()));
					AssertImportant(req.URLScrub.Equals(rgje[0].MyGetString().Split('#', 2)[0]));
				}

				break;

			// ID -> REQUEST_ALIVE, URL_REQUEST_START_JOB, etc.
			// "byte_count":#
			// NOTE: This event exists even when the data wasn't compressed ("filtered").
			// NOTE: cf. UPLOAD_DATA_STREAM_INIT
			case "URL_REQUEST_JOB_FILTERED_BYTES_READ":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				req = this.RequestFromUID(in evt, false);
				AssertInfo(req != null);
				if (req == null) break;

				rgje = evt.ParseSimpleJsonString(rgstrBytes);
				if (rgje == null) break;

				cb = rgje[0].MyGetUNumber(0);
				AssertCritical((int)cb >= 0);

				req.cbDownload += cb;

				if (req.stream == null) break;

				AssertCritical(req.Session != null);
				AssertImportant(req.Session?.Type == req.Type);

				req.stream.cbDownload += cb;

				req.stream.SetLastTime(evt.Timestamp.ToGraphable());

				break;

			// ID -> REQUEST_ALIVE, URL_REQUEST_START_JOB x2, etc.
			// "location":"<url>"
			case "URL_REQUEST_REDIRECTED":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				req = this.RequestFromUID(in evt, false);
				AssertImportant(req != null);
				if (req != null)
					req.fRedirect = true;

				break;

			// ID -> REQUEST_ALIVE, URL_REQUEST_START_JOB, etc.
			// "priority":"<PRIORITY>"
			case "URL_REQUEST_SET_PRIORITY":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				req = this.RequestFromUID(in evt);
				if (req == null) break;

				rgje = evt.ParseSimpleJsonString(rgstrPriority);
				if (rgje == null) break;

				req.priority = rgje[0].MyGetString().GetPriority();

				break;

			default:
				// Remember this event as unhandled.
				this.unhandled.Add(evt.TaskName);
				break;
			} // switch evt.TaskName
		} // Dispatch_Url

		/*
			evt.TaskName must contain "CONNECT_"
		*/
		void Dispatch_Connect(in IGenericEvent evt)
		{
			ushort port;
			int srcdep;
			Request req;
			Socket soc;
			IPAddress ipAddress;
			JsonElement[] rgje;

			AssertCritical(evt.TaskName.Contains("CONNECT_"));

			switch (evt.TaskName)
			{
			// ID -> SOCKET_POOL_CONNECT_JOB_CREATED
			// source_type: SSL_CONNECT_JOB, TRANSPORT_CONNECT_JOB
			// NOTE: What happens between CONNECT_JOB.Begin/.End is known to be connection overhead, not HTTP object/data transfer.
			// NOTE: Due to Chromium optimizations, this Socket could get abandoned, and we don't want to leave the impression of HTTP data traffic being orphaned.
			case "CONNECT_JOB":
				if (!evt.IsEndPhase()) break;

				soc = this.SocketFromUID(in evt);
				if (soc?.cxn == null) break;

				// Zero the connection overhead traffic. These values surface only if the Socket doesn't ultimately attach to a Session, in which case there should be zero non-overhead traffic.
				soc.cxn.cbSend = soc.cxn.cbRecv = 0;

				break;

			// ID -> SOCKET_POOL_CONNECT_JOB_CREATED
			// srcdep -> TRANSPORT_CONNECT_JOB_CONNECT_ATTEMPT, SOCKET_POOL_BOUND_TO_SOCKET
			// source_type: SSL_CONNECT_JOB, TRANSPORT_CONNECT_JOB
			case "CONNECT_JOB_SET_SOCKET":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("SSL_CONNECT_JOB") || evt.CheckSourceType("TRANSPORT_CONNECT_JOB"));

				srcdep = evt.GetSourceId();

				soc = this.SocketFromUID(in evt);
				if (soc == null)
					soc = this.SocketFromSrcDep(srcdep, in evt);

				if (soc == null) break;

				this.SocketAttachUID_SrcDep(soc, srcdep, in evt);

				this.ResolverManagerAttachSrcDep(srcdep, in evt);

				req = this.RequestFromUID(in evt);
				if (req == null) break;

				if (req.port == 0)
					req.port = (ushort)soc.addrRemote.PortGraphable();

				// If this is a Preconnect Request then this is our last opportunity to assign a Socket/Session.
				// Otherwise wait for a binding event: SOCKET_POOL_BOUND_TO_SOCKET

				if (!req.IsPreconnect) break;

				if (req.FAttachPlaceholderSessionAndStream(soc, in evt))
				{
					this.sessionTable.Add(req.Session);
					this.SessionAttachUID(req.Session, in evt); // sets: session.uidVal
				}

				break;

			// ID -> CONNECT_JOB, etc.
			// .End: "net_error":#
			// source_type: TRANSPORT/SSL_CONNECT_JOB
			case "SSL_CONNECT_JOB_CONNECT":
			case "TRANSPORT_CONNECT_JOB_CONNECT":
				AssertCritical(!evt.IsInstantPhase());

				req = this.RequestFromUID(in evt);
				if (req == null) break;

				if (evt.TaskName.StartsWith("SSL"))
					req.fSSL = true;

				if (evt.IsBeginPhase())
				{
					AssertImportant(evt.CheckSourceType("SSL_CONNECT_JOB") || evt.CheckSourceType("TRANSPORT_CONNECT_JOB"));
					break;
				}

				req.SetNetError(in evt);

				break;

			// ID -> SOCKET_POOL_CONNECT_JOB_CREATED
			case "TRANSPORT_CONNECT_JOB_CONNECT_ATTEMPT":
				AssertImportant(evt.IsInstantPhase());

				soc = this.SocketFromRecent(in evt, "SOCKET_ALIVE");

				this.ResetRecent(evt.ProcessId, evt.ThreadId); // No adjacent event to track, yet.

				AssertImportant(soc != null);
				if (soc == null) break;

				rgje = evt.ParseSimpleJsonString(rgstrSourceId_Address);
				if (rgje == null) break;

				srcdep = rgje[0].MyGetNumber();
				this.SocketAttachUID_SrcDep(soc, srcdep, evt);

				if (DNSClient.DNSTable.TryParseWithPort(rgje[1].MyGetString(), out ipAddress, out port))
					soc.addrRemote = new IPEndPoint(ipAddress, port);

				break;

			default:
				// Remember this event as unhandled.
				this.unhandled.Add(evt.TaskName);
				break;
			} // switch evt.TaskName
		} // Dispatch_Connect

		/*
			evt.TaskName must NOT match any of the other standard in/prefix strings.
		*/
		void Dispatch_Misc(in IGenericEvent evt)
		{
			int srcdep;
			int err;
			uint cb;
			Request req;
			Socket soc;
			TimestampUI timeStamp;
			JsonElement[] rgje;

			switch (evt.TaskName)
			{
			// ID -> REQUEST_ALIVE, URL_REQUEST_START_JOB, etc.
			// NOTE: This is (the first and) the last time that we see any activity on this Request.
			case "CORS_REQUEST":
				if (evt.IsEndPhase())
				{
					req = this.RequestFromUID(in evt, false);
					AssertImportant(req != null);
					if (req == null) break;

					req.Gone = true;
					this.GarbageCollect(in evt);

					break;
				}

				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				// REQUEST_ALIVE: Sometimes this UID is different from that one.
				this.SetRecentUID(in evt);

				break;

			// ID -> WEBSOCKET_STATE_CHANGED
			// NOTE: This event effectively takes the place of CORS_REQUEST for a WebSocket Channel.
			case "WEBSOCKET_ALIVE":
				if (evt.IsEndPhase())
				{
					req = this.RequestFromUID(in evt, false);
					AssertImportant(req != null);
					if (req == null) break;
					if (!req.Closed) break;

					req.Gone = true;
					this.GarbageCollect(in evt);

					break;
				}

				AssertImportant(evt.CheckSourceType("WEBSOCKET_CHANNEL"));
				AssertImportant(evt.ParseSimpleJsonString(new[]{"state"})?[0].MyGetString() == "FRESHLY_CONSTRUCTED"); // else see the event: WEBSOCKET_STATE_CHANGED

				// for: REQUEST_ALIVE
				this.SetRecentUID(in evt);

				break;

			// ID -> CORS_REQUEST, URL_REQUEST_START_JOB, HTTP_STREAM_REQUEST, HTTP_STREAM_JOB_CONTROLLER_BOUND, HTTP_STREAM_REQUEST_BOUND_TO_JOB, HTTP_STREAM_REQUEST_BOUND_TO_QUIC_SESSION
			// "priority":"<PRIORITY>", "url":"<url>"
			// This is the first event of interest in a Request, and the event for which we capture a StackWalk.
			case "REQUEST_ALIVE":
				timeStamp = evt.Timestamp.ToGraphable();

				if (!evt.IsBeginPhase())
				{
					req = this.RequestFromUID(in evt, false);
					AssertImportant(req != null);
					if (req == null) break;

					req.SetNetError(in evt);
					if (req.timeStampEndJob.HasMaxValue())
						req.timeStampEndJob = timeStamp; // just in case

					break;
				}

				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				// If the previous event was: WEBSOCKET_ALIVE
				UIDVal uid = this.GetRecentUID("CORS_REQUEST", in evt);

				bool _fWebSocket = false;

				if (uid == 0)
				{
					uid = this.GetRecentUID("WEBSOCKET_ALIVE", in evt);
					if (uid != 0)
						_fWebSocket = true;
				}

				AssertImportant(this.RequestFromUID(in evt) == null);

				rgje = evt.ParseSimpleJsonString(rgstrURL_Priority);
				if (rgje == null) break;

				string strURL = rgje[0].MyGetString();

				req = new Request(strURL, in evt)
				{
					fWebSocket = _fWebSocket,
					priority = rgje[1].MyGetString().GetPriority(),
					timeStampBeginJob = timeStamp // to be overwritten by: URL_REQUEST_START_JOB
				};

				req.xlink.GetLink(evt.ThreadId, timeStamp, in this.allTables.threadTable);

				this.Add(req);
				this.RequestAttachUID(req, in evt);

				if (uid != 0 && uid != evt.GetUID())
					this.RequestAttachUID(req, uid, in evt);

				break;

			// ID -> REQUEST_ALIVE, etc.
			// SrcDep -> HTTP_STREAM_JOB_CONTROLLER_BOUND, HTTP_STREAM_JOB_BOUND_TO_REQUEST
			// source_type: URL_REQUEST
			// NOTE: Add the SrcDep to the Request identified by the ID for future correlation.
			case "CREATED_BY":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				req = this.RequestFromUID_SrcDep(in evt);

				break;

			// ID -> the thing being canceled
			// source_type: SOCKET, URL_REQUEST, HTTP_STREAM_JOB, CERT_VERIFIER_JOB
			// One of several things is canceled, depending on the source_type.
			case "CANCELLED":
				AssertImportant(evt.IsInstantPhase());

				switch (evt.GetSourceType())
				{
				case "SOCKET":
					soc = this.SocketFromUID(in evt, false);
					if (soc == null) break;

					// This event is followed by: SOCKET_CLOSED
					soc.fCanceled = true;

					break;

				case "URL_REQUEST":
					// The Request may have already been closed by: URL_REQUEST_START_JOB.End
					req = this.RequestFromUID(in evt, false);
					AssertImportant(req != null);
					if (req == null) break;

					req.SetNetError(in evt);
					req.fCanceled = true;

					break;
#if DEBUG
				case "HTTP_STREAM_JOB":
					break;

				case "HOST_RESOLVER_IMPL_JOB":
					break;

				case "SSL_CONNECT_JOB":
					break;

				case "CERT_VERIFIER_JOB":
				case "PAC_FILE_DECIDER":
					break;

				default:
					AssertImportant(false); // Else what?
					break;
#endif // DEBUG
				}
				break;

			// 'Instant' version:
			// ID -> HOST_RESOLVER_MANAGER_JOB, etc.
			// SrcDep -> SOCKET_ALIVE
			// source_type: HOST_RESOLVER_IMPL_JOB
			// NOTE: If the SrcDep of a SOCKET_ALIVE/UDP_CLIENT_SOCKET matches this one then it is a DNS Socket.
			case "DNS_TRANSACTION":
				if (!evt.IsInstantPhase())
					break;

				AssertImportant(evt.CheckSourceType("HOST_RESOLVER_IMPL_JOB")); // else what?

				srcdep = evt.GetSourceId();
				this.AddDNSSrcDep(in evt, srcdep);

				break;

			// ID -> SOCKET_ALIVE/UDP_SOCKET
			// source_type: UDP_SOCKET
			case "UDP_LOCAL_ADDRESS":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("UDP_SOCKET"));

				soc = this.SocketFromUID(in evt);
				if (soc == null) break; // common

				if (!soc.addrLocal.Empty()) break;

				rgje = evt.ParseSimpleJsonString(rgstrAddress);
				if (rgje == null) break;

				soc.SetAddrLocalRemote(rgje[0].MyGetString(), null);

				break;

			// ID -> REQUEST_ALIVE, URL_REQUEST_START_JOB
			// "total_size":#, "is_chunked":bool, "net_error":#
			// NOTE: For POST Requests, get the size of the object being uploaded.
			// NOTE: cf. URL_REQUEST_JOB_FILTERED_BYTES_READ
			case "UPLOAD_DATA_STREAM_INIT":
				if (!evt.IsEndPhase())
				{
					AssertImportant(evt.CheckSourceType("URL_REQUEST"));
					break;
				}

				req = this.RequestFromUID(in evt);
				AssertInfo(req != null);
				if (req == null) break;

				rgje = evt.ParseSimpleJsonString(rgstrSize_Chunked_Error);
				if (rgje == null) break;

				err = rgje[2].MyGetNumber(int.MinValue);

				// This event should have: net_error & is_chunked & total_size, or net_error alone, or nothing.
				// If there is no net_error then the event contains nothing of interest.
				if (err == int.MinValue) break;

				cb = rgje[0].MyGetUNumber(0);
				AssertCritical((int)cb >= 0);

				if (err != 0)
					req.iError = err;
				else if (rgje[1].MyGetBool()) // is_chunked
					req.fChunkedUpload = true;
				else
					req.cbUpload += cb;

				AssertImportant(!req.fChunkedUpload); // else invalid cbSend?

				// HTTP1: A placeholder Session & Stream were already created.
				// HTTP2/3: The Session was already created. The Stream will be created later.
				if (req.stream == null)
				{
					AssertImportant(req.Type == StreamType.HTTP2 || req.Type == StreamType.QUIC);
					break;
				}

				AssertImportant(req.Type == StreamType.HTTP1);
				AssertImportant(req.Session != null && req.Session.Type == StreamType.HTTP1);

				if (err != 0)
					req.stream.iError = err;
				else if (rgje[1].MyGetBool()) // is_chunked
					req.stream.fChunkedUpload = true;
				else
					req.stream.cbUpload += cb;

				AssertImportant(!req.stream.fChunkedUpload); // else invalid dbSend?

				req.stream.SetLastTime(evt.Timestamp.ToGraphable());

				break;

			case "WEBSOCKET_SENT_FRAME_HEADER":
			case "WEBSOCKET_RECV_FRAME_HEADER":
				AssertImportant(evt.IsInstantPhase());
				AssertImportant(evt.CheckSourceType("URL_REQUEST"));

				req = this.RequestFromUID(in evt, false);
				AssertImportant(req != null);
				if (req == null) break;

				AssertImportant(!req.Gone);
				AssertImportant(req.fWebSocket || req.URLScrub.StartsWith("wss:"));
				req.fWebSocket = true;

				rgje = evt.ParseSimpleJsonString(rgstrOpcode_Payload);
				if (rgje == null) break;

				// opcode >= 8 are control frames
				if (rgje[0].MyGetUNumber() >= 8) break;

				cb = (uint)rgje[1].MyGetDecimal();
				if (cb == 0) break;

				if (evt.TaskName.Equals("WEBSOCKET_SENT_FRAME_HEADER"))
				{
					req.cbUpload += cb;
					if (req.stream != null)
						req.stream.cbUpload += cb;
				}
				else
				{
					req.cbDownload += cb;
					if (req.stream != null)
						req.stream.cbDownload += cb;
				}

				break;

			case "SSL_CONNECT":
				if (!evt.IsEndPhase())
				{
					AssertImportant(evt.CheckSourceType("SOCKET"));
					break;
				}

				soc = this.SocketFromUID(in evt);
				if (soc == null) break; // Canceled

				soc.fSSL = true;

				rgje = evt.ParseSimpleJsonString(rgstrProto2);
				if (rgje == null) break;

				string proto = rgje[0].MyGetString();
				if (string.IsNullOrWhiteSpace(proto))
				{
					err = evt.GetNetError();
					soc.iError = err;
					break;
				}

				AssertImportant(proto.Equals("h2") || proto.Equals("http/1.1") || proto.Equals("unknown"));
				AssertImportant(soc.Type == StreamType.TCP);

				soc.Type = (proto.Equals("h2") ? StreamType.HTTP2 : StreamType.HTTP1);

				break;

			default:
				// Remember this event as unhandled.
				this.unhandled.Add(evt.TaskName);
				break;
			} // switch evt.TaskName
		} // Dispatch_Misc


		/*
			DEBUG-only: Emit a text file of <AnnotationQueryEntries> that can be pasted into a WPA View Profile: .wpaProfile
			The text file goes to: %TEMP%\<ETL_File_Name>.Annotations.txt
			It contains a WPA annotation (<AnnotationQueryEntry>) for each Request, which will attach in WPA to the ETL events related to that Request.
			The name of each annotation will be the timestamp of the Request's event: URL_REQUEST_START_JOB

			Paste the generated XML for <AnnotationOptionsParameter> within the "Annotation" Column's XML in the .wpaProfile:
				<Column Guid="..." Name="Annotation" ...>
				  <!-- Paste the generated XML here as follows: --->
				  <AnnotationsOptionsParameter>
				    <AnnotationQueryEntries>
				      <AnnotationQueryEntry Annotation="35.417239" AnnotationQuery="[Field 3]:=3,190,708,989,122,975,144 OR [Field 3]:= 3,190,708,989,122,975,138 OR ..." />
				      <!-- etc. -->
				    </AnnotationQueryEntries>
				  </AnnotationsOptionsParameter>
				  <!-- End Paste -->
				</Column>

			WARNING: WPA can be VERY slow with more than a few dozen Annotation Queries.
		*/
		[Conditional("DEBUG")]
		public void _EmitViewProfileAnnotationQueriesDB()
		{
			System.Text.StringBuilder sb = new(512);

			string pathETL = this.allTables.traceMetadata.TracePath;
			sb.AppendFormat("{0}{1}.Annotations.txt", Path.GetTempPath(), Path.GetFileNameWithoutExtension(pathETL));

			using (StreamWriter writer = new StreamWriter(sb.ToString())) // could throw
			{
				string strTab = "                  "; // 18

				// Prolog to the <AnnotationQueryEntry> tags:
				writer.WriteLine("            <!-- Column Name=\"Annotation\" ... -->");
				writer.Write(strTab);
				writer.WriteLine("<AnnotationsOptionsParameter>");
				writer.Write(strTab);
				writer.WriteLine("  <AnnotationQueryEntries>");

				// Write an <AnnotationQueryEntry> for each Request, where the name is the opening timestamp:

				foreach (Request req in this)
				{
					sb.Clear();
					sb.Append("    <AnnotationQueryEntry Annotation=");
					sb.AppendFormat("\"{0}\"", (req.timeStampBeginJob.ToMicroseconds / 1_000_000.0).ToString("0.000000")); // Name = timestamp
					sb.Append(" AnnotationQuery=\"");
					sb.AppendJoin(" OR ", System.Linq.Enumerable.Select(req.rguidDB, uID => string.Format("[Field 3]:={0:#,#}", uID))); // clever but not too efficient
					sb.Append("\" />");

					writer.Write(strTab);
					writer.WriteLine(sb.ToString());
				}

				// Epilog to the <AnnotationQueryEntry> tags:
				writer.Write(strTab);
				writer.WriteLine("  </AnnotationQueryEntries>");
				writer.Write(strTab);
				writer.WriteLine("</AnnotationsOptionsParameter>");
				writer.WriteLine("            <!-- /Column -->");
			} // close writer
		} // _EmitViewProfileAnnotationQueriesDB

		[Conditional("DEBUG")]
		public void EmitViewProfileAnnotationQueriesDB()
		{
			try
			{
				_EmitViewProfileAnnotationQueriesDB();
			}
			catch
			{
				// too bad, so sad
			}
		}

	} // class TraceTable
} // namespace NetBlameCustomDataSource.Chromium
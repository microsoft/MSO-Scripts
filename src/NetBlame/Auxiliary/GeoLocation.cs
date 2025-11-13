// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using System.IO;

using System.Text.Json;
using System.Text.Json.Serialization;

using static NetBlameCustomDataSource.Util;

namespace NetBlameCustomDataSource
{
/*
	This component uses the free IP-GeoLocation API from ip-api.
	- Service:      https://ip-api.com
	- Attribution:  Geolocation data provided by ip-api.com
	- License:      Free for non-commercial use only
	- Rate limit:   45 requests per minute (free tier)
*/
	static class GeoLocation
	{
		public static string Attribution => "\nIP GeoLocation powered by ip-api.com – the free, non-commercial API.\nhttps://www.ip-api.com";

		static string strGeoService = "ip-api.com";
		static string strGetGeoService1 = "http://ip-api.com/json/";
		static string strGetGeoService2 = "?fields=status,message,country,regionName,city";

		public class Place
		{
			[JsonPropertyName("status")]
			public string Status { get; set; }

			[JsonPropertyName("message")]
			public string Message { get; set; }

			[JsonPropertyName("country")]
			public string Country { get; set; }

			[JsonPropertyName("regionName")]
			public string Region { get; set; }

			[JsonPropertyName("city")]
			public string City { get; set; }
		}

		/*
			Return a string representing the geolocation of this IPAddress.
			Check first for special ranges representing LocalHost or a Local/Private Network.
			Then contact geoplugin.com to get a geo-location string.
		*/
		public static string GetGeoLocation(IPAddress ipAddr)
		{
			if (ipAddr == null)
				return string.Empty;

			string strAddrType = Util.AddressType(ipAddr);
			if (strAddrType != null)
				return strAddrType;

			if (ipAddr.IsIPv4MappedToIPv6)
				ipAddr = ipAddr.MapToIPv4();

			string strRequest = strGetGeoService1 + ipAddr.ToString() + strGetGeoService2;

			Place place = null;

			try
			{
				HttpClient client = new HttpClient();
				using HttpResponseMessage response = client.GetAsync(strRequest).GetAwaiter().GetResult();

				if (response.StatusCode == HttpStatusCode.TooManyRequests)
					return "Too Many Requests: " + strGeoService;

				AssertImportant(response.StatusCode == HttpStatusCode.OK);

				response.EnsureSuccessStatusCode(); // may throw

				Stream stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
				AssertImportant(stream?.CanRead ?? false);

				// Parse the response JSON into: Country / Region / City

				if (stream?.CanRead ?? false)
					place = JsonSerializer.Deserialize<Place>(stream);
			}
			catch (System.Exception x)
			{
				return x.Message;
			}

			if (place == null)
				return Util.strNA; // N/A

			if (!place.Status.Equals("success"))
				return place.Message;

			// Turn the three location strings into one.

			System.Text.StringBuilder sbLocation = new System.Text.StringBuilder(32);

			if (!string.IsNullOrWhiteSpace(place.Country))
			{
				if (!string.IsNullOrWhiteSpace(place.Region) && place.Country.Equals("United States"))
					place.Country = "USA";

				sbLocation.Append(place.Country);
			}
			if (!string.IsNullOrWhiteSpace(place.Region)) // This is the State if the Country is USA.
			{
				sbLocation.Append(" / ");
				sbLocation.Append(place.Region);
			}
			if (!string.IsNullOrWhiteSpace(place.City))
			{
				sbLocation.Append(" / ");
				sbLocation.Append(place.City);
			}

			AssertImportant(sbLocation.Length > 0);
			if (sbLocation.Length == 0)
				return Util.strNA; // N/A

			return sbLocation.ToString();
		} // GetGeoLocation
	} // class GeoLocation
}

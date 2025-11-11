// Copyright(c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http;
using System.Xml;
using System.IO;

using static NetBlameCustomDataSource.Util;

namespace NetBlameCustomDataSource
{
	// IP Geolocation by geoPlugin: http://www.geoplugin.com/
	// This product includes GeoLite data created by MaxMind, available from: http://www.maxmind.com

	static class GeoLocation
	{
		public static string Attribution => "IP Geolocation by geoPlugin:\n http://www.geoplugin.com/ \nThis product includes GeoLite data created by MaxMind,\navailable from: http://www.maxmind.com";

		static string strGetXmlService = "http://www.geoplugin.net/xml.gp?ip=";

		enum XmlName
		{
			none =        0x00,
			bitfCountry = 0x01,
			bitfRegion =  0x02,
			bitfCity =    0x04,
			bitfStatus =  0x08,
			bitfAll = bitfCountry | bitfRegion | bitfCity | bitfStatus
		};


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

			string strRequest = strGetXmlService + ipAddr.ToString();

			try
			{
				HttpClient client = new HttpClient();
				using HttpResponseMessage response = client.GetAsync(strRequest).GetAwaiter().GetResult();
				response.EnsureSuccessStatusCode(); // may throw
				Stream stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();

				// Parse the response XML into: Country / Region / City

				XmlReader xmlReader = new XmlTextReader(stream);

				string strCountryName = null;
				string strRegionName = null;
				string strCityName = null;
				string strStatus = null;

				XmlName grbitName = XmlName.none;
				string strXmlName = string.Empty;
				while (xmlReader.Read())
				{
					string strValue = null;

					switch (xmlReader.NodeType)
					{
					case XmlNodeType.Element:
						strXmlName = xmlReader.Name;
						continue; // no break test needed

					case XmlNodeType.Text:
						strValue = xmlReader.Value;
						goto case XmlNodeType.Whitespace;

					case XmlNodeType.Whitespace:
						switch (strXmlName)
						{
						case "geoplugin_countryName":
							grbitName |= XmlName.bitfCountry;
							strCountryName = strValue;
							break;
						case "geoplugin_region":
							grbitName |= XmlName.bitfRegion;
							strRegionName = strValue;
							break;
						case "geoplugin_city":
							grbitName |= XmlName.bitfCity;
							strCityName = strValue;
							break;
						case "geoplugin_status":
							grbitName |= XmlName.bitfStatus;
							strStatus = strValue;
							break;
						default:
							continue; // no break test needed
						}
						strXmlName = null;
						break;
					default:
						continue; // no break test needed
					} // switch xmlReader.NodeType

					if (grbitName == XmlName.bitfAll)
						break;
				} // while mlReader.Read

				AssertImportant(grbitName == XmlName.bitfAll); // Saw the expected XML records.

				// Turn the three location strings into one.

				System.Text.StringBuilder sbLocation = new System.Text.StringBuilder(32);

				if (strCountryName != null)
				{
					if (strRegionName != null && strCountryName == "United States")
						strCountryName = "USA";

					sbLocation.Append(strCountryName);
				}
				if (strRegionName != null) // This is the State if the Country is USA.
				{
					sbLocation.Append(" / ");
					sbLocation.Append(strRegionName);
				}
				if (strCityName != null)
				{
					sbLocation.Append(" / ");
					sbLocation.Append(strCityName);
				}
				if (strStatus != null && sbLocation.Length == 0)
				{
					sbLocation.Append("Geo status: ");
					sbLocation.Append(strStatus);
				}

				return sbLocation.ToString();
			}
			catch
			{
				return string.Empty;
			}
		} // GetGeoLocation
	} // class GeoLocation
}

Copyright (c) Microsoft Corporation. Licensed under the MIT License.

# NetBlame Add-in

This add-in analyzes and summarizes network and thread pool ETW events:

	Microsoft-Windows-Winsock-NameResolution	{55404e71-4db9-4deb-a5f5-8f86e46dde56}
	Microsoft-Windows-Winsock-AFD	{e53c6823-7bb8-44bb-90dc-3f86090d48a6}
	Microsoft-Windows-DNS-Client	{1c95126e-7eea-49a9-a3fe-a378b03ddb4d}
	Microsoft-Windows-WinINet	{43d1a55c-76d6-4f7e-995c-64c711e5cafe}
	Microsoft-Windows-WinHttp	{7d44233d-3055-4b9c-ba64-0d47ca40a232}
	Microsoft-Windows-WebIO	{50b3e73c-9370-461d-bb9f-26f32d68887d}
	Microsoft-Windows-TCPIP	{2f07e2ee-15db-40f1-90ef-9d7ba282188a}
	Windows-ThreadPool	{c861d0e2-a2c1-4d36-9f9c-970bab943a12}
	Office-ThreadPool	{A019725F-CFF1-47E8-8C9E-8FE2635B6388}
	OfficeDispatchQueue	{559A5658-8100-4D84-B756-0A47A476280C}

It is based on the [Microsoft Performance Toolkit SDK](https://github.com/microsoft/microsoft-performance-toolkit-sdk)

This product includes GeoLite2 data created by MaxMind, available from https://www.maxmind.com

# Build

When a Release of this project is downloaded and unzipped, the NetBlame plug-in is ready to go.

When this repository is cloned, the NetBlame plug-in must be built.

See: https://github.com/microsoft/MSO-Scripts/wiki/Network-Activity#plugin

`DotNet.exe build -c Release`

# Run

Requires WPA v11.7.383+ and SDK v1.2.2+

`c:\MSO-Scripts\BETA\TraceNetwork View`

The above script executes this WPA command to load the NetBlame plug-in and process the ETW trace:

```
WPA -i "$Env:LocalAppData\MSO-Scripts\MSO-Trace-Network.etl"
-processors "Event Tracing for Windows","Office_NetBlame"
-addsearchdir "c:\MSO_Scripts\NetBlame\bin\Release\net6.0"
-profile "c:\MSO-Scripts\BETA\WPAP\Network.wpaProfile"
```

The script chooses one of these paths for -addsearchdir :
- `"c:\MSO_Scripts\NetBlame\bin\Release\net6.0"`
- `"c:\MSO_Scripts\NetBlame\bin\Debug\net6.0"`
- `"c:\MSO_Scripts\BETA\ADDIN"`

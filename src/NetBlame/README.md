Copyright (c) Microsoft Corporation. Licensed under the MIT License.

# NetBlame Plug-in

This plug-in analyzes and summarizes network and thread pool ETW events:

	{43d1a55c-76d6-4f7e-995c-64c711e5cafe} : Microsoft-Windows-WinINet
	{7d44233d-3055-4b9c-ba64-0d47ca40a232} : Microsoft-Windows-WinHttp
	{50b3e73c-9370-461d-bb9f-26f32d68887d} : Microsoft-Windows-WebIO
	{e53c6823-7bb8-44bb-90dc-3f86090d48a6} : Microsoft-Windows-Winsock-AFD
	{2f07e2ee-15db-40f1-90ef-9d7ba282188a} : Microsoft-Windows-TCPIP

	{55404e71-4db9-4deb-a5f5-8f86e46dde56} : Microsoft-Windows-Winsock-NameResolution
	{1c95126e-7eea-49a9-a3fe-a378b03ddb4d} : Microsoft-Windows-DNS-Client

	{c861d0e2-a2c1-4d36-9f9c-970bab943a12} : Windows-ThreadPool
	{A019725F-CFF1-47E8-8C9E-8FE2635B6388} : Office-ThreadPool
	{559A5658-8100-4D84-B756-0A47A476280C} : OfficeDispatchQueue

	{d2d578d9-2936-45b6-a09f-30e32715f42d} : Google.Chrome
	{3A5F2396-5C8F-4F1F-9B67-6CCA6C990E61} : Microsoft.MSEdgeStable
	{BD089BAA-4E52-4794-A887-9E96868570D2} : Microsoft.MSEdgeBeta
	{C56B8664-45C5-4E65-B3C7-A8D6BD3F2E67} : Microsoft.MSEdgeCanary
	{D30B5C9F-B58F-4DC9-AFAF-134405D72107} : Microsoft.MSEdgeDev
	{E16EC3D2-BB0F-4E8F-BDB8-DE0BEA82DC3D} : Microsoft.MSEdgeWebView2

See [the wiki](https://github.com/microsoft/MSO-Scripts/wiki/Network-Activity) for detailed information.

## Credits

The NetBlame plug-in is based on the [Microsoft Performance Toolkit SDK](https://github.com/microsoft/microsoft-performance-toolkit-sdk)

This product includes GeoLocation data created by ip-api, available at https://ip-api.com

<a name="build"></a>

## Setup
When a [Release of this repository](https://github.com/microsoft/MSO-Scripts/releases) is **downloaded and unzipped**, the NetBlame plug-in is ready to go.

When this repository is **cloned or copied**, the NetBlame plug-in must be built from its source code.
&nbsp; See: [How to Get the WPA Network Plug-in ('NetBlame')](https://github.com/microsoft/MSO-Scripts/wiki/Network-Activity#plugin)

## Download

* Download and unzip the MSO-Scripts and NetBlame source code (PowerShell option):<br/>
`Invoke-WebRequest -uri "https://github.com/microsoft/MSO-Scripts/archive/refs/heads/main.zip" -outfile "$Env:TEMP\MSO-Scripts.zip"`<br/>
`Expand-Archive -path "$Env:TEMP\MSO-Scripts.zip" -destinationpath "$Env:TEMP"`<br/>
`mv "$Env:TEMP\MSO-Scripts-main\" "c:\MSO-Scripts\"`

* If needed, install DotNet (v8 or higher) (_only required when building the 'NetBlame' WPA Plug-In locally_):<br/>
`winget install --id Microsoft.DotNet.Runtime.10`<br/>
`winget install --id Microsoft.DotNet.SDK.10`

## Build

* Build the WPA Plug-in:<br/>
`cd "c:\MSO-Scripts\src\NetBlame"`<br/>
RELEASE:<br/>
`& $Env:ProgramFiles\dotnet\dotnet build -c Release`<br/>
OR DEBUG:<br/>
`& $Env:ProgramFiles\dotnet\dotnet build -p AUX_TABLES=1 -c Debug`

## Trace

* Collect a Network Trace (requires Administrator privilege):<br/>
`cd "c:\MSO-Scripts\src"`<br/>
`.\BETA\TraceNetwork.bat Start`<br/>
_Launch Chrome or Edge and visit a site._<br/>
`.\BETA\TraceNetwork.bat Stop`

## View

* If needed, download the [Windows Performance Analyzer (WPA)](https://apps.microsoft.com/detail/9n0w1b2bxgnz)<br/>
  _Requires WPA v11.7.383+ using Performance Toolkit SDK v1.2.2+_

* Launch the Viewer:<br/>
`.\BETA\TraceNetwork.bat View`<br/>
  WPA shows the 'Master URL Table'

<br/>

> &#x2139;&#xFE0F; **Info**<br/>
> The `TraceNetwork View` script executes the following WPA command to load the NetBlame plug-in and process the ETW trace:<br/>
> `WPA -i "$Env:LocalAppData\MSO-Scripts\MSO-Trace-Network.etl"`<br/>
> `-processors "Event Tracing for Windows","Office_NetBlame"`<br/>
> `-addsearchdir "c:\MSO-Scripts\src\NetBlame\bin\Release\net6.0"`<br/>
> `-profile "c:\MSO-Scripts\src\BETA\WPAP\Network.wpaProfile"`<br/>
>
> The script chooses one of these paths for -addsearchdir :
> - `"c:\MSO-Scripts\src\NetBlame\bin\Release\net6.0"`
> - `"c:\MSO-Scripts\src\NetBlame\bin\Debug\net6.0"`
> - `"c:\MSO-Scripts\src\BETA\ADDIN"`

<br/>

> &#x26A0;&#xFE0F; **Important**<br/>
> In order to debug a WPA add-in using VSCode, the modules **mscordbi.dll** & **mscordaccore.dll** (adjacent to wpa.exe) must be accessible (Read/Execute)
> by VSCode's debugger process, not just by the WPA process. Therefore, when WPA is installed as a Store App,
> the permissions on those two modules may need to be modified such that the context in which the VSCode Debugger runs can also read and execute them.

## Going Farther
The NetBlame plug-in also provides graphs/tables which expose more details about specific network providers:

* Within WPA: New Tab [+]
* Within WPA's Graph Explorer (Ctrl-G), expand: **> Network**
* Dbl-click any of:
	- Master URL Table - NetBlame (_default_)
	- NetBlame Chromium Requests (_Chrome and Edge_)
	- NetBlame Chromium Streams (`DEBUG`/`AUX_TABLES` _only_)
	- NetBlame DNS Table (_list of servers referenced_)
	- NetBlame TCB Table (_Transfer Control Blocks for TCP/IP_)
	- NetBlame ThreadPool Table (`DEBUG`/`AUX_TABLES` _only_)
	- NetBlame WinHTTP Request Table
	- NetBlame WinINet Table
	- NetBlame WinSock Table

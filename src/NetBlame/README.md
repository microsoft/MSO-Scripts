# Introduction 
The Performance Toolkit SDK allows for you to create AddIns to process your own data inside of Windows Performance Analyzer (WPA).
# Requirements
1. Visual Studio 2019
2. .NET Standard 2.1
3. Access to the PerfToolKit NuGet feed. See [here](https://dev.azure.com/perftoolkit/SDK/_wiki/wikis/SDK.wiki?wikiVersion=GBwikiMaster&pagePath=%2FOverview&pageId=2) for details on connecting to the NuGet feed

# Configuring NuGet

#### Feeds

- URI:  https://dev.azure.com/perftoolkit/SDK/_packaging?_a=feed&feed=PerfToolkit
- Feed URI:  https://pkgs.dev.azure.com/perftoolkit/_packaging/PerfToolkit/nuget/v3/index.json

#### Configuring Visual Studio

To access the NuGet packages from Visual Studio, simply add the feed URI in the Package Manager settings as a new source

- Tools -> NuGet Package Manager -> Package Manager Settings -> Package Sources
- Click the green plus (+) to add a new source.
- Give the feed a descriptive name, e.g. PerfToolKit, in the name field.
- Place the feed URI in the source field
- The feed will now be available from the package manager

# Instructions

1. [Overview](https://dev.azure.com/perftoolkit/SDK/_wiki/wikis/SDK.wiki?wikiVersion=GBwikiMaster&pagePath=%2FOverview&pageId=2)
2. [Creating your project](https://dev.azure.com/perftoolkit/SDK/_wiki/wikis/SDK.wiki?wikiVersion=GBwikiMaster&pagePath=%2FUsing%20the%20SDK%2FCreating%20your%20project&pageId=8)

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

# MSO-Scripts

### _Democratizing Windows Performance Analysis_

## Documentation

MSO-Scripts substantially facilitates the use of Microsoft's Event Tracing for Windows (ETW) technology for analyzing the resource consumption (CPU, Memory, I/O, Handles, Registry, ...) and performance behavior of Windows and its applications, particularly Microsoft Office.

MSO-Scripts also [reveals detailed network activity](https://github.com/microsoft/MSO-Scripts/wiki/Network-Activity) by way of an add-in for the Windows Performance Analyzer (WPA).

Please see [the wiki](../../wiki) for detailed documentation.

## Quick Start

There are eleven customizable scripts for tracing various system resources:
* TraceCPU
* TraceFileDiskIO
* TraceHandles &nbsp; (_Kernel, GDI, and USER handles_)
* TraceHeap &nbsp; &nbsp; &nbsp; &nbsp;(_Windows Heap + VirtualAlloc_)
* TraceHeapEx &nbsp; &nbsp; (_Windows Heap + VirtualAlloc + Handles_)
* TraceMemory&nbsp; &nbsp;(_RAM usage, etc._)
* TraceMondo &nbsp; &nbsp; (_CPU + FileDiskIO + Handles + Network + Defender_) 
* TraceNetwork &nbsp; (_Chromium, WinHTTP, WinINet, LDAP, WinSock, TcpIp_)
* TraceOffice &nbsp; &nbsp; &nbsp; (_Excel, OneNote, PowerPoint, Word: ETW Trace and Office Logs_)
* TraceOutlook &nbsp; (_ETW Trace and Office/Outlook Logs_)
* TraceRegistry

The scripts accept these tracing commands: Start, Stop, View, Status, Cancel<br/>
These commands, except View, require Administrator privilege.

To trace CPU activity:
* Download and unzip [MSO-Scripts](https://github.com/microsoft/MSO-Scripts/archive/refs/heads/main.zip).
* _MSO-Scripts_\\`TraceCPU Start`<br/>
_Exercise the application/scenario._
* _MSO-Scripts_\\`TraceCPU Stop`
* _MSO-Scripts_\\`TraceCPU View` &nbsp; _Launches the [Windows Performance Analyzer](https://devblogs.microsoft.com/performance-diagnostics/wpa-intro/)_

List all options for TraceCPU:
* _MSO-Scripts_\\`TraceCPU -?`

  [See the Wiki: TraceCPU](https://github.com/microsoft/MSO-Scripts/wiki/CPU-and-Threads)

## Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft 
trademarks or logos is subject to and must follow 
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.

Copyright (c) Microsoft Corporation. Licensed under the MIT License.

These are manifest files for Office ETW logging providers.
https://learn.microsoft.com/en-us/windows-hardware/test/weg/instrumenting-your-code-with-etw#implementing-etw-instrumentation

MSO-Scripts registers these providers automatically when needed.

A. MSEdge_Stable   {3A5F2396-5C8F-4F1F-9B67-6CCA6C990E61} EdgeETW.man
B. MSEdge_Canary   {C56B8664-45C5-4E65-B3C7-A8D6BD3F2E67} EdgeETW.man
C. MSEdge_Beta     {BD089BAA-4E52-4794-A887-9E96868570D2} EdgeETW.man
D. MSEdge_Dev      {D30B5C9F-B58F-4DC9-AFAF-134405D72107} EdgeETW.man
E. MSEdge_Internal {49C85E08-E8A5-49D6-81EA-7270531EC8AF} EdgeETW.man
F. MSEdge_WebView  {E16EC3D2-BB0F-4E8F-BDB8-DE0BEA82DC3D} EdgeETW.man
G. Chrome          {D2D578D9-2936-45B6-A09f-30E32715F42D} ChromeETW.man

Present in:
.\WPRP\MSEdge.wprp
..\WPRP\EdgeChrome.wprp

To register the provider in a Trace* script:
- Call: EnsureETWProvider(".\OETW\MsoEtwXX.man")

To manually register the provider on your machine:
- Copy MsoEtwXX.man and MsoEtwXX.res to your machine.
- Run as Admin: wevtutil im <path>\MsoEtwXX.man /rf:"<fullpath>\MsoEtwXX.res" /mf:"<fullpath>\MsoEtwXX.res"

To manually unregister the provider:
- Run as Admin: wevtutil um <path>\MsoEtwXX.man

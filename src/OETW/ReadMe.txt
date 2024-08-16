Copyright (c) Microsoft Corporation. Licensed under the MIT License.

These are manifest files for Office ETW logging providers.
https://learn.microsoft.com/en-us/windows-hardware/test/weg/instrumenting-your-code-with-etw#implementing-etw-instrumentation

MSO-Scripts registers these providers automatically when needed.

A. Microsoft-Office-Events     {8736922d-E8B2-47eb-8564-23E77E728CF3} MsoEtwCM.man
B. OfficeLoggingLiblet         {F50D9315-E17E-43C1-8370-3EDF6CC057BE} MsoEtwCM.man
C. Microsoft-Office-Threadpool {a019725f-cff1-47e8-8c9e-8fe2635b6388} MsoEtwTP.man
D. OfficeDispatchQueue         {559a5658-8100-4d84-b756-0a47a476280c} MsoEtwDQ.man
E. OfficeAirSpace              {f562bb8e-422d-4b5c-b20e-90d710f7d11c} MsoEtwAS.man

Present in:
A. Microsoft-Office-Events is present in most WPRP files. It is (usually) pre-registered. 
B. OfficeProviders.wprp
C. ThreadPool*.wprp, Network*.wprp
D. ThreadPool*.wprp, Network*.wprp
E. OfficeProviders.wprp

To register the provider in a Trace* script:
- Call: EnsureETWProvider(".\OETW\MsoEtwXX.man")

To manually register the provider on your machine:
- Copy MsoEtwXX.man and MsoEtwXX.res to your machine.
- Run as Admin: wevtutil im <path>\MsoEtwXX.man /rf:"<fullpath>\MsoEtwXX.res" /mf:"<fullpath>\MsoEtwXX.res"

To manually unregister the provider:
- Run as Admin: wevtutil um <path>\MsoEtwXX.man

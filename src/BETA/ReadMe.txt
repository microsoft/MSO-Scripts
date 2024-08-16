Copyright (c) Microsoft Corporation. Licensed under the MIT License.

Tracing CPU Counters / Processor Performance Monitor Unit (PMU) Events

https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-pmu-events
https://devblogs.microsoft.com/performance-diagnostics/recording-hardware-performance-pmu-events-with-complete-examples/

- To list available CPU Counters, run: WPR -PMCSources
  Or run: XPerf -PMCSources
  If there is only one counter listed, your machine is probably configured for HYPER-V.

- To add additional CPU-specific counters:
  For Intel CPUs, run: Generate-PmuRegFile -Description
  Then run RegEdit on the generated .REG file. Then restart the OS.

- To trace Cycles per Instruction, run: TracePMC Start -CPI
  Or run: PMC.bat

- To trace Branch Mispredicts and Cache Misses, run: TracePMC Start -PMC *
  Or run: PMCs.bat

- To sample-trace specific CPU counters, run: TracePMC start -PMC Counter1[,Counter2[,...]]
  Or run: PMCs.bat Counter1 [Counter2 [...]]

- To trace specific CPU counters at each CSwitch, run: PMC Counter1 [Counter2 [...]]

- To view the resulting trace file, run: TracePMC View [-Path <path>\<file>.etl]
  Or run: WPA <path>\<file>.etl -profile .\WPAP\CPUCounters.wpaProfile

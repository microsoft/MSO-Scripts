# Support

MSO-Scripts facilitates the use of Microsoft's Event Tracing for Windows ([ETW](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/event-tracing-for-windows--etw-)) technology, including [WPR](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/introduction-to-wpr) and [WPA](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/windows-performance-analyzer).

The scripts are intended to be self-service, [well documented](../../wiki), and highly customizable:
- Start with the 'CUSTOMIZE THIS' section of any script file: Trace*.ps1
- Run any script (Trace*.ps1) with `-Verbose` to reveal its underlying actions and how it invokes WPR or WPA.
- Refer to the wiki topic: [Customize Tracing](../../wiki/Customize-Tracing)

For new combinations of tracing and logging providers to suit your needs, please make use of these resources to create your own customized version.

## How to file issues and get help  

These scripts are designed to simplify complex tracing/logging scenarios on Microsoft Windows and also handle potential error conditions. The ever evolving Windows platform may necessitate occasional script updates and corrections to operate well in common environments. Similarly, [stacktags](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/stack-tags) are based on module and function names, while [regions-of-interested](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/creating-a-regions-of-interest-file) are based on internal logging mechanisms, all of which are subject to change.

Contributions are welcome.

This project uses GitHub Issues to track bugs and feature requests. Please search the existing issues before filing new issues to avoid duplicates.

## Microsoft Support Policy  

Support for this **PROJECT or PRODUCT** is limited to the resources listed above.

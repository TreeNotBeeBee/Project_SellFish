# Third-party components

sell fish uses the components below. The distribution includes their license and notice files.

- .NET 11.0.0: `licenses/DotNet-LICENSE.txt` and `licenses/DotNet-THIRD-PARTY-NOTICES.txt`.
- Windows App SDK / WinUI 2.7.0: `licenses/WindowsAppSDK-LICENSE.txt`.
- Windows SDK .NET projection 10.0.26100.87: `licenses/WindowsSDK-LICENSE.rtf`, downloaded from the exact package's [license link](https://aka.ms/WinSDKLicenseURL).
- WebView2 1.0.3719.77 (WinUI transitive dependency): `licenses/WebView2-LICENSE.txt` and `licenses/WebView2-NOTICE.txt`. WindowPilot does not create a browser control.
- Win2D 1.4.0: [NuGet package](https://www.nuget.org/packages/Microsoft.Graphics.Win2D/1.4.0); upstream [MIT license](https://github.com/microsoft/Win2D/blob/winappsdk/main/LICENSE.txt), included as `licenses/Win2D-upstream-LICENSE.txt`. The package metadata links its binary license terms at `http://www.microsoft.com/web/webpi/eula/eula_win2d_10012014.htm`.
- Interop.UIAutomationClient 10.19041.0 provides managed declarations for the Windows UI Automation interfaces. Its MIT license is included in `licenses/UIAutomation-Interop-LICENSE.txt`. [NuGet package](https://www.nuget.org/packages/Interop.UIAutomationClient/10.19041.0)

Build-only dependency: Microsoft.Windows.SDK.BuildTools 10.0.28000.2957. No Windows SDK build tools are redistributed with the application.

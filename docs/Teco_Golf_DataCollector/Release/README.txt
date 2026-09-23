Teco_Golf_DataCollector Release DLL

目標框架：.NET 8
建置組態：Release
System.IO.Ports：NuGet 10.0.5（Windows net8.0 DLL）

本資料夾包含應用端使用 Data Collector 所需的 DLL：

Teco_Golf_DataCollector.dll
CS_Communication_Golf_DDC.dll
CS_Communication_Hanbell.dll
CS_Communication_Modbus.dll
CS_Protocol_Modbus.dll
CS_GeneralNS.dll
CS_Network_CommonUse.dll
System.IO.Ports.dll

使用方式：
1. 將全部 DLL 放入應用程式輸出目錄，或放入手冊第一章說明的 lib 目錄。
2. 在 .NET 8 應用程式中引用需要的 DLL；建議使用手冊提供的 *.csproj 參考方式。
3. 執行時保留應用程式自己的 .deps.json 與 .runtimeconfig.json。

本交付不包含原始碼、底層 .csproj、NuGet 快取或 Console 執行檔。

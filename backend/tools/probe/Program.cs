using CS_NS_Communication_Golf_DDC;
using CS_NS_Communication_Modbus;
using CS_NS_Teco_Golf_DataCollector;
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Teco.Hvac.Probe
{
    /// <summary>
    /// P0 技術驗證探針：
    /// 1) 確認 net10.0 專案能編譯並載入供應商交付的 8 個 net8.0/netstandard2.0 DLL。
    /// 2) 確認在目前執行環境（含 Linux 容器）建立 Collector、註冊事件、Start/Stop 不會拋出
    ///    PlatformNotSupportedException / DllNotFoundException 等平台相容性例外。
    /// 3) 若目標網段可達（--connect 旗標），實際嘗試連線並回報 ConnectionState /
    ///    IsReadSuccess，驗證資料真的能收到。
    /// 未指定 --connect 時，程式只驗證「能不能建立/啟動/停止」，不強求連線成功
    ///（開發機通常連不到 192.168.10.0/24，這是預期的，不代表失敗）。
    /// </summary>
    internal static class Program
    {
        private static int _connectionEvents;
        private static int _dataEvents;

        private static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            bool waitForRealConnection = Array.IndexOf(args, "--connect") >= 0;

            Console.WriteLine("=== Teco Golf Data Collector — P0 探針 ===");
            Console.WriteLine($"執行環境：{RuntimeInformation.OSDescription}");
            Console.WriteLine($"RID：{RuntimeInformation.RuntimeIdentifier}");
            Console.WriteLine($"Framework：{RuntimeInformation.FrameworkDescription}");
            Console.WriteLine();

            try
            {
                using CS_TecoGolf_DataCollector collector = new CS_TecoGolf_DataCollector();
                Console.WriteLine("[OK] CS_TecoGolf_DataCollector 建構成功（組件已成功載入）。");

                collector.EventConnectionStatusChanged += Collector_EventConnectionStatusChanged;
                collector.EventDataReceived += Collector_EventDataReceived;

                CS_ConnectPath hanbellPath = new CS_ConnectPath("192.168.10.198", "4196");
                hanbellPath.Externed = "9600";
                collector.HanbellConnectPath = hanbellPath;
                collector.DDC1ConnectPath = new CS_ConnectPath("192.168.10.12", "502");
                collector.DDC2ConnectPath = new CS_ConnectPath("192.168.10.14", "502");
                Console.WriteLine("[OK] 三條連線路徑設定成功。");

                collector.Start();
                Console.WriteLine("[OK] Start() 呼叫成功，未拋出例外。");
                Console.WriteLine($"IsStarted = {collector.IsStarted}");

                int waitSeconds = waitForRealConnection ? 30 : 8;
                Console.WriteLine($"觀察 {waitSeconds} 秒的連線／資料事件...");
                Thread.Sleep(TimeSpan.FromSeconds(waitSeconds));

                collector.Stop();
                collector.EventDataReceived -= Collector_EventDataReceived;
                collector.EventConnectionStatusChanged -= Collector_EventConnectionStatusChanged;
                Console.WriteLine("[OK] Stop() 與事件解除成功，未拋出例外。");

                Console.WriteLine();
                Console.WriteLine($"共收到 {_connectionEvents} 次連線狀態事件、{_dataEvents} 次資料事件。");

                if (waitForRealConnection && _connectionEvents == 0)
                {
                    Console.WriteLine("[FAIL] --connect 模式下完全沒有收到任何連線事件，請檢查網路。");
                    Environment.Exit(2);
                }

                Console.WriteLine();
                Console.WriteLine("[PASS] 平台相容性驗證通過：組件載入、建構、Start/Stop 生命週期" +
                    "在本環境（" + RuntimeInformation.RuntimeIdentifier + "）皆正常，" +
                    "未出現 PlatformNotSupportedException 或 DllNotFoundException。");
            }
            catch (DllNotFoundException ex)
            {
                Console.WriteLine($"[FAIL] DllNotFoundException：{ex.Message}");
                Console.WriteLine("→ 通常代表原生相依（例如 System.IO.Ports 的平台實作）在本環境缺失。");
                Environment.Exit(1);
            }
            catch (PlatformNotSupportedException ex)
            {
                Console.WriteLine($"[FAIL] PlatformNotSupportedException：{ex.Message}");
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] 未預期例外：{ex.GetType().FullName}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Environment.Exit(1);
            }
        }

        private static void Collector_EventConnectionStatusChanged(object? sender, EventArgsConnectStatus e)
        {
            Interlocked.Increment(ref _connectionEvents);
            Console.WriteLine(
                $"[連線事件] {e.TriggerTime:HH:mm:ss.fff} 通道={e.ConnectionChannel} " +
                $"狀態={e.ConnectionState} IsConnected={e.IsConnected} " +
                $"IP={e.ConnectPath.ID}:{e.ConnectPath.Path}");
        }

        private static void Collector_EventDataReceived(object? sender, EventArgsDataReceived e)
        {
            Interlocked.Increment(ref _dataEvents);
            Console.WriteLine(
                $"[資料事件] {e.UpdateTime:HH:mm:ss.fff} " +
                $"Hanbell1.ReadStatus={e.HanbellStatus1.ReadStatus} " +
                $"Hanbell2.ReadStatus={e.HanbellStatus2.ReadStatus} " +
                $"DDC1.ReadStatus={e.DDC1Status.ReadStatus} (FCU={e.DDC1Status.FCUList.Count}) " +
                $"DDC2.ReadStatus={e.DDC2Status.ReadStatus} (FCU={e.DDC2Status.FCUList.Count})");
        }
    }
}

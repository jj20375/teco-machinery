# 1. Hyper-V VM 建置（Windows 宿主）

> 第一次部署的完整順序（建 VM → 裝 Docker → 同步專案 → 搬資料庫 → 啟動 → 驗證）見
> [`docs/正式機首次部署手冊.md`](../../../docs/正式機首次部署手冊.md)，這份只負責 VM 本身。

前提：Windows Server 2019/2022/2025，或 Windows 10/11 **Pro/Enterprise**（Home 版無 Hyper-V，
改走 `../../docs/BACKEND_INTEGRATION_PLAN.md` 附錄的 WSL2 退路）。

## 1.1 啟用 Hyper-V

```powershell
# 系統管理員 PowerShell
Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All
Restart-Computer
```

> 實際部署時，VM 可以**先掛在 Hyper-V 內建的 Default Switch（NAT）**，先完成部署與驗證，
> 等要接設備時再改成這裡的 External 交換器。Default Switch 的網段重開機可能改變，**不要在它上面設固定 IP**。
> 改交換器與固定 IP 的完整步驟（含只有一張網卡時的遠端斷線風險、退回方式）見
> [`docs/正式機首次部署手冊.md`](../../../docs/正式機首次部署手冊.md) 附錄 B。

## 1.2 建立 External Virtual Switch（接現場設備網段）

現場電腦有雙網段：主 IP `192.168.50.199`、進階 IP `192.168.10.199`（三個 Modbus 通道都在
`192.168.10.0/24`）。VM 需要能直接路由到這個網段。

```powershell
# 查詢實體網卡名稱
Get-NetAdapter

# 綁定到 192.168.10.0/24 那張網卡（依實際名稱替換 "Ethernet 2"）
New-VMSwitch -Name "Teco-Field-Switch" -NetAdapterName "Ethernet 2" -AllowManagementOS $true
```

若現場只有一張網卡同時接兩個網段（VLAN 或路由器分流），改用該網卡建立 vSwitch 即可；
容器透過 VM 的路由表存取 192.168.10.0/24。

## 1.3 建立 VM

```powershell
$vmName = "teco-hvac-linux"
New-VM -Name $vmName -MemoryStartupBytes 12GB -Generation 2 `
    -NewVHDPath "D:\VMs\$vmName\$vmName.vhdx" -NewVHDSizeBytes 200GB `
    -SwitchName "Teco-Field-Switch"

Set-VMProcessor -VMName $vmName -Count 4
Set-VM -VMName $vmName -AutomaticStartAction Start -AutomaticStopAction ShutDown
Set-VMFirmware -VMName $vmName -EnableSecureBoot Off   # Ubuntu Server 開機需要，或改用支援簽章的開機檔

# 掛載 Ubuntu Server 24.04 LTS ISO 後啟動安裝
Set-VMDvdDrive -VMName $vmName -Path "D:\ISO\ubuntu-24.04-live-server-amd64.iso"
Start-VM -VMName $vmName
```

用 Hyper-V 管理員的「連線」視窗完成 Ubuntu Server 安裝精靈：
- 設定靜態 IP：連到 `Teco-Field-Switch` 的介面設為 `192.168.10.200/24`，Gateway 依現場路由器
- 開啟 OpenSSH Server（安裝精靈有勾選項）
- 建立一般使用者（例如 `teco`），**不要**用 root 直接登入

若需要第二張網卡讓 Mac／維運端從 `192.168.50.0/24` 存取，另建一個 vSwitch 綁該網段的網卡，
在 Hyper-V 設定裡幫 VM 加一張虛擬網卡並掛上去，於 Ubuntu 內用 netplan 設定第二個介面。

## 1.4 驗證網路

```bash
# 在 VM 內
ip addr show
ping -c3 192.168.10.198   # Gateway
ping -c3 192.168.10.12    # DDC1
ping -c3 192.168.10.14    # DDC2
```

三個都要能 ping 通，才代表 P0-3（容器能連到現場網段）的前提成立。

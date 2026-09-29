# ⚠️ 本檔必須存成 UTF-8 with BOM：Windows 內建的 PowerShell 5.1 會把沒有 BOM 的檔案當成系統編碼讀，中文會變亂碼。
#
# 把 VM 裡每天產生的資料庫備份（~/backup/teco_hvac_*.sql.gz）拉到 Windows 主機。
#
# 為什麼要拉出來：VM 被刪、VM 的虛擬硬碟檔被刪時，VM 裡的備份會一起消失；放一份在 Windows 上
# 才救得回來。⚠️ 這台主機只有 C 槽，Windows 上的備份跟 VM 在同一顆實體硬碟，硬碟壞掉仍會
# 一起消失——這一層只防「誤刪 VM」，一定還要再複製到這台主機以外（見部署手冊附錄 D）。
#
# 用法（系統管理員 PowerShell，第一次先手動跑一次，確認 SSH 金鑰與主機指紋都沒問題）：
#   powershell -ExecutionPolicy Bypass -File C:\TecoScripts\04-windows-pull-backup.ps1 -VmHost emmt-teco-project@100.75.209.59
# 排程註冊方式見 docs/正式機首次部署手冊.md 附錄 D。

param(
    # VM 的 SSH 目標（使用者@Tailscale 位址）。用 Tailscale 位址，因為 Default Switch 的 IP 重開機會變。
    [Parameter(Mandatory = $true)][string]$VmHost,
    [string]$Destination = 'C:\TecoBackup',
    [string]$IdentityFile = "$env:USERPROFILE\.ssh\id_ed25519_teco_backup",
    [int]$KeepDays = 60
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$logFile = Join-Path $Destination 'pull-backup.log'

function Write-Log([string]$message) {
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $message" | Add-Content -Path $logFile -Encoding utf8
}

try {
    # BatchMode：排程執行時不能跳出任何互動提示（例如詢問密碼），失敗就直接失敗並寫 log。
    # -p 保留原始修改時間：沒有它每份檔案的時間都會變成「今天複製的時間」，下面的保留天數清理永遠刪不到。
    & scp -p -q -o BatchMode=yes -o ConnectTimeout=20 -i $IdentityFile "${VmHost}:~/backup/teco_hvac_*.sql.gz" $Destination
    if ($LASTEXITCODE -ne 0) { throw "scp 結束代碼 $LASTEXITCODE" }

    $files = Get-ChildItem -Path $Destination -Filter 'teco_hvac_*.sql.gz'
    $latest = $files | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    # 備份檔小於 1KB 幾乎可以確定是空的（mariadb-dump 失敗但 gzip 仍產生了檔案）。
    if ($null -eq $latest -or $latest.Length -lt 1KB) { throw "最新的備份檔不存在或小於 1KB，請檢查 VM 的 cron 備份" }

    $cutoff = (Get-Date).AddDays(-$KeepDays)
    $old = $files | Where-Object { $_.LastWriteTime -lt $cutoff }
    $old | Remove-Item -Force

    Write-Log "成功：共 $($files.Count) 份，最新 $($latest.Name)（$([math]::Round($latest.Length / 1KB)) KB），清除 $(@($old).Count) 份超過 $KeepDays 天的舊檔"
}
catch {
    Write-Log "失敗：$($_.Exception.Message)"
    exit 1
}

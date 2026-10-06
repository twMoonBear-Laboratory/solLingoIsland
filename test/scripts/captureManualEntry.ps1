#requires -Version 7
<#
  Issue #311（app 內「使用手冊」入口）之實機走查＋手冊證據擷取。

  驗的是**本件行為本身**，不是「腳本跑得動」：
    (1) 關於分頁有「使用手冊」鈕（ManualBtn），文字＝「使用手冊」、啟用。
    (2) 該鈕與「更新紀錄」鈕（ChangeLogBtn）同列（垂直中心差 ≤ 半個鈕高）且位於其右。
    (3) 以 InvokePattern 實按後，攔截檔恰新增一筆，內容＝{RepoUrl}/blob/v{VERSION}/README.md
        ——RepoUrl 自 sysLingoIsland/UpdateService.cs 原始碼讀值、VERSION 自根檔讀值，不硬編第二份。
    (3b) 成功路徑不跳任何提示框。
    (4) 按下後 App 仍存活、主視窗仍在（開啟路徑不致崩潰）。
    (5) 案二：測試縫指向既存資料夾（寫檔必擲例外＝以測試縫模擬開啟失敗，走與 Process.Start 擲例外同一條提示分支），實按後標題「LingoIsland 使用手冊」之提示框出現、
        文字含「無法開啟瀏覽器」與完整網址；按確定後 App 存活。

  不開瀏覽器：以測試縫環境變數 LINGOISLAND_MANUAL_LAUNCH_LOG 啟動受測 app，「使用手冊」改把網址附寫該檔（design ＜II.C.(A).4＞ 使用手冊入口契約）。
  系統匣「使用手冊」項不以 UIA 驅動（Shell 通知區非本 app 視窗、座標隨工作列設定而變＝盲點風險），其接線由
  單元層 UserManualTests 之結構斷言覆蓋；兩入口共用 UserManual.Open，本腳本實按關於頁鈕即走過共用路徑。
  離線提示與版號退路由單元層以注入替身覆蓋（端端須拔除網路介面／打包成品恆有版號）。
  桌面 UIA e2e 工法依 [modTechStackWinApp] ＜III＞；取窗／最大化／截圖沿用 uiaCommon.ps1，不重造。
  %APPDATA% 起手備份、finally 還原（啟動 app 會改寫 ui-state 等檔）。全程 0 次 OpenAI 呼叫。
#>

# 注意：本腳本起手會**關閉所有執行中之 LingoIsland**（含你正在用的那一個），%APPDATA%\LingoIsland 起手備份、結束還原。
# 手冊圖 docs/manual-assets/about-manual-button.png 只在帶 -UpdateManualAsset 時覆寫（平時只寫 OutDir）。
param(
  [string]$ExePath = "",
  [string]$OutDir  = "",
  [switch]$UpdateManualAsset
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Stop"

#region I.主旨目的 ================================
Write-Host "# I.主旨目的 ================================" -ForegroundColor Blue
Write-Host "* 驗證關於分頁「使用手冊」鈕（Issue #311）於實機成立：鈕在、與更新紀錄同列居右、實按開出之網址＝RepoUrl＋本機版號、App 不崩潰。"
#endregion

#region II.參考準備 ================================
Write-Host "# II.參考準備 ================================" -ForegroundColor Blue

  #region A.參數準備 --------------------------------
  Write-Host "## A.參數準備 --------------------------------" -ForegroundColor Cyan
  $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
  if (-not $ExePath) { $ExePath = Join-Path $repoRoot "sysLingoIsland\bin\Release\net9.0-windows10.0.19041.0\LingoIsland.exe" }
  if (-not $OutDir)  { $OutDir  = Join-Path $env:TEMP "LingoIsland-manualentry-evidence" }
  $appData   = Join-Path $env:APPDATA "LingoIsland"
  $backupDir = Join-Path $env:TEMP ("LingoIsland-backup-manualentry-" + (Get-Date -Format "yyyyMMddHHmmss"))
  $launchLog = Join-Path $env:TEMP ("LingoIsland-manual-launch-" + (Get-Date -Format "yyyyMMddHHmmss") + ".log")
  $manualPng = Join-Path $repoRoot "docs\manual-assets\about-manual-button.png"

  # 期望網址：自事實來源讀值（不硬編第二份）
  $updSrc = Get-Content (Join-Path $repoRoot "sysLingoIsland\UpdateService.cs") -Raw -Encoding UTF8
  $m = [regex]::Match($updSrc, 'const string RepoUrl\s*=\s*"([^"]+)"')
  if (-not $m.Success) { Write-Host "* [錯誤] UpdateService.cs 讀不到 RepoUrl 常數" -ForegroundColor Red; exit 1 }
  $repoUrl  = $m.Groups[1].Value
  $version  = (Get-Content (Join-Path $repoRoot "VERSION") -Raw).Trim()
  $expected = "$repoUrl/blob/v$version/README.md"
  Write-Host "* ExePath  = $ExePath"
  Write-Host "* OutDir   = $OutDir"
  Write-Host "* 期望網址 = $expected"
  if (-not (Test-Path $ExePath)) { Write-Host "* [錯誤] 找不到建置產物：$ExePath（請先 dotnet build -c Release）" -ForegroundColor Red; exit 1 }
  if (-not (Test-Path $OutDir))  { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
  # 本機離線時「使用手冊」會先跳離線提示（設計如此），案一之「成功不跳框」無從判定——據實中止
  if (-not [System.Net.NetworkInformation.NetworkInterface]::GetIsNetworkAvailable()) { Write-Host "* [錯誤] 本機判定離線——案一會先跳離線提示，本輪無法判定" -ForegroundColor Red; exit 1 }
  #endregion

  #region B.型別準備（共用定義） --------------------------------
  Write-Host "## B.型別準備（共用定義） --------------------------------" -ForegroundColor Cyan
  . "$PSScriptRoot\uiaCommon.ps1"
  Write-Host "* DPI 一致化（PER_MONITOR_AWARE_V2）＝$([Win32Ui]::MakeDpiAware())"
  $AE = [System.Windows.Automation.AutomationElement]

  function Switch-Tab {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$TabId, [IntPtr]$Hwnd)
    for ($i = 0; $i -lt 5; $i++) {
      Set-WindowForeground -Hwnd $Hwnd | Out-Null
      $tab = Find-ByAutomationId -Root $Root -Id $TabId
      if ($null -eq $tab) { Start-Sleep -Milliseconds 500; continue }
      $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
      Start-Sleep -Milliseconds 900
      if ($tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { return $true }
    }
    return $false
  }
  function Get-LogLines {
    if (-not (Test-Path $launchLog)) { return @() }
    return @(Get-Content $launchLog -Encoding UTF8 | Where-Object { $_.Trim() -ne "" })
  }
  #endregion
#endregion

#region III.內容程序 ================================
Write-Host "# III.內容程序 ================================" -ForegroundColor Blue

$fails = @()
$notes = @()
try {

  #region A.APPDATA 備份＋設測試縫 --------------------------------
  Write-Host "## A.APPDATA 備份＋設測試縫 --------------------------------" -ForegroundColor Cyan
  Get-Process -Name "LingoIsland" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
  Start-Sleep -Milliseconds 500
  if (Test-Path $appData) {
    Copy-Item -Path $appData -Destination $backupDir -Recurse -Force
    Write-Host "* 已備份 $appData → $backupDir"
  }
  if (Test-Path $launchLog) { Remove-Item $launchLog -Force }
  $env:LINGOISLAND_MANUAL_LAUNCH_LOG = $launchLog   # 子行程繼承；「使用手冊」改寫檔、不開瀏覽器
  Write-Host "* 測試縫 LINGOISLAND_MANUAL_LAUNCH_LOG＝$launchLog"
  #endregion

  #region B.啟動 App、切關於分頁 --------------------------------
  Write-Host "## B.啟動 App、切關於分頁 --------------------------------" -ForegroundColor Cyan
  $app  = Start-AppAndGetWindow -ExePath $ExePath -TimeoutSec 30
  $hwnd = $app.Hwnd
  Set-WindowMaximized -Hwnd $hwnd | Out-Null
  Set-WindowForeground -Hwnd $hwnd | Out-Null
  $root = $AE::FromHandle($hwnd)
  if (-not (Switch-Tab -Root $root -TabId "TabAbout" -Hwnd $hwnd)) { throw "切不到關於分頁（TabAbout）" }
  Start-Sleep -Milliseconds 900
  #endregion

  #region C.訴求1–2：鈕在、同列居右 --------------------------------
  Write-Host "## C.訴求1–2 鈕在、與更新紀錄同列居右 --------------------------------" -ForegroundColor Cyan
  $manualBtn = Find-ByAutomationId -Root $root -Id "ManualBtn"
  $logBtn    = Find-ByAutomationId -Root $root -Id "ChangeLogBtn"
  if ($null -eq $manualBtn) { throw "找不到「使用手冊」鈕（ManualBtn）——#311 未落地" }
  if ($null -eq $logBtn)    { throw "找不到「更新紀錄」鈕（ChangeLogBtn）——前置狀態不成立" }
  if ($manualBtn.Current.Name -ne "使用手冊") { $fails += "訴求1：鈕文字「$($manualBtn.Current.Name)」≠「使用手冊」" }
  if (-not $manualBtn.Current.IsEnabled)      { $fails += "訴求1：「使用手冊」鈕停用" }
  $mr = $manualBtn.Current.BoundingRectangle; $lr = $logBtn.Current.BoundingRectangle
  $dy = [Math]::Abs(($mr.Y + $mr.Height / 2) - ($lr.Y + $lr.Height / 2))
  if ($dy -gt ($lr.Height / 2)) { $fails += "訴求2：兩鈕不同列（垂直中心差 $dy px）" }
  if ($mr.X -le $lr.X)          { $fails += "訴求2：「使用手冊」未在「更新紀錄」右側（X $($mr.X) ≤ $($lr.X)）" }
  # 命中斷言：鈕中心點須屬受測行程（他窗覆蓋即本輪無法判定，據實中止、不假 PASS）
  $cx = [int]($mr.X + $mr.Width / 2); $cy = [int]($mr.Y + $mr.Height / 2)
  if ([Win32Ui]::PidAtPoint($cx, $cy) -ne [uint32]$app.ProcessId) { throw "「使用手冊」鈕座標被他窗覆蓋——本輪無法判定（請關閉遮擋視窗後重跑）" }
  if ($fails.Count -eq 0) { Write-Host "* [OK] 鈕在、文字正確、與更新紀錄同列居右" -ForegroundColor Green }
  Save-WindowShot -Hwnd $hwnd -Path (Join-Path $OutDir "01-about-manual-button.png")
  if ($UpdateManualAsset) {
    Copy-Item (Join-Path $OutDir "01-about-manual-button.png") $manualPng -Force
    Write-Host "* 手冊圖 → $manualPng"
  } else { Write-Host "* 手冊圖未覆寫（未帶 -UpdateManualAsset）" }
  #endregion

  #region D.訴求3–4：實按→攔截網址、App 存活 --------------------------------
  Write-Host "## D.訴求3–4 實按→攔截網址 --------------------------------" -ForegroundColor Cyan
  $before = (Get-LogLines).Count
  $manualBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  $deadline = (Get-Date).AddSeconds(10)
  while ((Get-Date) -lt $deadline -and (Get-LogLines).Count -le $before) { Start-Sleep -Milliseconds 300 }
  $lines = Get-LogLines
  $new = @($lines | Select-Object -Skip $before)
  if ($new.Count -ne 1) { $fails += "訴求3：實按後攔截檔新增 $($new.Count) 筆（應為 1）" }
  elseif ($new[0].Trim() -ne $expected) { $fails += "訴求3：開出網址「$($new[0].Trim())」≠ 期望「$expected」" }
  else { Write-Host "* [OK] 開出網址＝$expected" -ForegroundColor Green }
  # 成功路徑不得跳任何提示框（防退化成「成功也跳框」）
  $CT = [System.Windows.Automation.ControlType]; $TS = [System.Windows.Automation.TreeScope]
  $winCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Window)
  Start-Sleep -Milliseconds 1500
  $stray = @(@($root.FindAll($TS::Descendants, $winCond)) + @($AE::RootElement.FindAll($TS::Children, $winCond)) |
    Where-Object { $_.Current.Name -eq "LingoIsland 使用手冊" -and $_.Current.ProcessId -eq $app.ProcessId })
  if ($stray.Count -gt 0) { $fails += "訴求3：成功路徑卻跳出「LingoIsland 使用手冊」提示框" } else { Write-Host "* [OK] 成功路徑未跳提示框" -ForegroundColor Green }

  Start-Sleep -Milliseconds 800
  $alive = Get-Process -Id $app.ProcessId -ErrorAction SilentlyContinue
  if ($null -eq $alive -or $alive.HasExited) { $fails += "訴求4：按下後 App 行程已結束" }
  elseif ([Win32Ui]::PidOf($hwnd) -ne [uint32]$app.ProcessId) { $fails += "訴求4：按下後主視窗已不在" }
  else { Write-Host "* [OK] App 存活、主視窗仍在" -ForegroundColor Green }
  #endregion

  #region E.訴求5：開啟失敗→提示附網址、App 存活（案二） --------------------------------
  Write-Host "## E.訴求5 開啟失敗→提示附網址 --------------------------------" -ForegroundColor Cyan
  # 測試縫指向既存資料夾＝寫檔必擲例外＝走真實之「開啟失敗」分支（不必破壞本機瀏覽器關聯）
  Get-Process -Name "LingoIsland" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
  Start-Sleep -Milliseconds 800
  $env:LINGOISLAND_MANUAL_LAUNCH_LOG = $OutDir
  Write-Host "* 測試縫改指向資料夾（不可寫為檔）：$OutDir"
  $app  = Start-AppAndGetWindow -ExePath $ExePath -TimeoutSec 30
  $hwnd = $app.Hwnd
  Set-WindowMaximized -Hwnd $hwnd | Out-Null
  Set-WindowForeground -Hwnd $hwnd | Out-Null
  $root = $AE::FromHandle($hwnd)
  if (-not (Switch-Tab -Root $root -TabId "TabAbout" -Hwnd $hwnd)) { throw "案二：切不到關於分頁（TabAbout）" }
  Start-Sleep -Milliseconds 900
  $manualBtn = Find-ByAutomationId -Root $root -Id "ManualBtn"
  if ($null -eq $manualBtn) { throw "案二：找不到「使用手冊」鈕（ManualBtn）" }
  $manualBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

  # 模態提示：owned 視窗掛主視窗之下（Descendants），兩處都掃
  $dlg = $null; $deadline = (Get-Date).AddSeconds(10)
  while ($null -eq $dlg -and (Get-Date) -lt $deadline) {
    foreach ($w in @($root.FindAll($TS::Descendants, $winCond)) + @($AE::RootElement.FindAll($TS::Children, $winCond))) {
      if ($w.Current.Name -eq "LingoIsland 使用手冊" -and $w.Current.ProcessId -eq $app.ProcessId) { $dlg = $w; break }
    }
    if ($null -eq $dlg) { Start-Sleep -Milliseconds 300 }
  }
  if ($null -eq $dlg) { $fails += "訴求5：開啟失敗後未見標題「LingoIsland 使用手冊」之提示框" }
  else {
    # 自 Descendants 取得之節點子樹可能不完整：改以其原生 hwnd 重取根，並稍候對話框內容就緒
    Start-Sleep -Milliseconds 700
    $dlgHwnd = [IntPtr]$dlg.Current.NativeWindowHandle
    if ($dlgHwnd -ne [IntPtr]::Zero) { $dlg = $AE::FromHandle($dlgHwnd) }
    # 實撞：Win32 MessageBox（#32770）之靜態文字與按鈕於本 UIA 用戶端皆暴露為 Pane（非 Text／Button），故不以控制型別篩、改收全部後代之 Name
    $kids  = @($dlg.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
    $texts = @($kids | ForEach-Object { $_.Current.Name }) -join "`n"
    if ($texts -notlike "*無法開啟瀏覽器*") { $fails += "訴求5：提示文字未含「無法開啟瀏覽器」（實得「$texts」）" }
    if ($texts -notlike "*$expected*")    { $fails += "訴求5：提示文字未含完整網址 $expected" }
    if ($fails.Count -eq 0) { Write-Host "* [OK] 提示框出現、含「無法開啟瀏覽器」與完整網址" -ForegroundColor Green }
    Save-WindowShot -Hwnd ([IntPtr]$dlg.Current.NativeWindowHandle) -Path (Join-Path $OutDir "02-manual-open-failed.png")
    $okBtn = $kids | Where-Object { $_.Current.Name -in @("OK", "確定") } | Select-Object -First 1
    if ($null -eq $okBtn) { $fails += "訴求5：提示框找不到確定鈕（OK／確定）" }
    else {
      $inv = $null
      if ($okBtn.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$inv)) { $inv.Invoke() }
      else {
        [Win32Ui]::ForceForeground($dlgHwnd); Start-Sleep -Milliseconds 300
        if ([Win32Ui]::GetForegroundWindow() -ne $dlgHwnd) { throw "案二：提示框無法帶到前景、Enter 會落空——本輪無法判定" }
        [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
      }
    }
    # 實撞：Pane 型之 OK 雖回報支援 InvokePattern，Invoke 後對話框未必關閉——輪詢 3 秒，仍在即退回「前景＋Enter」（MessageBox 預設鈕＝確定）
    $dlgOpen = { $dlgHwnd -ne [IntPtr]::Zero -and [Win32Ui]::PidOf($dlgHwnd) -eq [uint32]$app.ProcessId }
    $deadline = (Get-Date).AddSeconds(3)
    while ((& $dlgOpen) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    if (& $dlgOpen) {
      [Win32Ui]::ForceForeground($dlgHwnd); Start-Sleep -Milliseconds 300
      if ([Win32Ui]::GetForegroundWindow() -ne $dlgHwnd) { throw "案二：提示框無法帶到前景、Enter 會落空——本輪無法判定" }
      [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
      $deadline = (Get-Date).AddSeconds(3)
      while ((& $dlgOpen) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    }
    if (& $dlgOpen) { $fails += "訴求5：按確定後提示框仍在" } else { Write-Host "* [OK] 按確定後提示框關閉" -ForegroundColor Green }
    Start-Sleep -Milliseconds 800
  }
  $alive = Get-Process -Id $app.ProcessId -ErrorAction SilentlyContinue
  if ($null -eq $alive -or $alive.HasExited) { $fails += "訴求5：開啟失敗後 App 行程已結束" }
  elseif ([Win32Ui]::PidOf($hwnd) -ne [uint32]$app.ProcessId) { $fails += "訴求5：開啟失敗後主視窗已不在" }
  else { Write-Host "* [OK] 開啟失敗後 App 存活、主視窗仍在" -ForegroundColor Green }
  #endregion

}
finally {
  #region E.收尾：關程式、清測試縫、還原 APPDATA --------------------------------
  Write-Host "## E.收尾 --------------------------------" -ForegroundColor Cyan
  Get-Process -Name "LingoIsland" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
  Start-Sleep -Milliseconds 500
  Remove-Item Env:\LINGOISLAND_MANUAL_LAUNCH_LOG -ErrorAction SilentlyContinue
  if (Test-Path $backupDir) {
    Remove-Item -Path $appData -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -Path $backupDir -Destination $appData -Recurse -Force
    Remove-Item -Path $backupDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "* 已還原 $appData"
  }
  if (Test-Path $launchLog) { Remove-Item $launchLog -Force -ErrorAction SilentlyContinue }
  #endregion
}
#endregion

#region IV.完成條件 ================================
Write-Host "# IV.完成條件 ================================" -ForegroundColor Blue
$notes | ForEach-Object { Write-Host "  [註] $_" -ForegroundColor Yellow }
if ($fails.Count -gt 0) {
  Write-Host "* 結果：FAIL（$($fails.Count) 項）" -ForegroundColor Red
  $fails | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
  exit 1
}
Write-Host "* 結果：PASS（訴求 1–5 全數成立；未開瀏覽器、0 次 OpenAI 呼叫）" -ForegroundColor Green
exit 0
#endregion

#requires -Version 7
<#
  Issue #309（筆記頁匯入英文清單，spec#14）之實機走查＋手冊證據擷取。

  驗的是**本件行為本身**，不是「腳本跑得動」：
    (1) 筆記分頁選取資料夾後「匯入清單」鈕（NotesImportBtn）為啟用。
    (2) 按鈕→系統檔案對話框→選入樣本 .txt（apple／banana／空行／cherry／Cherry）後，**確認頁（Title「匯入清單」）喚出**，
        列數＝4（NotesImportRow0–3；空行被忽略、檔內重複仍成列以供顯示）。
    (3) 預掃描狀態正確：apple／banana 已在探針夾→狀態含「已在筆記」且**未勾**；cherry→「新字」且**勾**；Cherry→「檔內重複」、未勾且停用。
    (4) 主鈕文案＝「查詢並加入 1 字」（N＝勾選數）；按 [全不選] 後主鈕**停用**且文案為 0 字。
    (5) 按 [取消] 關閉確認頁後：探針夾條目數不變、**AI 動作確認頁（Title 含「正在匯入」）全程未出現**（唯一會發查詢之路徑必經該頁，
        其未出現即 0 次 OpenAI 呼叫之機器證據）；ai-spend-ledger.json 位元組不變只作輔證。

  查詢並加入之成功／失敗路徑由 NotesImportRunnerTests 以 fake 委派覆蓋（零額度）；本腳本刻意只走到確認頁並取消，不花 OpenAI 額度（USR 常設裁定）。
  桌面 UIA e2e 工法依 [modTechStackWinApp] ＜III＞；取窗／最大化／截圖沿用 uiaCommon.ps1，不重造。
  %APPDATA% 起手備份、finally 還原（本腳本會植入探針筆記夾）。
#>

param(
  [string]$ExePath = "",
  [string]$OutDir  = ""
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Stop"

#region I.主旨目的 ================================
Write-Host "# I.主旨目的 ================================" -ForegroundColor Blue
Write-Host "* 驗證筆記頁匯入英文清單（Issue #309）於實機成立：選夾→匯入鈕→檔案對話框→確認頁列數與狀態→全不選停用→取消零呼叫。"
#endregion

#region II.參考準備 ================================
Write-Host "# II.參考準備 ================================" -ForegroundColor Blue

  #region A.參數準備 --------------------------------
  Write-Host "## A.參數準備 --------------------------------" -ForegroundColor Cyan
  $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
  if (-not $ExePath) { $ExePath = Join-Path $repoRoot "sysLingoIsland\bin\Release\net9.0-windows10.0.19041.0\LingoIsland.exe" }
  if (-not $OutDir)  { $OutDir  = Join-Path $env:TEMP "LingoIsland-notesimport-evidence" }
  $appData    = Join-Path $env:APPDATA "LingoIsland"
  $backupDir  = Join-Path $env:TEMP ("LingoIsland-backup-notesimport-" + (Get-Date -Format "yyyyMMddHHmmss"))
  $probeId    = "zzzz309309309309309309309309309f"
  $probeName  = "ZZ-IMPORT-PROBE"
  $samplePath = Join-Path $env:TEMP "zz-notes-import-sample.txt"
  $manualPng  = Join-Path $repoRoot "docs\manual-assets\notes-import-confirm.png"
  Write-Host "* ExePath = $ExePath"
  Write-Host "* OutDir  = $OutDir"
  Write-Host "* 探針夾＝$probeName（預植 apple／banana）／樣本＝$samplePath"
  if (-not (Test-Path $ExePath)) { Write-Host "* [錯誤] 找不到建置產物：$ExePath（請先 dotnet build -c Release）" -ForegroundColor Red; exit 1 }
  if (-not (Test-Path $OutDir))  { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
  #endregion

  #region B.型別準備（共用定義） --------------------------------
  Write-Host "## B.型別準備（共用定義） --------------------------------" -ForegroundColor Cyan
  . "$PSScriptRoot\uiaCommon.ps1"
  Write-Host "* DPI 一致化（PER_MONITOR_AWARE_V2）＝$([Win32Ui]::MakeDpiAware())"
  $AE = [System.Windows.Automation.AutomationElement]
  $TS = [System.Windows.Automation.TreeScope]
  $CT = [System.Windows.Automation.ControlType]

  function Click-Element {
    param([System.Windows.Automation.AutomationElement]$El)
    $r = $El.Current.BoundingRectangle
    [Win32Ui]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
    Start-Sleep -Milliseconds 120
    [Win32Ui]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [Win32Ui]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 400
  }

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

  # 本行程中、標題符合者之對話視窗；輪詢至出現或逾時。
  # 實撞：WPF 之 owned 視窗（OpenFileDialog／ShowDialog 子窗）在 UIA 樹掛於**主視窗之下**（Descendants），不在 RootElement 之 Children——
  # 只掃頂層必逾時；故先掃主視窗後代、再退掃頂層（兩處都看）。
  function Wait-WindowByTitle {
    param([System.Windows.Automation.AutomationElement]$MainRoot, [int]$ProcessId, [string]$TitleLike, [IntPtr]$ExcludeHwnd, [int]$TimeoutSec = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $winCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Window)
    $topCond = New-Object System.Windows.Automation.AndCondition(
      $winCond, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, [int]$ProcessId)))
    while ((Get-Date) -lt $deadline) {
      foreach ($w in $MainRoot.FindAll($TS::Descendants, $winCond)) {
        if ($w.Current.Name -like $TitleLike) { return $w }
      }
      foreach ($w in $AE::RootElement.FindAll($TS::Children, $topCond)) {
        if ([IntPtr]$w.Current.NativeWindowHandle -ne $ExcludeHwnd -and $w.Current.Name -like $TitleLike) { return $w }
      }
      Start-Sleep -Milliseconds 400
    }
    return $null
  }

  function Invoke-El { param($El) $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
  function Toggle-State { param($El) return $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState }
  function Get-ProbeEntryCount {
    $j = Get-Content (Join-Path $appData "notes.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    $f = @($j.Folders) | Where-Object { $_.Id -eq $probeId } | Select-Object -First 1
    if ($null -eq $f) { return -1 }
    return @($f.Entries).Count
  }
  function Get-LedgerBytes {
    $p = Join-Path $appData "ai-spend-ledger.json"
    if (Test-Path $p) { return [System.IO.File]::ReadAllBytes($p) } else { return [byte[]]@() }
  }
  #endregion
#endregion

#region III.內容程序 ================================
Write-Host "# III.內容程序 ================================" -ForegroundColor Blue

$fails = @()
$notes = @()
try {

  #region A.APPDATA 備份＋植入探針夾與樣本檔 --------------------------------
  Write-Host "## A.APPDATA 備份＋植入探針夾與樣本檔 --------------------------------" -ForegroundColor Cyan
  Get-Process -Name "LingoIsland" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) }
  Start-Sleep -Milliseconds 500
  Copy-Item -Path $appData -Destination $backupDir -Recurse -Force
  Write-Host "* 已備份 $appData → $backupDir"

  $notesJson = Join-Path $appData "notes.json"
  $data = if (Test-Path $notesJson) { Get-Content $notesJson -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{ Folders = @() } }
  $data.Folders = @(@($data.Folders) | Where-Object { $_.Id -ne $probeId })
  $mkEntry = { param($w) [pscustomobject]@{ Id = ([guid]::NewGuid().ToString("N")); AddedAt = "2026-10-06T00:00:00.0000000+08:00"; Original = $w; Phonetic = "[$w]"; Translation = "譯:$w"; Color = ""; PracticeScore = -1 } }
  $data.Folders = @($data.Folders) + ([pscustomobject]@{
    Id = $probeId; Name = $probeName; Folders = @(); Sort = $null
    Entries = @((& $mkEntry "apple"), (& $mkEntry "banana"))
  })
  ($data | ConvertTo-Json -Depth 10) | Set-Content -Path $notesJson -Encoding UTF8
  Write-Host "* 已植入探針夾「$probeName」（apple／banana）"

  [System.IO.File]::WriteAllText($samplePath, "apple`r`nbanana`r`n`r`n  cherry  `r`nCherry`r`n", (New-Object System.Text.UTF8Encoding($true)))
  Write-Host "* 已寫樣本 txt（含 BOM、空行、前後空白、大小寫重複）"
  $entriesBefore = Get-ProbeEntryCount
  $ledgerBefore  = Get-LedgerBytes
  Write-Host "* 起手：探針夾條目數＝$entriesBefore／AI 花費帳本 $($ledgerBefore.Length) bytes"
  #endregion

  #region B.啟動 App、切筆記頁、選探針夾 --------------------------------
  Write-Host "## B.啟動 App、切筆記頁、選探針夾 --------------------------------" -ForegroundColor Cyan
  $app  = Start-AppAndGetWindow -ExePath $ExePath -TimeoutSec 30
  $hwnd = $app.Hwnd
  Set-WindowMaximized -Hwnd $hwnd | Out-Null
  Set-WindowForeground -Hwnd $hwnd | Out-Null
  $root = $AE::FromHandle($hwnd)
  if (-not (Switch-Tab -Root $root -TabId "TabNotes" -Hwnd $hwnd)) { throw "切不到筆記分頁（TabNotes）" }
  Start-Sleep -Milliseconds 900

  $tree = Find-ByAutomationId -Root $root -Id "FolderTree"
  if ($null -eq $tree) { throw "找不到資料夾樹（FolderTree）" }
  $probeText = @($tree.FindAll($TS::Descendants,
      (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text))) |
    Where-Object { $_.Current.Name -eq $probeName }) | Select-Object -First 1
  if ($null -eq $probeText) { throw "資料夾樹找不到探針夾「$probeName」——前置狀態不成立" }
  $pr = $probeText.Current.BoundingRectangle
  if ([Win32Ui]::PidAtPoint([int]($pr.X + 4), [int]($pr.Y + $pr.Height / 2)) -ne [uint32]$app.ProcessId) { throw "探針夾座標被他窗覆蓋——本輪無法判定" }
  Click-Element -El $probeText
  Start-Sleep -Milliseconds 700
  Write-Host "* 已選取探針夾"

  # 訴求1：匯入鈕啟用
  $importBtn = Find-ByAutomationId -Root $root -Id "NotesImportBtn"
  if ($null -eq $importBtn) { throw "找不到「匯入清單」鈕（NotesImportBtn）——#309 未落地" }
  if (-not $importBtn.Current.IsEnabled) { $fails += "訴求1：已選取資料夾但「匯入清單」鈕仍停用" }
  else { Write-Host "* [OK] 匯入清單鈕啟用" -ForegroundColor Green }
  Save-WindowShot -Hwnd $hwnd -Path (Join-Path $OutDir "01-notes-page-import-btn.png")
  #endregion

  #region C.訴求2：按鈕→檔案對話框→確認頁喚出 --------------------------------
  Write-Host "## C.訴求2 按鈕→檔案對話框→確認頁 --------------------------------" -ForegroundColor Cyan
  Invoke-El $importBtn
  $fileDlg = Wait-WindowByTitle -MainRoot $root -ProcessId $app.ProcessId -TitleLike "選擇英文清單*" -ExcludeHwnd $hwnd -TimeoutSec 15
  if ($null -eq $fileDlg) { throw "按匯入鈕後未見檔案對話框（Title 選擇英文清單…）" }
  Write-Host "* 檔案對話框已開：「$($fileDlg.Current.Name)」"
  # 檔名欄：通用檔案對話框之「檔案名稱」欄開啟時即持鍵盤焦點。
  # 實撞：對 1148 之 ComboBox／Edit 以 ValuePattern.SetValue 回「Value is read-only」、SetFocus 回「cannot receive focus」——
  # 故改以鍵盤鍵入：先把對話框帶到前景，Ctrl+A 清預設檔名，鍵入完整路徑（不含 SendKeys 特殊字元），Enter＝「開啟」。
  $dlgHwnd = [IntPtr]$fileDlg.Current.NativeWindowHandle
  [Win32Ui]::ForceForeground($dlgHwnd); Start-Sleep -Milliseconds 500
  if ([Win32Ui]::GetForegroundWindow() -ne $dlgHwnd) { throw "檔案對話框未能帶到前景（前景＝「$([Win32Ui]::WindowTitle([Win32Ui]::GetForegroundWindow()))」），鍵入會落空" }
  [System.Windows.Forms.SendKeys]::SendWait("^a"); Start-Sleep -Milliseconds 120
  [System.Windows.Forms.SendKeys]::SendWait($samplePath); Start-Sleep -Milliseconds 400
  [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")   # ＝「開啟」
  Write-Host "* 已於檔案對話框鍵入樣本路徑並送出"

  $confirm = Wait-WindowByTitle -MainRoot $root -ProcessId $app.ProcessId -TitleLike "匯入清單" -ExcludeHwnd $hwnd -TimeoutSec 15
  if ($null -eq $confirm) { throw "選檔後未見確認頁（Title 匯入清單）" }
  $confirmHwnd = [IntPtr]$confirm.Current.NativeWindowHandle
  Write-Host "* 確認頁已開"
  $rows = @($confirm.FindAll($TS::Descendants,
      (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::CheckBox))) |
    Where-Object { $_.Current.AutomationId -like "NotesImportRow*" -and $_.Current.AutomationId -notlike "*Status" } |
    Sort-Object { [int]($_.Current.AutomationId -replace "\D", "") })
  Write-Host "* 確認頁列數＝$($rows.Count)（名稱：$(($rows | ForEach-Object { $_.Current.Name }) -join '｜')）"
  if ($rows.Count -ne 4) { $fails += "訴求2：確認頁列數＝$($rows.Count)，應為 4（apple／banana／cherry／Cherry；空行忽略）" }
  else { Write-Host "* [OK] 確認頁喚出且列數＝4" -ForegroundColor Green }
  #endregion

  #region D.訴求3＋4：狀態與預設勾選、主鈕文案 --------------------------------
  Write-Host "## D.訴求3＋4 狀態／勾選／主鈕 --------------------------------" -ForegroundColor Cyan
  function Status-Of([int]$i) {
    $s = Find-ByAutomationId -Root $confirm -Id ("NotesImportRow" + $i + "Status")
    if ($null -eq $s) { return "" } else { return $s.Current.Name }
  }
  $expect = @(
    @{ Word = "apple";  Status = "已在筆記"; Checked = $false; Enabled = $true  },
    @{ Word = "banana"; Status = "已在筆記"; Checked = $false; Enabled = $true  },
    @{ Word = "cherry"; Status = "新字";     Checked = $true;  Enabled = $true  },
    @{ Word = "Cherry"; Status = "檔內重複"; Checked = $false; Enabled = $false }
  )
  for ($i = 0; $i -lt [Math]::Min(4, $rows.Count); $i++) {
    $r = $rows[$i]; $e = $expect[$i]
    $st = Status-Of $i
    $isChecked = (Toggle-State $r) -eq [System.Windows.Automation.ToggleState]::On
    Write-Host "* 列$i：名稱「$($r.Current.Name)」狀態「$st」勾＝$isChecked 啟用＝$($r.Current.IsEnabled)"
    if ($r.Current.Name -ne $e.Word)        { $fails += "訴求3：列$i 名稱「$($r.Current.Name)」≠「$($e.Word)」" }
    if ($st -notlike "*$($e.Status)*")      { $fails += "訴求3：列$i（$($e.Word)）狀態「$st」未含「$($e.Status)」" }
    if ($isChecked -ne $e.Checked)          { $fails += "訴求3：列$i（$($e.Word)）預設勾選＝$isChecked，應為 $($e.Checked)" }
    if ($r.Current.IsEnabled -ne $e.Enabled){ $fails += "訴求3：列$i（$($e.Word)）可改選＝$($r.Current.IsEnabled)，應為 $($e.Enabled)" }
  }
  $confirmBtn = Find-ByAutomationId -Root $confirm -Id "NotesImportConfirm"
  if ($null -eq $confirmBtn) { $fails += "訴求4：找不到主鈕（NotesImportConfirm）" }
  else {
    Write-Host "* 主鈕文案＝「$($confirmBtn.Current.Name)」啟用＝$($confirmBtn.Current.IsEnabled)"
    if ($confirmBtn.Current.Name -ne "查詢並加入 1 字") { $fails += "訴求4：主鈕文案「$($confirmBtn.Current.Name)」≠「查詢並加入 1 字」" }
    if (-not $confirmBtn.Current.IsEnabled) { $fails += "訴求4：有 1 字勾選但主鈕停用" }
  }
  if ($fails.Count -eq 0) { Write-Host "* [OK] 四列狀態／預設勾選／主鈕文案皆符" -ForegroundColor Green }

  # 手冊證據圖＝確認頁最終態（含主視窗背景）
  Set-WindowForeground -Hwnd $confirmHwnd | Out-Null
  Start-Sleep -Milliseconds 300
  Save-WindowShot -Hwnd $confirmHwnd -Path (Join-Path $OutDir "02-import-confirm.png")
  Copy-Item (Join-Path $OutDir "02-import-confirm.png") $manualPng -Force
  Write-Host "* 手冊圖已產出：$manualPng"

  # 訴求4 後半：全不選→主鈕停用、文案 0 字
  $selNone = Find-ByAutomationId -Root $confirm -Id "NotesImportSelectNone"
  if ($null -eq $selNone) { $fails += "訴求4：找不到 [全不選]（NotesImportSelectNone）" }
  else {
    Invoke-El $selNone
    Start-Sleep -Milliseconds 500
    $confirmBtn = Find-ByAutomationId -Root $confirm -Id "NotesImportConfirm"
    Write-Host "* 全不選後：主鈕文案＝「$($confirmBtn.Current.Name)」啟用＝$($confirmBtn.Current.IsEnabled)"
    if ($confirmBtn.Current.IsEnabled) { $fails += "訴求4：全不選後主鈕仍啟用" }
    if ($confirmBtn.Current.Name -ne "查詢並加入 0 字") { $fails += "訴求4：全不選後主鈕文案「$($confirmBtn.Current.Name)」≠「查詢並加入 0 字」" }
    if (-not $confirmBtn.Current.IsEnabled -and $confirmBtn.Current.Name -eq "查詢並加入 0 字") { Write-Host "* [OK] 全不選→主鈕停用" -ForegroundColor Green }
    Save-WindowShot -Hwnd $confirmHwnd -Path (Join-Path $OutDir "03-select-none.png")
  }
  #endregion

  #region E.訴求5：取消→資料不變、零 AI 呼叫 --------------------------------
  Write-Host "## E.訴求5 取消→資料不變、零呼叫 --------------------------------" -ForegroundColor Cyan
  $cancelBtn = Find-ByAutomationId -Root $confirm -Id "NotesImportCancel"
  if ($null -eq $cancelBtn) { throw "找不到 [取消]（NotesImportCancel）" }
  Invoke-El $cancelBtn
  Start-Sleep -Milliseconds 800
  $still = Wait-WindowByTitle -MainRoot $root -ProcessId $app.ProcessId -TitleLike "匯入清單" -ExcludeHwnd $hwnd -TimeoutSec 1
  if ($null -ne $still) { $fails += "訴求5：按取消後確認頁仍開著" } else { Write-Host "* [OK] 確認頁已關閉" -ForegroundColor Green }
  $aiWin = Wait-WindowByTitle -MainRoot $root -ProcessId $app.ProcessId -TitleLike "*正在匯入*" -ExcludeHwnd $hwnd -TimeoutSec 1
  if ($null -ne $aiWin) { $fails += "訴求5：AI 動作確認頁（正在匯入…）出現了——取消路徑不該發出任何查詢" }
  else { Write-Host "* [OK] AI 動作確認頁全程未出現（＝0 次 AI 呼叫）" -ForegroundColor Green }
  $entriesAfter = Get-ProbeEntryCount
  $ledgerAfter  = Get-LedgerBytes
  Write-Host "* 收尾：探針夾條目數＝$entriesAfter（起手 $entriesBefore）／AI 花費帳本 $($ledgerAfter.Length) bytes（起手 $($ledgerBefore.Length)）"
  if ($entriesAfter -ne $entriesBefore) { $fails += "訴求5：取消後探針夾條目數由 $entriesBefore 變 $entriesAfter——取消不該寫入" }
  $sameLedger = ($ledgerBefore.Length -eq $ledgerAfter.Length) -and ([System.Linq.Enumerable]::SequenceEqual([byte[]]$ledgerBefore, [byte[]]$ledgerAfter))
  if (-not $sameLedger) { $fails += "訴求5：AI 花費帳本有變動（輔證）" }
  if ($entriesAfter -eq $entriesBefore -and $sameLedger) { Write-Host "* [OK] 取消後資料不變、AI 花費帳本未動（輔證）" -ForegroundColor Green }
  Save-WindowShot -Hwnd $hwnd -Path (Join-Path $OutDir "04-after-cancel.png")
  #endregion

}
finally {
  #region F.收尾：關程式並還原 APPDATA --------------------------------
  Write-Host "## F.收尾 --------------------------------" -ForegroundColor Cyan
  Get-Process -Name "LingoIsland" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) }
  Start-Sleep -Milliseconds 500
  if (Test-Path $backupDir) {
    Remove-Item -Path $appData -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -Path $backupDir -Destination $appData -Recurse -Force
    Remove-Item -Path $backupDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "* 已還原 $appData"
  }
  if (Test-Path $samplePath) { Remove-Item $samplePath -Force -ErrorAction SilentlyContinue }
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
Write-Host "* 結果：PASS（訴求 1–5 全數成立；全程 0 次 OpenAI 呼叫）" -ForegroundColor Green
exit 0
#endregion

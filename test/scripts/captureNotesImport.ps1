#requires -Version 7
<#
  Issue #309（筆記頁匯入英文清單，spec#14）之實機走查＋手冊證據擷取。

  驗的是**本件行為本身**，不是「腳本跑得動」：
    (1) 筆記分頁選取資料夾後「匯入清單」鈕（NotesImportBtn）為啟用。
    (2) 按鈕→系統檔案對話框→選入樣本 .txt（apple／banana／空行／cherry／Cherry）後，**確認頁（Title「匯入清單」）喚出**，
        列數＝4（NotesImportRow0–3；空行被忽略、檔內重複仍成列以供顯示）。
    (3) 預掃描狀態正確：apple／banana 已在探針夾→狀態含「已在筆記」且**未勾**；cherry→「新字」且**勾**；Cherry→「檔內重複」、未勾且停用。
    (4) 主鈕文案＝「查詢並加入 1 字」（N＝勾選數）；按 [全不選] 後主鈕**停用**且文案為 0 字。
    (3b) 已在筆記列之狀態含所在夾名「Unit 3 生詞」。
    (5) 按 [取消] 關閉確認頁後：探針夾條目數不變、**AI 動作確認頁（Title 含「正在匯入」）全程未出現**（唯一會發查詢之路徑必經該頁，
        其未出現即 0 次 OpenAI 呼叫之機器證據）；ai-spend-ledger.json 位元組不變只作輔證。

  #320（多檔與拖放，spec#14 擴充）增走：
    ⑥ 對話框多選兩檔（unit3-words.txt＋unit4-words.csv，跨檔重複一字）→合併列數、跨檔重複標首見檔名且停用、各列來源欄、首行「2 個檔」→手冊圖改於此擷取→取消。
    ⑦ 真實 OLE 檔案拖放同兩檔到條目區空白處（拖曳來源＝本腳本以 Add-Type C# 於專屬 STA 執行緒開的小窗）→拖曳中見目標夾橫幅、
       DoDragDrop 於 10 秒內返回（放下回呼不阻塞）、確認頁可見於最上層、與 ⑥ 逐列相同→取消；橫幅不殘留。
    ⑧ 拖入 notes.docx→見「只能拖入 .txt／.csv」toast、放下效果 None、不開確認頁。
    ⑨ 緊接 ⑧ 拖入 txt＋docx＋Big5 txt→照常開出、首行單檔格式、「未納入」列兩檔→取消。
    ⑪ 既有拖曳不受影響：按住 apple 卡片握把拖到第二探針夾，notes.json 歸屬改變。
    ⑩ 全程 AI 動作確認頁未出現、兩探針夾條目合計不變。

  #321（CSV 第二欄自備中譯，spec#14 擴充）增走（接在 ⑩ 之後；受測 app 以**無效假金鑰**啟動——萬一誤觸查詢只得 401、不計費）：
    ⑫ 對話框選 unit5-bilingual.csv（表頭＋grape,葡萄／kiwi,（空白）／"mango","芒果, 熱帶水果"／apple,蘋果）→中譯來源欄、apple 狀態「勾選＝以自備中譯更新」、
       整批切換存在且未勾、主鈕「加入 3 字（查詢 1 字）」、費用「共 1 次 AI 查詢」「另 2 字採用自備中譯」→手冊圖 notes-import-own-translation.png。
    ⑬ 勾整批切換→全部「線上查詢」、主鈕「查詢並加入 3 字」→取消勾→還原。
    ⑭ 先斷言 kiwi 已勾再取消→主鈕「加入 2 字（不查詢）」→實按→結果訊息框「已加入 2 字」「採用自備中譯」→AI 動作頁未出現、帳本不變、
       notes.json 之 grape／mango 中譯＝自備、音標空、kiwi 未寫入。
  落點被他窗覆蓋即據實中止（不判 PASS）。

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
Write-Host "* 並驗多檔與拖放（Issue #320）：多選兩檔合併去重與來源欄、真實 OLE 拖放同一張表、非清單檔拒收提示、混雜檔未納入、既有卡片拖曳不受影響。"
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
  $probeName  = "Unit 3 生詞"            # 手冊圖會入正式 README，探針名須擬真、不用內部代號（S3P B-14）
  $sampleDir  = Join-Path $env:TEMP "LingoIsland-e2e-320"
  $samplePath = Join-Path $sampleDir "unit3-words.txt"
  $unit3Path  = $samplePath
  $unit4Path  = Join-Path $sampleDir "unit4-words.csv"
  $docxPath   = Join-Path $sampleDir "notes.docx"
  $big5Path   = Join-Path $sampleDir "old-big5.txt"
  $probe2Id   = "zzzz320320320320320320320320320f"
  $probe2Name = "Unit 4 生詞"
  $manualPng  = Join-Path $repoRoot "docs\manual-assets\notes-import-confirm.png"
  $unit5Path  = Join-Path $sampleDir "unit5-bilingual.csv"
  $ownPng     = Join-Path $repoRoot "docs\manual-assets\notes-import-own-translation.png"
  $sampleOwnWords = @("grape", "kiwi", "mango")   # #321 樣本字：植探針前自全樹移除，免受測者真筆記已有而誤判
  $origApiKey = $env:OPENAI_API_KEY
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

  # ---- #320 輔助：真實 OLE 檔案拖曳來源（C#：專屬 STA 執行緒跑 WinForms 小窗，於其 MouseDown 內 DoDragDrop；
  #      不以 PowerShell scriptblock 當執行緒委派——新執行緒無 Runspace 會失敗） ----
  if (-not ("FileDragSource" -as [type])) {
    Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Drawing.Primitives, System.Collections.Specialized, System.Threading, System.Threading.Thread, System.ComponentModel.Primitives, System.Runtime.InteropServices -TypeDefinition @"
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

public static class FileDragSource
{
    private static ManualResetEvent _ready, _done;
    public static string Effect = "";
    public static long ElapsedMs;
    public static bool MouseDownFired;
    public static IntPtr Handle;

    public static bool Start(string[] files, int x, int y)
    {
        _ready = new ManualResetEvent(false); _done = new ManualResetEvent(false); Effect = ""; ElapsedMs = 0; MouseDownFired = false;
        var t = new Thread(() =>
        {
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(x, y), Size = new System.Drawing.Size(90, 60),
                TopMost = true, ShowInTaskbar = false, BackColor = System.Drawing.Color.LightPink, Text = "e2e-drag-source",
            };
            form.MouseDown += (s, e) =>
            {
                MouseDownFired = true;
                var data = new DataObject();
                var sc = new StringCollection(); sc.AddRange(files);
                data.SetFileDropList(sc);
                var sw = Stopwatch.StartNew();
                var r = form.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
                ElapsedMs = sw.ElapsedMilliseconds; Effect = r.ToString();
                _done.Set();
                form.BeginInvoke(new Action(form.Close));
            };
            form.Shown += (s, e) => { Handle = form.Handle; _ready.Set(); };
            Application.Run(form);
        });
        t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
        return _ready.WaitOne(5000);
    }

    public static bool WaitDone(int ms) { return _done.WaitOne(ms); }
}

public static class MouseDrag
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    public static void Down(int x, int y) { SetCursorPos(x, y); Thread.Sleep(150); mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(200); }
    public static void Up() { mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); }
    public static void MoveTo(int x0, int y0, int x1, int y1, int steps)
    {
        for (int i = 1; i <= steps; i++)
        {
            SetCursorPos(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps);
            mouse_event(0x0001, 0, 0, 0, IntPtr.Zero); // 相對 0 位移＝補一個移動事件，使拖放迴圈收到 DragOver
            Thread.Sleep(25);
        }
    }
    public static void Wiggle(int x, int y) { SetCursorPos(x + 2, y); mouse_event(0x0001, 0, 0, 0, IntPtr.Zero); Thread.Sleep(40); SetCursorPos(x, y); mouse_event(0x0001, 0, 0, 0, IntPtr.Zero); Thread.Sleep(40); }
    public static void Drag(int x0, int y0, int x1, int y1) { Down(x0, y0); MoveTo(x0, y0, x0 + 12, y0 + 12, 4); MoveTo(x0 + 12, y0 + 12, x1, y1, 30); Wiggle(x1, y1); Wiggle(x1, y1); Thread.Sleep(250); Up(); Thread.Sleep(300); }
}
"@
  }

  # 確認頁（Title「匯入清單」且含主鈕 NotesImportConfirm——同名 MessageBox 不算）
  function Wait-Confirm {
    param([int]$TimeoutSec = 15)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
      $w = Wait-WindowByTitle -MainRoot $root -ProcessId $app.ProcessId -TitleLike "匯入清單" -ExcludeHwnd $hwnd -TimeoutSec 1
      if ($null -ne $w -and $null -ne (Find-ByAutomationId -Root $w -Id "NotesImportConfirm")) { return $w }
    } while ((Get-Date) -lt $deadline)
    return $null
  }
  function Close-Confirm {
    param($Confirm)
    $c = Find-ByAutomationId -Root $Confirm -Id "NotesImportCancel"
    if ($null -ne $c) { Invoke-El $c }
    Start-Sleep -Milliseconds 800
  }
  function Get-RowSnapshot {
    param($Confirm)
    $rows = @($Confirm.FindAll($TS::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::CheckBox))) |
      Where-Object { $_.Current.AutomationId -match '^NotesImportRow\d+$' } |
      Sort-Object { [int]($_.Current.AutomationId -replace "\D", "") })
    $i = 0
    foreach ($r in $rows) {
      $st = Find-ByAutomationId -Root $Confirm -Id ("NotesImportRow" + $i + "Status")
      $sc = Find-ByAutomationId -Root $Confirm -Id ("NotesImportRow" + $i + "Source")
      $tr = Find-ByAutomationId -Root $Confirm -Id ("NotesImportRow" + $i + "Translation")
      [pscustomobject]@{
        Translation = if ($null -eq $tr) { "" } else { $tr.Current.Name }
        Name    = $r.Current.Name
        Checked = ((Toggle-State $r) -eq [System.Windows.Automation.ToggleState]::On)
        Enabled = $r.Current.IsEnabled
        Status  = if ($null -eq $st) { "" } else { $st.Current.Name }
        Source  = if ($null -eq $sc) { "" } else { $sc.Current.Name }
      }
      $i++
    }
  }
  function Get-CostText {
    param($Confirm)
    $t = @($Confirm.FindAll($TS::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text))) |
      Where-Object { $_.Current.Name -like "*已在筆記之字不會重複建立*" }) | Select-Object -First 1
    if ($null -eq $t) { return "" } else { return $t.Current.Name }
  }
  function Remove-WordsEverywhere {
    param($Folders, [string[]]$Words)
    foreach ($f in @($Folders)) {
      if ($null -eq $f) { continue }
      $f.Entries = @(@($f.Entries) | Where-Object { $Words -notcontains $_.Original })
      Remove-WordsEverywhere $f.Folders $Words
    }
  }
  function Get-HeaderText {
    param($Confirm)
    $t = @($Confirm.FindAll($TS::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text))) |
      Where-Object { $_.Current.Name -like "來源：*" }) | Select-Object -First 1
    if ($null -eq $t) { return "" } else { return $t.Current.Name }
  }
  # 條目區（EntryScroll）卡片下方之空白處——驗透明背景可命中
  function Get-EntryBlankPoint {
    $sv = Find-ByAutomationId -Root $root -Id "EntryScroll"
    if ($null -eq $sv) { throw "找不到條目區（EntryScroll）" }
    $r = $sv.Current.BoundingRectangle
    return [pscustomobject]@{ X = [int]($r.X + $r.Width / 2); Y = [int]($r.Y + $r.Height - 40) }
  }
  function Find-ToastText {
    param([string]$Like)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, [int]$app.ProcessId)
    foreach ($w in $AE::RootElement.FindAll($TS::Children, $cond)) {
      foreach ($t in $w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)))) {
        if ($t.Current.Name -like "*$Like*") { return $t.Current.Name }
      }
    }
    return ""
  }
  # 真實 OLE 拖放：來源窗按下→分步移至落點→（看橫幅／toast）→命中斷言→放開→等 DoDragDrop 返回
  function Invoke-FileDrag {
    param([string[]]$Files, [int]$TargetX, [int]$TargetY, [switch]$WatchBanner, [string]$WatchToast = "")
    $fx = [Math]::Max(10, $TargetX - 320); $fy = [Math]::Max(10, $TargetY - 30)
    if (-not [FileDragSource]::Start($Files, $fx, $fy)) { throw "拖曳來源視窗未能啟動" }
    Start-Sleep -Milliseconds 300
    [Win32Ui]::ForceForeground([FileDragSource]::Handle); Start-Sleep -Milliseconds 400   # 受測 app 最大化蓋滿螢幕：把來源窗帶到最上層
    $srcPid = [Win32Ui]::PidAtPoint($fx + 45, $fy + 30)
    Write-Host "  · 拖曳來源窗於 ($fx,$fy)；其中心之 pid＝$srcPid（本腳本 $PID）"
    if ($srcPid -ne [uint32]$PID) { throw "拖曳來源窗被他窗覆蓋（pid=$srcPid）——本輪無法判定，據實中止" }
    [MouseDrag]::Down($fx + 45, $fy + 30)
    Write-Host "  · 來源窗 MouseDown＝$([FileDragSource]::MouseDownFired)"
    [MouseDrag]::MoveTo($fx + 45, $fy + 30, $fx + 60, $fy + 45, 4)
    [MouseDrag]::MoveTo($fx + 60, $fy + 45, $TargetX, $TargetY, 30)
    $banner = ""; $toast = ""
    $deadline = (Get-Date).AddMilliseconds(1500)
    while ((Get-Date) -lt $deadline) {
      [MouseDrag]::Wiggle($TargetX, $TargetY)
      if ($WatchBanner -and $banner -eq "") { $b = Find-ByAutomationId -Root $root -Id "NotesDropBanner"; if ($null -ne $b) { $banner = $b.Current.Name } }
      if ($WatchToast -ne "" -and $toast -eq "") { $toast = Find-ToastText $WatchToast }
      if ((-not $WatchBanner -or $banner -ne "") -and ($WatchToast -eq "" -or $toast -ne "")) { break }
    }
    $hit = [Win32Ui]::PidAtPoint($TargetX, $TargetY) -eq [uint32]$app.ProcessId
    if (-not $hit) {
      # 落點被覆蓋：移回來源窗放開（取消拖曳），據實回報
      [MouseDrag]::MoveTo($TargetX, $TargetY, $fx + 45, $fy + 30, 10); [MouseDrag]::Up(); [FileDragSource]::WaitDone(5000) | Out-Null
      return [pscustomobject]@{ HitOk = $false; Banner = $banner; Toast = $toast; Returned = $false; ElapsedMs = 0; Effect = "" }
    }
    [MouseDrag]::Up()
    $returned = [FileDragSource]::WaitDone(10000)
    return [pscustomobject]@{ HitOk = $true; Banner = $banner; Toast = $toast; Returned = $returned; ElapsedMs = [FileDragSource]::ElapsedMs; Effect = [FileDragSource]::Effect }
  }
  function Get-AllFolders {
    param($Folders)
    foreach ($f in @($Folders)) { if ($null -ne $f) { $f; Get-AllFolders $f.Folders } }
  }
  function Get-FolderIdOf {
    param([string]$Word)
    $j = Get-Content (Join-Path $appData "notes.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($f in (Get-AllFolders $j.Folders)) { if (@($f.Entries) | Where-Object { $_.Original -eq $Word }) { return $f.Id } }
    return ""
  }
  function Get-FolderEntryCount {
    param([string]$Id)
    $j = Get-Content (Join-Path $appData "notes.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    $f = (Get-AllFolders $j.Folders) | Where-Object { $_.Id -eq $Id } | Select-Object -First 1
    if ($null -eq $f) { return -1 } else { return @($f.Entries).Count }
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
  Remove-WordsEverywhere $data.Folders $sampleOwnWords   # #321：樣本字不得預先存在（APPDATA 已備份、finally 還原）
  $mkEntry = { param($w) [pscustomobject]@{ Id = ([guid]::NewGuid().ToString("N")); AddedAt = "2026-10-06T00:00:00.0000000+08:00"; Original = $w; Phonetic = "[$w]"; Translation = "譯:$w"; Color = ""; PracticeScore = -1 } }
  $data.Folders = @($data.Folders) + ([pscustomobject]@{
    Id = $probeId; Name = $probeName; Folders = @(); Sort = $null
    Entries = @((& $mkEntry "apple"), (& $mkEntry "banana"))
  })
  $data.Folders = @(@($data.Folders) | Where-Object { $_.Id -ne $probe2Id }) + ([pscustomobject]@{
    Id = $probe2Id; Name = $probe2Name; Folders = @(); Sort = $null; Entries = @()
  })
  ($data | ConvertTo-Json -Depth 10) | Set-Content -Path $notesJson -Encoding UTF8
  Write-Host "* 已植入探針夾「$probeName」（apple／banana）與空的第二探針夾「$probe2Name」"
  if (Test-Path $sampleDir) { Remove-Item $sampleDir -Recurse -Force }
  New-Item -ItemType Directory -Path $sampleDir -Force | Out-Null
  [System.IO.File]::WriteAllText($unit4Path, "word`r`ngrape`r`nCHERRY`r`n", (New-Object System.Text.UTF8Encoding($false)))
  [System.IO.File]::WriteAllText($docxPath, "not a word list", (New-Object System.Text.UTF8Encoding($false)))
  [System.IO.File]::WriteAllText($unit5Path, "word,中文`r`ngrape,葡萄`r`nkiwi,`r`n`"mango`",`"芒果, 熱帶水果`"`r`napple,蘋果`r`n", (New-Object System.Text.UTF8Encoding($true)))
  [System.Text.Encoding]::RegisterProvider([System.Text.CodePagesEncodingProvider]::Instance)
  [System.IO.File]::WriteAllText($big5Path, "apple 蘋果`r`nbanana 香蕉`r`n", [System.Text.Encoding]::GetEncoding(950)) # 含中文才會解出 U+FFFD
  Write-Host "* 已寫 #320 樣本：unit4-words.csv（表頭＋grape＋CHERRY）、notes.docx、old-big5.txt（Big5 含中文）"

  [System.IO.File]::WriteAllText($samplePath, "apple`r`nbanana`r`n`r`n  cherry  `r`nCherry`r`n", (New-Object System.Text.UTF8Encoding($true)))
  Write-Host "* 已寫樣本 txt（含 BOM、空行、前後空白、大小寫重複）"
  $entriesBefore = Get-ProbeEntryCount
  $ledgerBefore  = Get-LedgerBytes
  Write-Host "* 起手：探針夾條目數＝$entriesBefore／AI 花費帳本 $($ledgerBefore.Length) bytes"
  #endregion

  #region B.啟動 App、切筆記頁、選探針夾 --------------------------------
  Write-Host "## B.啟動 App、切筆記頁、選探針夾 --------------------------------" -ForegroundColor Cyan
  # #321：受測 app 以明顯無效之假金鑰啟動（子行程繼承本行程環境變數）——金鑰預檢照常通過，萬一誤觸查詢也只得 401、不計費；finally 還原
  $env:OPENAI_API_KEY = "sk-e2e-invalid-placeholder-not-a-real-key-321"
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
  $coverPid = [Win32Ui]::PidAtPoint([int]($pr.X + 4), [int]($pr.Y + $pr.Height / 2))
  if ($coverPid -ne [uint32]$app.ProcessId) { throw "探針夾座標被他窗覆蓋（覆蓋者 pid=$coverPid／$((Get-Process -Id $coverPid -ErrorAction SilentlyContinue).ProcessName)）——本輪無法判定" }
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
    if ($e.Status -eq "已在筆記" -and $st -notlike "*$probeName*") { $fails += "訴求3b：列$i（$($e.Word)）已在筆記列未標所在夾「$probeName」（實得「$st」）" }
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
  # #321 invariant：無自備中譯（txt）時不出現中譯來源欄與整批切換、版面同 v4.18.0
  if ($null -ne (Find-ByAutomationId -Root $confirm -Id "NotesImportForceOnline")) { $fails += "#321：txt 匯入卻出現整批切換（NotesImportForceOnline）" }
  if ($null -ne (Find-ByAutomationId -Root $confirm -Id "NotesImportRow0Translation")) { $fails += "#321：txt 匯入卻出現中譯來源欄" }
  $cr0 = $confirm.Current.BoundingRectangle
  Write-Host "* #321：txt 確認頁無中譯來源欄／整批切換；視窗寬 $([int]$cr0.Width)px（DPI 縮放後，記錄不判）"

  # 手冊證據圖＝確認頁最終態（含主視窗背景）
  Set-WindowForeground -Hwnd $confirmHwnd | Out-Null
  Start-Sleep -Milliseconds 300
  Save-WindowShot -Hwnd $confirmHwnd -Path (Join-Path $OutDir "02-import-confirm.png")   # 手冊圖自 #320 起改由 ⑥（多檔狀態）擷取

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

  #region F.#320 ⑥ 對話框多選兩檔 --------------------------------
  Write-Host "## F.#320 ⑥ 對話框多選兩檔 --------------------------------" -ForegroundColor Cyan
  Set-WindowForeground -Hwnd $hwnd | Out-Null
  $importBtn = Find-ByAutomationId -Root $root -Id "NotesImportBtn"
  Invoke-El $importBtn
  $fileDlg = Wait-WindowByTitle -MainRoot $root -ProcessId $app.ProcessId -TitleLike "選擇英文清單*" -ExcludeHwnd $hwnd -TimeoutSec 15
  if ($null -eq $fileDlg) { throw "⑥：按匯入鈕後未見檔案對話框" }
  if ($fileDlg.Current.Name -notlike "*可多選*") { $fails += "⑥：對話框標題「$($fileDlg.Current.Name)」未註明可多選" }
  $dlgHwnd = [IntPtr]$fileDlg.Current.NativeWindowHandle
  [Win32Ui]::ForceForeground($dlgHwnd); Start-Sleep -Milliseconds 500
  if ([Win32Ui]::GetForegroundWindow() -ne $dlgHwnd) { throw "⑥：檔案對話框未能帶到前景，鍵入會落空" }
  # 兩段鍵入：先導航到樣本目錄，再以雙引號分隔多個檔名（共用對話框多選之標準鍵入法）
  [System.Windows.Forms.SendKeys]::SendWait("^a"); Start-Sleep -Milliseconds 120
  [System.Windows.Forms.SendKeys]::SendWait($sampleDir); Start-Sleep -Milliseconds 300
  [System.Windows.Forms.SendKeys]::SendWait("{ENTER}"); Start-Sleep -Milliseconds 900
  [System.Windows.Forms.SendKeys]::SendWait("^a"); Start-Sleep -Milliseconds 120
  [System.Windows.Forms.SendKeys]::SendWait('"unit3-words.txt" "unit4-words.csv"'); Start-Sleep -Milliseconds 400
  [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
  $confirm = Wait-Confirm -TimeoutSec 15
  if ($null -eq $confirm) { throw "⑥：多選兩檔後未見確認頁" }
  $multiRows = Get-RowSnapshot $confirm
  Write-Host "* ⑥ 確認頁列：$(($multiRows | ForEach-Object { "$($_.Name)/$($_.Source)/$($_.Status)" }) -join '｜')"
  $expectMulti = @(
    @{ Name = "apple";  Source = "unit3-words.txt"; Status = "已在筆記" },
    @{ Name = "banana"; Source = "unit3-words.txt"; Status = "已在筆記" },
    @{ Name = "cherry"; Source = "unit3-words.txt"; Status = "新字" },
    @{ Name = "Cherry"; Source = "unit3-words.txt"; Status = "檔內重複" },
    @{ Name = "grape";  Source = "unit4-words.csv"; Status = "新字" },
    @{ Name = "CHERRY"; Source = "unit4-words.csv"; Status = "與「unit3-words.txt」重複" }
  )
  if ($multiRows.Count -ne $expectMulti.Count) { $fails += "⑥：合併後列數＝$($multiRows.Count)，應為 $($expectMulti.Count)" }
  for ($i = 0; $i -lt [Math]::Min($multiRows.Count, $expectMulti.Count); $i++) {
    $r = $multiRows[$i]; $e = $expectMulti[$i]
    if ($r.Name -ne $e.Name)            { $fails += "⑥：列$i 名稱「$($r.Name)」≠「$($e.Name)」" }
    if ($r.Source -ne $e.Source)        { $fails += "⑥：列$i（$($e.Name)）來源「$($r.Source)」≠「$($e.Source)」" }
    if ($r.Status -notlike "*$($e.Status)*") { $fails += "⑥：列$i（$($e.Name)）狀態「$($r.Status)」未含「$($e.Status)」" }
  }
  if ($multiRows.Count -ge 6 -and $multiRows[5].Enabled) { $fails += "⑥：跨檔重複列（CHERRY）仍可勾選" }
  $header = Get-HeaderText $confirm
  Write-Host "* ⑥ 首行＝「$header」"
  if ($header -notlike "*2 個檔*") { $fails += "⑥：首行「$header」未含「2 個檔」" }
  if ($null -ne (Find-ByAutomationId -Root $confirm -Id "NotesImportExcluded")) { $fails += "⑥：無未納入之檔卻顯示了「未納入」行" }
  $confirmHwnd = [IntPtr]$confirm.Current.NativeWindowHandle
  Set-WindowForeground -Hwnd $confirmHwnd | Out-Null; Start-Sleep -Milliseconds 300
  Save-WindowShot -Hwnd $confirmHwnd -Path (Join-Path $OutDir "05-multi-confirm.png")
  Copy-Item (Join-Path $OutDir "05-multi-confirm.png") $manualPng -Force
  Write-Host "* 手冊圖已產出（多檔狀態）：$manualPng"
  Close-Confirm $confirm
  if ($fails.Count -eq 0) { Write-Host "* [OK] ⑥ 多選兩檔：合併、跨檔去重、來源欄皆符" -ForegroundColor Green }
  #endregion

  #region G.#320 ⑦ 真實 OLE 檔案拖放（同兩檔）落在條目區空白處 --------------------------------
  Write-Host "## G.#320 ⑦ 真實 OLE 拖放 --------------------------------" -ForegroundColor Cyan
  Set-WindowForeground -Hwnd $hwnd | Out-Null; Start-Sleep -Milliseconds 400
  $drop = Get-EntryBlankPoint
  Write-Host "* 落點（條目區卡片下方空白處）＝($($drop.X), $($drop.Y))"
  $res = Invoke-FileDrag -Files @($unit3Path, $unit4Path) -TargetX $drop.X -TargetY $drop.Y -WatchBanner
  Write-Host "* 拖曳結果：命中=$($res.HitOk) 橫幅=「$($res.Banner)」DoDragDrop 返回=$($res.Returned)（$($res.ElapsedMs) ms）效果=$($res.Effect)"
  if (-not $res.HitOk) { throw "⑦：落點被他窗覆蓋（PidAtPoint ≠ 受測 app）——本輪無法判定，據實中止" }
  if ($res.Banner -notlike "*放開後先開確認表*$probeName*") { $fails += "⑦：拖曳中未見目標夾橫幅（實得「$($res.Banner)」）" }
  if (-not $res.Returned) { $fails += "⑦：放下後 DoDragDrop 未於 10 秒內返回——放下回呼被阻塞（不得在回呼內開模態）" }
  $confirm = Wait-Confirm -TimeoutSec 15
  if ($null -eq $confirm) { $fails += "⑦：拖放兩檔後未見確認頁" }
  else {
    $c = $confirm.Current.BoundingRectangle
    $visible = [Win32Ui]::PidAtPoint([int]($c.X + $c.Width / 2), [int]($c.Y + 40)) -eq [uint32]$app.ProcessId
    $fg = [Win32Ui]::PidOf([Win32Ui]::GetForegroundWindow()) -eq [uint32]$app.ProcessId
    Write-Host "* 確認頁可見於最上層＝$visible／前景屬受測 app＝$fg（前景只記錄不判）"
    if (-not $visible) { $fails += "⑦：拖放開出之確認頁未在最上層可見" }
    $dropRows = Get-RowSnapshot $confirm
    $same = ($dropRows.Count -eq $multiRows.Count)
    for ($i = 0; $same -and $i -lt $dropRows.Count; $i++) {
      if ($dropRows[$i].Name -ne $multiRows[$i].Name -or $dropRows[$i].Source -ne $multiRows[$i].Source -or $dropRows[$i].Status -ne $multiRows[$i].Status -or $dropRows[$i].Checked -ne $multiRows[$i].Checked) { $same = $false }
    }
    if (-not $same) { $fails += "⑦：拖放之確認表與對話框多選不同（拖放 $($dropRows.Count) 列／多選 $($multiRows.Count) 列）" }
    else { Write-Host "* [OK] ⑦ 拖放之確認表與多選逐列相同（$($dropRows.Count) 列）" -ForegroundColor Green }
    Save-WindowShot -Hwnd ([IntPtr]$confirm.Current.NativeWindowHandle) -Path (Join-Path $OutDir "06-drop-confirm.png")
    Close-Confirm $confirm
  }
  $bannerAfter = Find-ByAutomationId -Root $root -Id "NotesDropBanner"
  if ($null -ne $bannerAfter) { $fails += "⑦：放下後橫幅仍殘留" }
  #endregion

  #region H.#320 ⑧ 拖入 .docx → 提示、不開確認頁 --------------------------------
  Write-Host "## H.#320 ⑧ 拖入非清單檔 --------------------------------" -ForegroundColor Cyan
  Set-WindowForeground -Hwnd $hwnd | Out-Null; Start-Sleep -Milliseconds 400
  $drop = Get-EntryBlankPoint
  $res = Invoke-FileDrag -Files @($docxPath) -TargetX $drop.X -TargetY $drop.Y -WatchToast "只能拖入"
  Write-Host "* 拖曳結果：命中=$($res.HitOk) toast=「$($res.Toast)」DoDragDrop 返回=$($res.Returned) 效果=$($res.Effect)"
  if (-not $res.HitOk) { throw "⑧：落點被他窗覆蓋——本輪無法判定，據實中止" }
  if ($res.Toast -notlike "*.txt／.csv*") { $fails += "⑧：拖入 .docx 未見「只能拖入 .txt／.csv」提示" }
  if ($res.Effect -ne "None") { $fails += "⑧：拖入 .docx 之放下效果＝$($res.Effect)，應為 None（不可放置）" }
  $confirm = Wait-Confirm -TimeoutSec 2
  if ($null -ne $confirm) { $fails += "⑧：拖入 .docx 卻開出了確認頁"; Close-Confirm $confirm }
  elseif ($res.Toast -like "*.txt／.csv*" -and $res.Effect -eq "None") { Write-Host "* [OK] ⑧ 拖入 .docx：有提示、不可放置、未開確認頁" -ForegroundColor Green }
  #endregion

  #region I.#320 ⑨ 混雜三檔（緊接 ⑧，順帶驗上一次不可放置之狀態不殘留） --------------------------------
  Write-Host "## I.#320 ⑨ 混雜三檔 --------------------------------" -ForegroundColor Cyan
  Start-Sleep -Milliseconds 2200   # 等 ⑧ 之 toast 淡出，免遮落點
  Set-WindowForeground -Hwnd $hwnd | Out-Null; Start-Sleep -Milliseconds 400
  $drop = Get-EntryBlankPoint
  $res = Invoke-FileDrag -Files @($unit3Path, $docxPath, $big5Path) -TargetX $drop.X -TargetY $drop.Y
  if (-not $res.HitOk) { throw "⑨：落點被他窗覆蓋——本輪無法判定，據實中止" }
  $confirm = Wait-Confirm -TimeoutSec 15
  if ($null -eq $confirm) { $fails += "⑨：混入不可用之檔後未開確認頁（應照常開出）" }
  else {
    $header = Get-HeaderText $confirm
    $ex = Find-ByAutomationId -Root $confirm -Id "NotesImportExcluded"
    $exText = if ($null -eq $ex) { "" } else { $ex.Current.Name }
    $rows9 = Get-RowSnapshot $confirm
    Write-Host "* ⑨ 首行＝「$header」／未納入＝「$exText」／列數＝$($rows9.Count)"
    if ($header -notlike "來源：unit3-words.txt*") { $fails += "⑨：可用來源僅 1，首行應為單檔格式「來源：unit3-words.txt…」（實得「$header」）" }
    if ($exText -notlike "*notes.docx（不是 .txt／.csv）*") { $fails += "⑨：未納入未列 notes.docx（實得「$exText」）" }
    if ($exText -notlike "*old-big5.txt（疑似不是 UTF-8 編碼）*") { $fails += "⑨：未納入未列 old-big5.txt（實得「$exText」）" }
    if ($rows9.Count -ne 4) { $fails += "⑨：列數＝$($rows9.Count)，應為 4（只來自 unit3-words.txt）" }
    Save-WindowShot -Hwnd ([IntPtr]$confirm.Current.NativeWindowHandle) -Path (Join-Path $OutDir "07-mixed-excluded.png")
    Close-Confirm $confirm
    if ($fails.Count -eq 0) { Write-Host "* [OK] ⑨ 混雜三檔：照常開出、未納入列明兩檔" -ForegroundColor Green }
  }
  #endregion

  #region J.#320 ⑪ 既有拖曳不受影響：卡片拖到另一夾 --------------------------------
  Write-Host "## J.#320 ⑪ 既有拖曳（卡片移夾） --------------------------------" -ForegroundColor Cyan
  Set-WindowForeground -Hwnd $hwnd | Out-Null; Start-Sleep -Milliseconds 400
  $txtCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)
  $tree = Find-ByAutomationId -Root $root -Id "FolderTree"
  $card = @($root.FindAll($TS::Descendants, $txtCond) | Where-Object { $_.Current.Name -eq "apple" -and -not $tree.Current.BoundingRectangle.Contains($_.Current.BoundingRectangle.TopLeft) }) | Select-Object -First 1
  $target = @($tree.FindAll($TS::Descendants, $txtCond) | Where-Object { $_.Current.Name -eq $probe2Name }) | Select-Object -First 1
  if ($null -eq $card -or $null -eq $target) { throw "⑪：找不到 apple 卡片或目標夾「$probe2Name」" }
  $cr = $card.Current.BoundingRectangle; $tr = $target.Current.BoundingRectangle
  $gx = [int]($cr.X - 20); $gy = [int]($cr.Y + $cr.Height / 2)        # 握把中心＝原文左緣往左約 20px
  $tx = [int]($tr.X + 4);  $ty = [int]($tr.Y + $tr.Height / 2)
  if ([Win32Ui]::PidAtPoint($gx, $gy) -ne [uint32]$app.ProcessId -or [Win32Ui]::PidAtPoint($tx, $ty) -ne [uint32]$app.ProcessId) { throw "⑪：握把或目標夾座標被他窗覆蓋——據實中止" }
  [MouseDrag]::Drag($gx, $gy, $tx, $ty)
  Start-Sleep -Milliseconds 900
  $folderOfApple = Get-FolderIdOf "apple"
  Write-Host "* ⑪ apple 所在夾 Id＝$folderOfApple（應為第二探針夾 $probe2Id）"
  if ($folderOfApple -ne $probe2Id) { $fails += "⑪：卡片拖到「$probe2Name」後歸屬未改（實得 $folderOfApple）——檔案拖放之穿隧處理誤傷既有拖曳" }
  else { Write-Host "* [OK] ⑪ 卡片移夾照常" -ForegroundColor Green }
  #endregion

  #region K.#320 ⑩ 全程零 AI 呼叫、條目不增 --------------------------------
  Write-Host "## K.#320 ⑩ 全程零呼叫 --------------------------------" -ForegroundColor Cyan
  $aiWin = Wait-WindowByTitle -MainRoot $root -ProcessId $app.ProcessId -TitleLike "*正在匯入*" -ExcludeHwnd $hwnd -TimeoutSec 1
  if ($null -ne $aiWin) { $fails += "⑩：AI 動作確認頁出現了" }
  $p1 = Get-ProbeEntryCount; $p2 = Get-FolderEntryCount $probe2Id
  Write-Host "* 探針夾條目＝$p1（起手 $entriesBefore，⑪ 移走 1）／第二探針夾＝$p2"
  if ($p1 + $p2 -ne $entriesBefore) { $fails += "⑩：兩探針夾條目合計 $($p1 + $p2) ≠ 起手 $entriesBefore——有寫入發生" }
  if ((Get-LedgerBytes).Length -ne $ledgerBefore.Length) { $fails += "⑩：AI 花費帳本有變動（輔證）" }
  if ($null -eq $aiWin -and $p1 + $p2 -eq $entriesBefore) { Write-Host "* [OK] ⑩ 全程 0 次 AI 呼叫、條目未增" -ForegroundColor Green }
  #endregion

  #region L.#321 ⑫ 雙語 csv：中譯來源欄、主鈕與費用依線上查詢數 --------------------------------
  Write-Host "## L.#321 ⑫ 雙語 csv 之確認表 --------------------------------" -ForegroundColor Cyan
  Set-WindowForeground -Hwnd $hwnd | Out-Null; Start-Sleep -Milliseconds 400
  # 重新點選探針夾（⑪ 之卡片拖曳後選取夾須仍為「$probeName」，以此保證目標）
  $tree = Find-ByAutomationId -Root $root -Id "FolderTree"
  $probeText = @($tree.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text))) | Where-Object { $_.Current.Name -eq $probeName }) | Select-Object -First 1
  if ($null -eq $probeText) { throw "⑫：資料夾樹找不到探針夾「$probeName」" }
  $pr = $probeText.Current.BoundingRectangle
  if ([Win32Ui]::PidAtPoint([int]($pr.X + 4), [int]($pr.Y + $pr.Height / 2)) -ne [uint32]$app.ProcessId) { throw "⑫：探針夾座標被他窗覆蓋——本輪無法判定，據實中止" }
  Click-Element -El $probeText; Start-Sleep -Milliseconds 600
  $importBtn = Find-ByAutomationId -Root $root -Id "NotesImportBtn"
  Invoke-El $importBtn
  $fileDlg = Wait-WindowByTitle -MainRoot $root -ProcessId $app.ProcessId -TitleLike "選擇英文清單*" -ExcludeHwnd $hwnd -TimeoutSec 15
  if ($null -eq $fileDlg) { throw "⑫：按匯入鈕後未見檔案對話框" }
  $dlgHwnd = [IntPtr]$fileDlg.Current.NativeWindowHandle
  [Win32Ui]::ForceForeground($dlgHwnd); Start-Sleep -Milliseconds 500
  if ([Win32Ui]::GetForegroundWindow() -ne $dlgHwnd) { throw "⑫：檔案對話框未能帶到前景，鍵入會落空" }
  [System.Windows.Forms.SendKeys]::SendWait("^a"); Start-Sleep -Milliseconds 120
  [System.Windows.Forms.SendKeys]::SendWait($unit5Path); Start-Sleep -Milliseconds 400
  [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
  $confirm = Wait-Confirm -TimeoutSec 15
  if ($null -eq $confirm) { throw "⑫：選雙語 csv 後未見確認頁" }
  $rows12 = @(Get-RowSnapshot $confirm)
  Write-Host "* ⑫ 確認頁列：$(($rows12 | ForEach-Object { "$($_.Name)/$($_.Translation)/$($_.Status)/勾=$($_.Checked)" }) -join '｜')"
  $expect12 = @(
    @{ Name = "grape"; Translation = "自備中譯：葡萄";            Checked = $true;  Status = "新字" },
    @{ Name = "kiwi";  Translation = "線上查詢";                  Checked = $true;  Status = "新字" },
    @{ Name = "mango"; Translation = "自備中譯：芒果, 熱帶水果";  Checked = $true;  Status = "新字" },
    @{ Name = "apple"; Translation = "自備中譯：蘋果";            Checked = $false; Status = "勾選＝以自備中譯更新；已在筆記" }
  )
  if ($rows12.Count -ne $expect12.Count) { $fails += "⑫：列數＝$($rows12.Count)，應為 4（表頭列略過）" }
  for ($i = 0; $i -lt [Math]::Min($rows12.Count, $expect12.Count); $i++) {
    $r = $rows12[$i]; $e = $expect12[$i]
    if ($r.Name -ne $e.Name)               { $fails += "⑫：列$i 名稱「$($r.Name)」≠「$($e.Name)」" }
    if ($r.Translation -ne $e.Translation) { $fails += "⑫：列$i（$($e.Name)）中譯來源「$($r.Translation)」≠「$($e.Translation)」" }
    if ($r.Checked -ne $e.Checked)         { $fails += "⑫：列$i（$($e.Name)）預設勾選＝$($r.Checked)，應為 $($e.Checked)" }
    if (-not $r.Status.StartsWith($e.Status)) { $fails += "⑫：列$i（$($e.Name)）狀態「$($r.Status)」未以「$($e.Status)」起首" }
  }
  $force = Find-ByAutomationId -Root $confirm -Id "NotesImportForceOnline"
  if ($null -eq $force) { $fails += "⑫：有自備中譯卻未見整批切換（NotesImportForceOnline）" }
  elseif ((Toggle-State $force) -ne [System.Windows.Automation.ToggleState]::Off) { $fails += "⑫：整批切換預設應為未勾" }
  $btn = Find-ByAutomationId -Root $confirm -Id "NotesImportConfirm"
  Write-Host "* ⑫ 主鈕＝「$($btn.Current.Name)」／費用＝「$(Get-CostText $confirm)」"
  if ($btn.Current.Name -ne "加入 3 字（查詢 1 字）") { $fails += "⑫：主鈕「$($btn.Current.Name)」≠「加入 3 字（查詢 1 字）」" }
  $cost = Get-CostText $confirm
  if ($cost -notlike "*共 1 次 AI 查詢*" -or $cost -notlike "*另 2 字採用自備中譯*") { $fails += "⑫：費用揭露「$cost」未含「共 1 次 AI 查詢」與「另 2 字採用自備中譯」" }
  $confirmHwnd = [IntPtr]$confirm.Current.NativeWindowHandle
  Set-WindowForeground -Hwnd $confirmHwnd | Out-Null; Start-Sleep -Milliseconds 300
  Save-WindowShot -Hwnd $confirmHwnd -Path (Join-Path $OutDir "08-own-translation-confirm.png")
  Copy-Item (Join-Path $OutDir "08-own-translation-confirm.png") $ownPng -Force
  Write-Host "* 手冊圖已產出（自備中譯狀態）：$ownPng"
  #endregion

  #region M.#321 ⑬ 整批切換「也改查線上」與還原 --------------------------------
  Write-Host "## M.#321 ⑬ 整批切換 --------------------------------" -ForegroundColor Cyan
  if ($null -ne $force) {
    $force.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 500
    $rows13 = @(Get-RowSnapshot $confirm)
    $btn = Find-ByAutomationId -Root $confirm -Id "NotesImportConfirm"
    Write-Host "* ⑬ 切換後中譯來源：$(($rows13 | ForEach-Object { $_.Translation }) -join '｜')／主鈕「$($btn.Current.Name)」"
    if (@($rows13 | Where-Object { $_.Translation -ne "線上查詢" }).Count -gt 0) { $fails += "⑬：切換後仍有非「線上查詢」之中譯來源" }
    if ($btn.Current.Name -ne "查詢並加入 3 字") { $fails += "⑬：切換後主鈕「$($btn.Current.Name)」≠「查詢並加入 3 字」" }
    if ($rows13.Count -ge 4 -and -not $rows13[3].Status.StartsWith("已在筆記")) { $fails += "⑬：切換後 apple 狀態「$($rows13[3].Status)」未回到「已在筆記…重新查詢」" }
    $force.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 500
    $btn = Find-ByAutomationId -Root $confirm -Id "NotesImportConfirm"
    $rowsBack = @(Get-RowSnapshot $confirm)
    if ($btn.Current.Name -ne "加入 3 字（查詢 1 字）" -or $rowsBack[0].Translation -ne "自備中譯：葡萄") { $fails += "⑬：取消切換後未還原（主鈕「$($btn.Current.Name)」、首列「$($rowsBack[0].Translation)」）" }
    else { Write-Host "* [OK] ⑬ 整批切換與還原皆符" -ForegroundColor Green }
  }
  #endregion

  #region N.#321 ⑭ 全自備實按加入：不開 AI 動作頁、零查詢、寫入自備中譯 --------------------------------
  Write-Host "## N.#321 ⑭ 全自備實按加入 --------------------------------" -ForegroundColor Cyan
  $kiwiBox = Find-ByAutomationId -Root $confirm -Id "NotesImportRow1"
  if ($null -eq $kiwiBox -or $kiwiBox.Current.Name -ne "kiwi") { throw "⑭：找不到 kiwi 列（NotesImportRow1）" }
  if ((Toggle-State $kiwiBox) -ne [System.Windows.Automation.ToggleState]::On) { throw "⑭：kiwi 前置應為勾選狀態——不盲切，據實中止" }
  $kiwiBox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 500
  $btn = Find-ByAutomationId -Root $confirm -Id "NotesImportConfirm"
  $cost = Get-CostText $confirm
  Write-Host "* ⑭ 取消 kiwi 後：主鈕「$($btn.Current.Name)」／費用「$cost」"
  if ($btn.Current.Name -ne "加入 2 字（不查詢）") { throw "⑭：主鈕「$($btn.Current.Name)」≠「加入 2 字（不查詢）」——不實按，據實中止（避免走到線上查詢）" }
  if ($cost -notlike "*不會呼叫 AI*") { $fails += "⑭：費用揭露「$cost」未含「不會呼叫 AI」" }
  Invoke-El $btn
  $msg = $null
  $deadline = (Get-Date).AddSeconds(15)
  while ((Get-Date) -lt $deadline -and $null -eq $msg) {
    $w = Wait-WindowByTitle -MainRoot $root -ProcessId $app.ProcessId -TitleLike "匯入清單" -ExcludeHwnd $hwnd -TimeoutSec 1
    if ($null -ne $w -and $null -eq (Find-ByAutomationId -Root $w -Id "NotesImportConfirm")) { $msg = $w }
  }
  if ($null -eq $msg) { $fails += "⑭：按下後未見結果訊息框（Title 匯入清單）" }
  else {
    # Win32 訊息框之文字元件可能稍後才填入 UIA 名稱：輪詢至含「匯入完成」或逾時（任何控制型別皆看）
    $msgText = ""
    $tEnd = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $tEnd -and $msgText -notlike "*匯入完成*") {
      $msgText = (@($msg.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join " ")
      if ($msgText -notlike "*匯入完成*") { Start-Sleep -Milliseconds 300 }
    }
    Write-Host "* ⑭ 結果訊息＝「$msgText」"
    if ($msgText -notlike "*已加入 2 字*" -or $msgText -notlike "*採用自備中譯*") { $fails += "⑭：結果訊息未含「已加入 2 字」與「採用自備中譯」（實得「$msgText」）" }
    Save-WindowShot -Hwnd ([IntPtr]$msg.Current.NativeWindowHandle) -Path (Join-Path $OutDir "09-own-result.png")
    $ok = @($msg.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button))) | Where-Object { $_.Current.Name -in @("OK", "確定") }) | Select-Object -First 1
    if ($null -ne $ok) { Invoke-El $ok; Start-Sleep -Milliseconds 600 }
  }
  $aiWin = Wait-WindowByTitle -MainRoot $root -ProcessId $app.ProcessId -TitleLike "*正在匯入*" -ExcludeHwnd $hwnd -TimeoutSec 1
  if ($null -ne $aiWin) { $fails += "⑭：全自備卻開了 AI 動作確認頁（正在匯入…）" }
  if ((Get-LedgerBytes).Length -ne $ledgerBefore.Length) { $fails += "⑭：AI 花費帳本有變動" }
  $j = Get-Content (Join-Path $appData "notes.json") -Raw -Encoding UTF8 | ConvertFrom-Json
  $all = @(Get-AllFolders $j.Folders | ForEach-Object { @($_.Entries) })
  $g = $all | Where-Object { $_.Original -eq "grape" } | Select-Object -First 1
  $m = $all | Where-Object { $_.Original -eq "mango" } | Select-Object -First 1
  $k = $all | Where-Object { $_.Original -eq "kiwi" } | Select-Object -First 1
  Write-Host "* ⑭ notes.json：grape＝「$($g.Translation)」音標「$($g.Phonetic)」／mango＝「$($m.Translation)」／kiwi 存在＝$($null -ne $k)"
  if ($null -eq $g -or $g.Translation -ne "葡萄" -or $g.Phonetic -ne "") { $fails += "⑭：grape 未以自備中譯「葡萄」寫入且音標空" }
  if ($null -eq $m -or $m.Translation -ne "芒果, 熱帶水果") { $fails += "⑭：mango 未以自備中譯「芒果, 熱帶水果」寫入" }
  if ($null -ne $k) { $fails += "⑭：kiwi 未勾卻被寫入" }
  if ((Get-FolderIdOf "grape") -ne $probeId) { $fails += "⑭：grape 未寫入目前選取夾「$probeName」" }
  if ($fails.Count -eq 0) { Write-Host "* [OK] ⑫–⑭ 自備中譯：中譯來源欄、整批切換、全自備零查詢寫入皆符" -ForegroundColor Green }
  #endregion

}
finally {
  #region F.收尾：關程式並還原 APPDATA --------------------------------
  Write-Host "## F.收尾 --------------------------------" -ForegroundColor Cyan
  Get-Process -Name "LingoIsland" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) }
  Start-Sleep -Milliseconds 500
  $env:OPENAI_API_KEY = $origApiKey   # #321：還原本行程之金鑰環境變數
  if (Test-Path $backupDir) {
    Remove-Item -Path $appData -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -Path $backupDir -Destination $appData -Recurse -Force
    Remove-Item -Path $backupDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "* 已還原 $appData"
  }
  if (Test-Path $sampleDir) { Remove-Item $sampleDir -Recurse -Force -ErrorAction SilentlyContinue }
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
Write-Host "* 結果：PASS（#309 訴求 1–5、#320 ⑥–⑪、#321 ⑫–⑭ 全數成立；全程 0 次 OpenAI 呼叫）" -ForegroundColor Green
exit 0
#endregion

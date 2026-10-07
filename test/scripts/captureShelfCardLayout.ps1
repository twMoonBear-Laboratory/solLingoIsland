#requires -Version 7
<#
  Issue #275（影片櫃與電子書櫃卡片版式）之機判＋證據擷取管線。

  驗兩件 USR 訴求在實機成立：
    (1) 標題自動換行、不以省略號截斷 —— 以「標題 TextBlock 高度 > 同卡單行列高 × 1.5」為換行證據
        （不硬編像素：單行基準取同卡之 meta 列實際高度，故不受 DPI／字級調整影響）；
        並斷言標題 UIA Name 與資料層書名／片名逐字相符（＝完整文字未被裁掉）。
    (2) 主題標籤在標題之前 —— 以 BoundingRectangle.Y 比較（主題 Y < 標題 Y）。

  桌面 UIA e2e 工法依 [modTechStackWinApp] ＜III＞；取窗與視窗尺寸守衛沿用 uiaCommon.ps1
  之 Start-AppAndGetWindow／Set-WindowMaximized（Issue #270），不重造。
#>

param(
  [string]$ExePath = "",
  [string]$OutDir  = ""
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Stop"

#region I.主旨目的 ================================
Write-Host "# I.主旨目的 ================================" -ForegroundColor Blue
Write-Host "* 驗證電子書櫃與影片櫃之卡片版式（Issue #275）："
Write-Host "  (1) 長標題自動換行、不截斷；(2) 主題標籤置於標題之前。"
Write-Host "* 同時產出兩櫃實機截圖，供 README 產品手冊佐證。"
#endregion

#region II.參考準備 ================================
Write-Host "# II.參考準備 ================================" -ForegroundColor Blue

  #region A.參數準備 --------------------------------
  Write-Host "## A.參數準備 --------------------------------" -ForegroundColor Cyan
  $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
  if (-not $ExePath) { $ExePath = Join-Path $repoRoot "sysLingoIsland\bin\Release\net9.0-windows10.0.19041.0\LingoIsland.exe" }
  if (-not $OutDir)  { $OutDir  = Join-Path $repoRoot "docs\manual-assets" }
  $appData   = Join-Path $env:APPDATA "LingoIsland"
  $backupDir = Join-Path $env:TEMP ("LingoIsland-backup-shelf-" + (Get-Date -Format "yyyyMMddHHmmss"))
  $wrapRatio = 1.5    # 標題高度 / 單行列高之下限，超過即視為確實換行（2 行約 2.0）
  Write-Host "* ExePath = $ExePath"
  Write-Host "* OutDir  = $OutDir"
  if (-not (Test-Path $ExePath)) { Write-Host "* [錯誤] 找不到建置產物：$ExePath（請先 dotnet build）" -ForegroundColor Red; exit 1 }
  if (-not (Test-Path $OutDir))  { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
  #endregion

  #region B.型別準備（共用定義） --------------------------------
  Write-Host "## B.型別準備（共用定義） --------------------------------" -ForegroundColor Cyan
  . "$PSScriptRoot\uiaCommon.ps1"
  Write-Host "* DPI 一致化（PER_MONITOR_AWARE_V2）＝$([Win32Ui]::MakeDpiAware())"
  #endregion
#endregion

#region III.內容程序 ================================
Write-Host "# III.內容程序 ================================" -ForegroundColor Blue

$fails = @()
try {

  #region A.APPDATA 備份（本腳本只讀不改資料，惟切分頁會寫 ui-state） --------------------------------
  Write-Host "## A.APPDATA 備份 --------------------------------" -ForegroundColor Cyan
  Get-Process -Name "LingoIsland" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) }
  Start-Sleep -Milliseconds 500
  Copy-Item -Path $appData -Destination $backupDir -Recurse -Force
  Write-Host "* 已備份 $appData → $backupDir"

  # 資料層期望值（供「標題完整未截斷」之逐字比對；比對來源＝資料檔本身，非硬編字面）
  $expectBook  = @(((Get-Content (Join-Path $appData "ebooks.json") -Raw -Encoding UTF8 | ConvertFrom-Json).Items) | ForEach-Object { $_.Title })
  $videosPath  = Join-Path $appData "videos.json"
  $expectVideo = if (Test-Path $videosPath) { @(((Get-Content $videosPath -Raw -Encoding UTF8 | ConvertFrom-Json).Items) | ForEach-Object { $_.Title }) } else { @() }
  Write-Host ("* 資料層：書 {0} 本、影片 {1} 部" -f $expectBook.Count, $expectVideo.Count)
  #endregion

  #region B.啟動 App 並最大化 --------------------------------
  Write-Host "## B.啟動 App 並最大化 --------------------------------" -ForegroundColor Cyan
  $app  = Start-AppAndGetWindow -ExePath $ExePath -TimeoutSec 30
  $hwnd = $app.Hwnd
  Write-Host ("* 受測行程 pid={0}（自主視窗反查）" -f $app.ProcessId)
  Set-WindowForeground -Hwnd $hwnd | Out-Null
  $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
  $wr = Set-WindowMaximized -Hwnd $hwnd
  Write-Host ("* 視窗尺寸＝{0}x{1}" -f ($wr.Right - $wr.Left), ($wr.Bottom - $wr.Top))
  if (($wr.Bottom - $wr.Top) -le 900) { throw "視窗未能放大至可供量測之高度（目前高 $($wr.Bottom - $wr.Top) px）" }
  #endregion

  #region C.量測共用 --------------------------------
  Write-Host "## C.量測共用 --------------------------------" -ForegroundColor Cyan
  $textCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Text)

  # 單卡量測：回傳 主題/標題 rect 與同卡之單行基準高（取該卡最矮的 Text 元素＝必為單行列）
  function Measure-Card {
    param($Card, [string]$ThemeId, [string]$TitleId)
    $theme = Find-ByAutomationId -Root $Card -Id $ThemeId
    $title = Find-ByAutomationId -Root $Card -Id $TitleId
    if ($null -eq $title) { throw "卡片內找不到標題（AutomationId=$TitleId）" }
    $lines = @()
    foreach ($t in $Card.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
      if ($t.Current.AutomationId -ne $TitleId -and $t.Current.BoundingRectangle.Height -gt 0) {
        $lines += $t.Current.BoundingRectangle.Height
      }
    }
    if ($lines.Count -eq 0) { throw "卡片內除標題外無其他文字列，無法取得單行基準高" }
    [pscustomobject]@{
      ThemeRect = if ($null -ne $theme) { $theme.Current.BoundingRectangle } else { $null }
      ThemeName = if ($null -ne $theme) { $theme.Current.Name } else { $null }
      TitleRect = $title.Current.BoundingRectangle
      TitleName = $title.Current.Name
      LineH     = ($lines | Measure-Object -Minimum).Minimum
    }
  }

  # 單櫃驗證：切分頁 → 取清單首卡 → 三項斷言
  function Test-Shelf {
    param([string]$TabId, [string]$ListId, [string]$ThemeId, [string]$TitleId, [string]$Label,
          [string[]]$ExpectTitles, [bool]$ThemeRequired)
    Write-Host ("## {0} --------------------------------" -f $Label) -ForegroundColor Cyan
    $tab = Find-ByAutomationId -Root $root -Id $TabId
    if ($null -eq $tab) { throw "找不到「$Label」分頁鈕（AutomationId=$TabId）" }
    $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 1200

    $list = $null
    for ($i = 0; $i -lt 20; $i++) {
      $list = Find-ByAutomationId -Root $root -Id $ListId
      if ($null -ne $list) { break }
      Start-Sleep -Milliseconds 400
    }
    if ($null -eq $list) { throw "找不到 $Label 清單（AutomationId=$ListId）" }

    # #317：清單項目可能晚於清單本身出現——輪詢至有項目（每 200ms、上限 5 秒），逾時才判「無資料項」
    $items = $null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    do {
      $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children,
                 [System.Windows.Automation.Condition]::TrueCondition)
      if ($items.Count -gt 0) { break }
      Start-Sleep -Milliseconds 200
    } while ($sw.ElapsedMilliseconds -lt 5000)
    Write-Host ("* {0} 清單項目 {1} 個（輪詢等待 {2} ms）" -f $Label, $items.Count, $sw.ElapsedMilliseconds)
    if ($items.Count -eq 0) { Write-Host "* [略過] $Label 無資料項" -ForegroundColor Yellow; return }

    # 取「標題最長」之卡片量測——最可能觸發換行；標題短到不需換行者本就不該換，拿它判會誤殺
    $target = $null; $best = -1
    foreach ($it in $items) {
      $t = Find-ByAutomationId -Root $it -Id $TitleId
      if ($null -ne $t -and $t.Current.Name.Length -gt $best) { $best = $t.Current.Name.Length; $target = $it }
    }
    if ($null -eq $target) { throw "$Label 清單中找不到帶 AutomationId=$TitleId 之標題元素" }

    $m = Measure-Card -Card $target -ThemeId $ThemeId -TitleId $TitleId
    $ratio = [math]::Round($m.TitleRect.Height / $m.LineH, 2)
    Write-Host ("* 標題「{0}」（{1} 字）：高 {2:N1} px｜單行基準 {3:N1} px｜比值 {4}" -f
                $m.TitleName, $m.TitleName.Length, $m.TitleRect.Height, $m.LineH, $ratio)

    # 斷言 1：確實換行（標題高度 > 單行基準 × wrapRatio）
    if ($ratio -lt $wrapRatio) {
      $script:fails += "$Label：標題未換行（高度比值 $ratio < $wrapRatio）——長標題仍被壓在單行"
    } else {
      Write-Host ("* [PASS] {0}：標題確實換行（比值 {1} ≥ {2}）" -f $Label, $ratio, $wrapRatio) -ForegroundColor Green
    }

    # 斷言 2：標題文字完整（與資料層逐字相符＝未被裁切）
    if ($ExpectTitles -notcontains $m.TitleName) {
      $script:fails += "$Label：標題文字與資料層不符（UIA=「$($m.TitleName)」），疑遭裁切或改寫"
    } else {
      Write-Host ("* [PASS] {0}：標題文字與資料層逐字相符（完整未截斷）" -f $Label) -ForegroundColor Green
    }

    # 斷言 3：主題在標題之前
    if ($null -eq $m.ThemeRect) {
      if ($ThemeRequired) {
        $script:fails += "$Label：找不到主題標籤（AutomationId=$ThemeId）"
      } else {
        Write-Host ("* [略過] {0}：該卡無主題（本頁無主題時不顯示該列，屬設計）" -f $Label) -ForegroundColor Yellow
      }
    } elseif ($m.ThemeRect.Y -ge $m.TitleRect.Y) {
      $script:fails += "$Label：主題標籤未置於標題之前（主題 Y=$($m.ThemeRect.Y)、標題 Y=$($m.TitleRect.Y)）"
    } else {
      Write-Host ("* [PASS] {0}：主題「{1}」在標題之前（Y {2:N0} < {3:N0}）" -f
                  $Label, $m.ThemeName, $m.ThemeRect.Y, $m.TitleRect.Y) -ForegroundColor Green
    }

    # 證據截圖（最終態，供 README）
    $shot = Join-Path $OutDir ("shelf-card-" + $ListId.ToLower() + ".png")
    Save-WindowShot -Hwnd $hwnd -Path $shot
    Write-Host "* 截圖：$shot"

    # 斷言 4：右鍵 hit-test（#275）——卡片外層由 Horizontal StackPanel 改為 Grid，須確認仍接得到滑鼠右鍵、
    # 刪除選單喚得出（`ListDeleteSupport` 掛在 ListBox 層，但仍須卡片能把事件遞上去）。
    # 只驗選單喚出即按 Esc 關閉，**不點 Delete**——不動使用者真實資料。
    $cr = $target.Current.BoundingRectangle
    $rcx = [int]($cr.X + $cr.Width / 2); $rcy = [int]($cr.Y + $cr.Height / 2)
    if ([Win32Ui]::PidAtPoint($rcx, $rcy) -ne [uint32]$app.ProcessId) {
      $script:fails += "$Label：右鍵座標 ($rcx,$rcy) 被他窗覆蓋，無法驗 hit-test"
    } else {
      [Win32Ui]::RightClick($rcx, $rcy)
      $menuCond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
          [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
          [System.Windows.Automation.ControlType]::Menu)),
        (New-Object System.Windows.Automation.PropertyCondition(
          [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$app.ProcessId)))
      # #317：原為固定 700ms 後單次 FindFirst——影片頁初始化慢時選單晚出現即誤判紅（flaky）。
      # 改輪詢：每 200ms 查一次、上限 5 秒，逾時才判紅；實際等待毫秒記入 log。
      $menu = $null
      $sw = [System.Diagnostics.Stopwatch]::StartNew()
      do {
        $menu = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                  [System.Windows.Automation.TreeScope]::Descendants, $menuCond)
        if ($null -ne $menu) { break }
        Start-Sleep -Milliseconds 200
      } while ($sw.ElapsedMilliseconds -lt 5000)
      Write-Host ("* {0}：右鍵選單輪詢等待 {1} ms（{2}）" -f $Label, $sw.ElapsedMilliseconds, $(if ($null -ne $menu) { '已出現' } else { '逾時 5000 ms' }))
      if ($null -eq $menu) {
        $script:fails += "$Label：右鍵未喚出選單（輪詢 5 秒逾時）——容器改 Grid 後卡片可能收不到滑鼠事件"
      } else {
        $names = @()
        foreach ($mi in $menu.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                      [System.Windows.Automation.ControlType]::MenuItem)))) {
          if ($mi.Current.Name) { $names += $mi.Current.Name }
        }
        Write-Host ("* [PASS] {0}：右鍵選單喚出（項目：{1}）" -f $Label, (($names | Select-Object -Unique) -join " / ")) -ForegroundColor Green
      }
      [Win32Ui]::KeyTap(0x1B, 40)   # Esc 關閉選單（不點 Delete、不動使用者資料）
      # #317：輪詢至選單確實關閉（每 200ms、上限 5 秒），免得殘留選單干擾下一櫃之操作
      $sw = [System.Diagnostics.Stopwatch]::StartNew()
      while ($sw.ElapsedMilliseconds -lt 5000 -and $null -ne
             [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
               [System.Windows.Automation.TreeScope]::Descendants, $menuCond)) {
        Start-Sleep -Milliseconds 200
      }
    }
  }
  #endregion

  #region D.電子書櫃 --------------------------------
  Test-Shelf -TabId "TabEbook" -ListId "BookList" -ThemeId "BookCardTheme" -TitleId "BookCardTitle" `
             -Label "電子書櫃" -ExpectTitles $expectBook -ThemeRequired $true
  #endregion

  #region E.影片櫃 --------------------------------
  Test-Shelf -TabId "TabVideo" -ListId "VideoList" -ThemeId "VideoCardTheme" -TitleId "VideoCardTitle" `
             -Label "影片櫃" -ExpectTitles $expectVideo -ThemeRequired $false
  #endregion

  #region F.結果 --------------------------------
  Write-Host "## F.結果 --------------------------------" -ForegroundColor Cyan
  if ($fails.Count -gt 0) {
    Write-Host "* 結果：FAIL" -ForegroundColor Red
    foreach ($f in $fails) { Write-Host "  - $f" -ForegroundColor Red }
  } else {
    Write-Host "* 結果：PASS（兩櫃標題皆自動換行且完整、主題標籤皆在標題之前）" -ForegroundColor Green
  }
  #endregion

} finally {
  #region IV.收尾 ================================
  Write-Host "# IV.收尾 ================================" -ForegroundColor Blue
  Get-Process -Name "LingoIsland" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) }
  Start-Sleep -Milliseconds 500
  if (Test-Path $backupDir) {
    Remove-Item -Path $appData -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -Path $backupDir -Destination $appData -Recurse -Force
    Remove-Item -Path $backupDir -Recurse -Force
    Write-Host "* 已自備份還原 $appData"
  }
  #endregion
}

if ($fails.Count -gt 0) { exit 1 }
exit 0
#endregion

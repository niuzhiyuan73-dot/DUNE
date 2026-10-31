#requires -Version 5.1
# 自定义玩法（图形界面版）：勾选内容集 / 模式 / 阵营 / 叠加，一键生成并安装。
# 全程鼠标操作，不再需要键盘输入编号。
# 本文件须为 UTF-8 with BOM。

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$Root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$Builder = Join-Path $Root "mods\tools\build_presets.py"
$OutDir = Join-Path $Root "mods\out"
$LogFile = Join-Path $OutDir "last_build.log"

trap {
    [System.Windows.Forms.MessageBox]::Show(
        "执行失败：`n`n" + $_.Exception.Message,
        "自定义玩法", "OK", "Error") | Out-Null
    exit 1
}

# ---------------------------------------------------------------- 配色 / 字体
$ClrBg     = [System.Drawing.Color]::FromArgb(245, 247, 250)
$ClrCard   = [System.Drawing.Color]::White
$ClrText   = [System.Drawing.Color]::FromArgb(31, 35, 40)
$ClrSub    = [System.Drawing.Color]::FromArgb(87, 96, 106)
$ClrAccent = [System.Drawing.Color]::FromArgb(47, 111, 235)
$ClrDanger = [System.Drawing.Color]::FromArgb(163, 45, 45)

$FontUI    = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)
$FontHint  = New-Object System.Drawing.Font("Microsoft YaHei UI", 8.5)
$FontTitle = New-Object System.Drawing.Font("Microsoft YaHei UI", 14, [System.Drawing.FontStyle]::Bold)
$FontBtn   = New-Object System.Drawing.Font("Microsoft YaHei UI", 10, [System.Drawing.FontStyle]::Bold)
$FontMono  = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)

# ---------------------------------------------------------------- Python 定位
$PyCandidates = @(
    (Join-Path $env:USERPROFILE ".workbuddy\binaries\python\versions\3.13.12\python.exe"),
    "python"
)
$script:Py = $null
foreach ($c in $PyCandidates) {
    if ($c -eq "python") { $script:Py = "python"; break }
    if (Test-Path -LiteralPath $c) { $script:Py = $c; break }
}

function Test-UnityPy {
    $out = & $script:Py -c "import UnityPy" 2>&1
    return ($LASTEXITCODE -eq 0)
}

# ---------------------------------------------------------------- 选项定义
$SetDefs = @(
    @{ Text = "基础组合（必需）";  Key = "BaseSet"; Locked = $true },
    @{ Text = "伊克斯的崛起";      Key = "RiseOfIx" },
    @{ Text = "不朽";              Key = "Immortality" },
    @{ Text = "崛起";              Key = "Uprising" },
    @{ Text = "宇联商会模块";      Key = "CHOAMModule" }
)

$ModeDefs = @(
    @{ Text = "马拉松（20 分，冲突可循环）";        Key = "marathon" },
    @{ Text = "分阵营（10 分，标准冲突）";          Key = "faction" },
    @{ Text = "分阵营 Pro（12 分，5 二级 + 5 三级）"; Key = "faction_pro" },
    @{ Text = "分阵营 Extra（14 分，3 张三级循环）";   Key = "faction_extra" }
)

$FacDefs = @(
    @{ Text = "弗雷曼";   Key = "fremen" },
    @{ Text = "姐妹会";   Key = "bene" },
    @{ Text = "宇航工会"; Key = "guild" },
    @{ Text = "皇帝";     Key = "emperor" }
)

$ModDefs = @(
    @{ Text = "霍尔兹曼跃迁";              Key = "holtzman" },
    @{ Text = "最后的时代（3 工人）";       Key = "lasttime" },
    @{ Text = "最后的时代 Ultra（4 工人 + 引擎）"; Key = "lasttime_ultra" },
    @{ Text = "议会霸权";                  Key = "council" }
)

# ---------------------------------------------------------------- 窗体
$form = New-Object System.Windows.Forms.Form
$form.Text = "自定义玩法 · Dune Imperium"
$form.ClientSize = New-Object System.Drawing.Size(736, 786)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedSingle"
$form.MaximizeBox = $false
$form.BackColor = $ClrBg
$form.Font = $FontUI
$form.AutoScaleMode = "Font"

$lblTitle = New-Object System.Windows.Forms.Label
$lblTitle.Text = "自定义玩法"
$lblTitle.Font = $FontTitle
$lblTitle.ForeColor = $ClrText
$lblTitle.Location = New-Object System.Drawing.Point(20, 12)
$lblTitle.Size = New-Object System.Drawing.Size(696, 30)
$form.Controls.Add($lblTitle)

$lblSub = New-Object System.Windows.Forms.Label
$lblSub.Text = "勾选后点右下角「生成并安装」。生成过程中窗口会短暂无响应，属正常现象。"
$lblSub.Font = $FontHint
$lblSub.ForeColor = $ClrSub
$lblSub.Location = New-Object System.Drawing.Point(22, 46)
$lblSub.Size = New-Object System.Drawing.Size(696, 22)
$form.Controls.Add($lblSub)

function New-Group($text, $x, $y, $w, $h) {
    $g = New-Object System.Windows.Forms.GroupBox
    $g.Text = $text
    $g.Location = New-Object System.Drawing.Point($x, $y)
    $g.Size = New-Object System.Drawing.Size($w, $h)
    $g.BackColor = $ClrCard
    $g.ForeColor = $ClrText
    $g.Font = $FontUI
    return $g
}

# ---- 内容集
$grpSets = New-Group "内容集" 20 78 344 172
$iy = 30
foreach ($d in $SetDefs) {
    $cb = New-Object System.Windows.Forms.CheckBox
    $cb.Text = $d.Text
    $cb.Tag = $d.Key
    $cb.Location = New-Object System.Drawing.Point(16, $iy)
    $cb.Size = New-Object System.Drawing.Size(300, 24)
    $cb.Checked = $true
    if ($d.Locked) {
        $cb.Enabled = $false
        $cb.Checked = $true
    }
    $grpSets.Controls.Add($cb)
    $iy += 27
}
$form.Controls.Add($grpSets)

# ---- 模式
$grpMode = New-Group "模式" 372 78 344 172
$my = 30
foreach ($d in $ModeDefs) {
    $rb = New-Object System.Windows.Forms.RadioButton
    $rb.Text = $d.Text
    $rb.Tag = $d.Key
    $rb.Location = New-Object System.Drawing.Point(16, $my)
    $rb.Size = New-Object System.Drawing.Size(316, 24)
    $grpMode.Controls.Add($rb)
    $my += 36
}
$form.Controls.Add($grpMode)

# ---- 阵营
$grpFac = New-Group "阵营（四家各不同，只选你自己那家）" 20 262 344 150
$fy = 30
foreach ($d in $FacDefs) {
    $rb = New-Object System.Windows.Forms.RadioButton
    $rb.Text = $d.Text
    $rb.Tag = $d.Key
    $rb.Location = New-Object System.Drawing.Point(16, $fy)
    $rb.Size = New-Object System.Drawing.Size(300, 24)
    $grpFac.Controls.Add($rb)
    $fy += 28
}
$form.Controls.Add($grpFac)

# ---- 叠加
$grpMods = New-Group "叠加玩法（可多选，可全不选）" 372 262 344 150
$ky = 30
foreach ($d in $ModDefs) {
    $cb = New-Object System.Windows.Forms.CheckBox
    $cb.Text = $d.Text
    $cb.Tag = $d.Key
    $cb.Location = New-Object System.Drawing.Point(16, $ky)
    $cb.Size = New-Object System.Drawing.Size(300, 24)
    $grpMods.Controls.Add($cb)
    $ky += 28
}
$form.Controls.Add($grpMods)

# ---- 先手
$grpFirst = New-Group "先手" 20 424 696 78
$rbFirstRandom = New-Object System.Windows.Forms.RadioButton
$rbFirstRandom.Text = "随机排座（推荐）"
$rbFirstRandom.Tag = "random"
$rbFirstRandom.Location = New-Object System.Drawing.Point(16, 30)
$rbFirstRandom.Size = New-Object System.Drawing.Size(170, 24)
$rbFirstRandom.Checked = $true
$grpFirst.Controls.Add($rbFirstRandom)

$rbFirstHuman = New-Object System.Windows.Forms.RadioButton
$rbFirstHuman.Text = "我先手"
$rbFirstHuman.Tag = "human"
$rbFirstHuman.Location = New-Object System.Drawing.Point(196, 30)
$rbFirstHuman.Size = New-Object System.Drawing.Size(120, 24)
$grpFirst.Controls.Add($rbFirstHuman)

$lblFirstHint = New-Object System.Windows.Forms.Label
$lblFirstHint.Text = "座位同时决定先手与棋子颜色"
$lblFirstHint.Font = $FontHint
$lblFirstHint.ForeColor = $ClrSub
$lblFirstHint.Location = New-Object System.Drawing.Point(330, 32)
$lblFirstHint.Size = New-Object System.Drawing.Size(350, 20)
$grpFirst.Controls.Add($lblFirstHint)
$form.Controls.Add($grpFirst)

# ---- 按钮
$btnBuild = New-Object System.Windows.Forms.Button
$btnBuild.Text = "生成并安装"
$btnBuild.Font = $FontBtn
$btnBuild.FlatStyle = "Flat"
$btnBuild.FlatAppearance.BorderSize = 0
$btnBuild.BackColor = $ClrAccent
$btnBuild.ForeColor = [System.Drawing.Color]::White
$btnBuild.Location = New-Object System.Drawing.Point(20, 514)
$btnBuild.Size = New-Object System.Drawing.Size(200, 44)
$btnBuild.Cursor = [System.Windows.Forms.Cursors]::Hand
$form.Controls.Add($btnBuild)

$btnClose = New-Object System.Windows.Forms.Button
$btnClose.Text = "关闭"
$btnClose.Font = $FontUI
$btnClose.FlatStyle = "Flat"
$btnClose.FlatAppearance.BorderSize = 1
$btnClose.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(200, 206, 214)
$btnClose.BackColor = $ClrCard
$btnClose.ForeColor = $ClrText
$btnClose.Location = New-Object System.Drawing.Point(236, 514)
$btnClose.Size = New-Object System.Drawing.Size(110, 44)
$btnClose.Cursor = [System.Windows.Forms.Cursors]::Hand
$form.Controls.Add($btnClose)

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Text = ""
$lblStatus.Font = $FontUI
$lblStatus.ForeColor = $ClrSub
$lblStatus.Location = New-Object System.Drawing.Point(372, 526)
$lblStatus.Size = New-Object System.Drawing.Size(344, 22)
$form.Controls.Add($lblStatus)

$txtLog = New-Object System.Windows.Forms.TextBox
$txtLog.Multiline = $true
$txtLog.ReadOnly = $true
$txtLog.ScrollBars = "Both"
$txtLog.WordWrap = $false
$txtLog.Font = $FontMono
$txtLog.BackColor = $ClrCard
$txtLog.ForeColor = $ClrSub
$txtLog.BorderStyle = "FixedSingle"
$txtLog.Location = New-Object System.Drawing.Point(20, 570)
$txtLog.Size = New-Object System.Drawing.Size(696, 196)
$form.Controls.Add($txtLog)

# ---------------------------------------------------------------- 默认选择
$grpSets.Controls | Where-Object { $_.Tag -eq "BaseSet" } | ForEach-Object {
    $_.Checked = $true
    $_.Enabled = $false
}
$grpMode.Controls | Where-Object { $_.Tag -eq "faction_pro" } | ForEach-Object { $_.Checked = $true }
$grpFac.Controls  | Where-Object { $_.Tag -eq "fremen" }     | ForEach-Object { $_.Checked = $true }

function Update-FactionEnabled {
    $mode = ($grpMode.Controls | Where-Object { $_.Checked } | Select-Object -First 1).Tag
    $enabled = ($mode -ne "marathon")
    $grpFac.Enabled = $enabled
    $grpFac.ForeColor = if ($enabled) { $ClrText } else { $ClrSub }
}
foreach ($rb in $grpMode.Controls) {
    $rb.Add_CheckedChanged({ Update-FactionEnabled })
}

# 「最后的时代」与 Ultra 互斥：勾一个自动取消另一个
function Sync-EraExclusive($sender) {
    if (-not $sender -or -not $sender.Checked) { return }
    foreach ($c in $grpMods.Controls) {
        if ($c -is [System.Windows.Forms.CheckBox] -and $c -ne $sender) {
            if (($c.Tag -eq "lasttime" -and $sender.Tag -eq "lasttime_ultra") -or
                ($c.Tag -eq "lasttime_ultra" -and $sender.Tag -eq "lasttime")) {
                $c.Checked = $false
            }
        }
    }
}
foreach ($cb in $grpMods.Controls) {
    $cb.Add_CheckedChanged({ Sync-EraExclusive $this })
}

$btnClose.Add_Click({ $form.Close() })

# ---------------------------------------------------------------- 生成
$btnBuild.Add_Click({
    $sets = @()
    foreach ($c in $grpSets.Controls) {
        if ($c.Checked) { $sets += $c.Tag }
    }
    if ($sets -notcontains "BaseSet") { $sets = @("BaseSet") + $sets }

    $mode = ($grpMode.Controls | Where-Object { $_.Checked } | Select-Object -First 1).Tag
    if (-not $mode) {
        [System.Windows.Forms.MessageBox]::Show("请选择一种模式。", "自定义玩法", "OK", "Warning") | Out-Null
        return
    }

    $fac = $null
    if ($mode -ne "marathon") {
        $fac = ($grpFac.Controls | Where-Object { $_.Checked } | Select-Object -First 1).Tag
        if (-not $fac) {
            [System.Windows.Forms.MessageBox]::Show("请选择你的阵营。", "自定义玩法", "OK", "Warning") | Out-Null
            return
        }
    }

    $mods = @()
    foreach ($c in $grpMods.Controls) {
        if ($c.Checked) { $mods += $c.Tag }
    }

    $first = ($grpFirst.Controls | Where-Object { $_ -is [System.Windows.Forms.RadioButton] -and $_.Checked } |
              Select-Object -First 1).Tag

    if (-not (Test-UnityPy)) {
        [System.Windows.Forms.MessageBox]::Show(
            "这个 Python 环境没有 UnityPy：`n" + $script:Py,
            "自定义玩法", "OK", "Error") | Out-Null
        return
    }
    if (-not (Test-Path -LiteralPath $OutDir)) {
        New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    }

    $cmdArgs = @($Builder, "--mode", $mode, "--sets", ($sets -join ","), "--first", $first, "--install", "--log", $LogFile)
    if ($fac) { $cmdArgs += @("--faction", $fac) }
    if ($mods.Count -gt 0) { $cmdArgs += @("--mod", ($mods -join ",")) }

    $btnBuild.Enabled = $false
    $lblStatus.ForeColor = $ClrSub
    $lblStatus.Text = "生成中，请稍候…"
    $form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    $form.Refresh()

    try {
        & $script:Py @cmdArgs | Out-Null
        $code = $LASTEXITCODE
    } finally {
        $btnBuild.Enabled = $true
        $form.Cursor = [System.Windows.Forms.Cursors]::Default
    }

    if (Test-Path -LiteralPath $LogFile) {
        $txtLog.Text = (Get-Content -LiteralPath $LogFile -Encoding UTF8) -join "`r`n"
        $txtLog.SelectionStart = 0
        $txtLog.ScrollToCaret()
    }

    if ($code -eq 0) {
        $lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(15, 110, 86)
        $lblStatus.Text = "已安装。开游戏 → 单人模式 → 标准剧本。"
    } else {
        $lblStatus.ForeColor = $ClrDanger
        $lblStatus.Text = "生成失败，详见下方输出。"
    }
})

Update-FactionEnabled
[void]$form.ShowDialog()

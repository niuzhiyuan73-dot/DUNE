#requires -Version 5.1
# 玩法切换器：马拉松 / 分阵营 / 分阵营 Pro / 原版（纯数据改动）。
# 本文件须为 UTF-8 with BOM。

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$GameBundle = Join-Path $Root "game\DuneImperium_Data\StreamingAssets\aa\StandaloneWindows64\b56aa8ad35f9c2e1694994f384e51bbe.bundle"
$Orig = Join-Path $Root "backup\game\scenario_bundle.orig"
$OutDir = Join-Path $Root "mods\out"
$Name = "b56aa8ad35f9c2e1694994f384e51bbe.bundle"

$Presets = @(
    @{ Key = "marathon";         File = "preset_marathon_$Name";              Label = "马拉松     20 分胜利，1 水开局，牌组多一张控制香料" },
    @{ Key = "fremen";           File = "preset_faction_fremen_$Name";        Label = "弗雷曼     晶牙匕 / 毛拉枪 / 詹米 换掉两张沙丘（11 张）" },
    @{ Key = "bene";             File = "preset_faction_bene_$Name";          Label = "姐妹会     +1 随机密卡，暗中准备 / 奇异女士 / 基因操控" },
    @{ Key = "guild";            File = "preset_faction_guild_$Name";         Label = "宇航工会   +1 香料，私人航班 / 降落许可 / 太空旅行 / 卫星禁令" },
    @{ Key = "emperor";          File = "preset_faction_emperor_$Name";       Label = "皇帝       +1 太阳币，双面间谍 / 萨多卡士兵 / 帝国镇暴兵" },
    @{ Key = "pro-fremen";       File = "preset_faction_pro_fremen_$Name";    Label = "弗雷曼     12 分 + 巨沙虫夏胡鲁，冲突 5 张二级 + 5 张三级" },
    @{ Key = "pro-bene";         File = "preset_faction_pro_bene_$Name";      Label = "姐妹会     12 分 + 为了全人类，冲突 5 张二级 + 5 张三级" },
    @{ Key = "pro-guild";        File = "preset_faction_pro_guild_$Name";     Label = "宇航工会   12 分 + 公会协约/公会使者，冲突 5 张二级 + 5 张三级" },
    @{ Key = "pro-emperor";      File = "preset_faction_pro_emperor_$Name";   Label = "皇帝       12 分 + 全面进攻，冲突 5 张二级 + 5 张三级" },
    @{ Key = "extra-fremen";     File = "preset_faction_extra_fremen_$Name";  Label = "弗雷曼     14 分 + 沙虫骑士 / 为勇士唱赞歌，10 张三级冲突" },
    @{ Key = "extra-bene";       File = "preset_faction_extra_bene_$Name";    Label = "姐妹会     14 分 + 魁萨茨·哈德拉克，10 张三级冲突" },
    @{ Key = "extra-guild";      File = "preset_faction_extra_guild_$Name";   Label = "宇航工会   14 分 + 公会领航员，10 张三级冲突" },
    @{ Key = "extra-emperor";    File = "preset_faction_extra_emperor_$Name"; Label = "皇帝       14 分 + 奢靡 / 萨多卡协同作战，10 张三级冲突" },
    @{ Key = "ultra-fremen";     File = "preset_faction_ultra_fremen_$Name";  Force = "_holtz_ultra"; Label = "弗雷曼     +斯第尔格/领导力，捕风器" },
    @{ Key = "ultra-bene";       File = "preset_faction_ultra_bene_$Name";    Force = "_holtz_ultra"; Label = "姐妹会     +杰西卡夫人/圣母莫希阿姆，思录机" },
    @{ Key = "ultra-guild";      File = "preset_faction_ultra_guild_$Name";   Force = "_holtz_ultra"; Label = "宇航工会   +公会领航员/公会银行家，缩微胶片" },
    @{ Key = "ultra-emperor";    File = "preset_faction_ultra_emperor_$Name"; Force = "_holtz_ultra"; Label = "皇帝       +登基大典/占领+高等议会，掩护炮击" }
)

$script:Holtz = $false
$script:Last = $false
$script:Ultra = $false
$script:Council = $false

function Show-Menu {
    Write-Host ""
    Write-Host "============ 玩法切换 ============"
    Write-Host ""
    $s1 = if ($script:Holtz) { "开" } else { "关" }
    $s2 = if ($script:Last) { "开" } else { "关" }
    $s4 = if ($script:Ultra) { "开" } else { "关" }
    $s3 = if ($script:Council) { "开" } else { "关" }
    Write-Host "  叠加玩法：霍尔兹曼跃迁 $s1（m）｜ 最后的时代 $s2（n）｜ Ultra $s4（u）｜ 议会霸权 $s3（c）"
    Write-Host "   霍尔兹曼跃迁：起始带霍兹曼引擎 + 寻求盟友→伊克斯舰组"
    Write-Host "   最后的时代：  每人开局 3 个工人（自带剑术大师）"
    Write-Host "   最后的时代 Ultra：每人开局 4 个工人 + 1 块霍兹曼引擎（与「最后的时代」二选一）"
    Write-Host "   议会霸权：    每人多一块弹药制约科技 + 1 太阳币"
    Write-Host "   注：14-17 已固定开启跃迁与 Ultra，m/n/u 对它们无效，只有 c 可叠加"
    Write-Host ""
    Write-Host "  马拉松（20 分）"
    Write-Host "    1) $($Presets[0].Label)"
    Write-Host ""
    Write-Host "  分阵营（10 分，标准冲突牌堆）"
    Write-Host "    2) $($Presets[1].Label)"
    Write-Host "    3) $($Presets[2].Label)"
    Write-Host "    4) $($Presets[3].Label)"
    Write-Host "    5) $($Presets[4].Label)"
    Write-Host ""
    Write-Host "  分阵营 Pro（12 分，5 张二级 + 5 张三级冲突，无一级）"
    Write-Host "    6) $($Presets[5].Label)"
    Write-Host "    7) $($Presets[6].Label)"
    Write-Host "    8) $($Presets[7].Label)"
    Write-Host "    9) $($Presets[8].Label)"
    Write-Host ""
    Write-Host "  分阵营 Extra（14 分，厄拉奇恩 / 帝国盆地 / 迦太格 三张循环）"
    Write-Host "   10) $($Presets[9].Label)"
    Write-Host "   11) $($Presets[10].Label)"
    Write-Host "   12) $($Presets[11].Label)"
    Write-Host "   13) $($Presets[12].Label)"
    Write-Host ""
    Write-Host "  分阵营 Ultra（14 分，固定开跃迁 + 最后的时代 Ultra；兵营 5 支 + 钩杖 + 引爆）"
    Write-Host "   14) $($Presets[13].Label)"
    Write-Host "   15) $($Presets[14].Label)"
    Write-Host "   16) $($Presets[15].Label)"
    Write-Host "   17) $($Presets[16].Label)"
    Write-Host ""
    Write-Host "    m) 切换霍尔兹曼跃迁叠加"
    Write-Host "    n) 切换最后的时代叠加（3 工人）"
    Write-Host "    u) 切换最后的时代 Ultra 叠加（4 工人）"
    Write-Host "    c) 切换议会霸权叠加"
    Write-Host "    0) 还原原版（DLC 选择界面恢复）"
    Write-Host "    q) 退出"
    Write-Host ""
}

if (-not (Test-Path -LiteralPath $GameBundle)) {
    Write-Host "[错误] 找不到游戏剧本包："
    Write-Host "       $GameBundle"
    Read-Host "按回车退出"
    exit 1
}
if (-not (Test-Path -LiteralPath $Orig)) {
    Write-Host "[错误] 找不到原版备份："
    Write-Host "       $Orig"
    Read-Host "按回车退出"
    exit 1
}

while ($true) {
    Show-Menu
    $choice = Read-Host "请选择"

    if ($choice -eq "q") { exit 0 }

    if ($choice -eq "m") {
        $script:Holtz = -not $script:Holtz
        Write-Host "[切换] 霍尔兹曼跃迁叠加：$(if ($script:Holtz) { '开' } else { '关' })"
        Read-Host "按回车继续"
        continue
    }

    if ($choice -eq "n") {
        $script:Last = -not $script:Last
        if ($script:Last) { $script:Ultra = $false }
        Write-Host "[切换] 最后的时代叠加：$(if ($script:Last) { '开' } else { '关' })（Ultra 已自动关闭）"
        Read-Host "按回车继续"
        continue
    }

    if ($choice -eq "u") {
        $script:Ultra = -not $script:Ultra
        if ($script:Ultra) { $script:Last = $false }
        Write-Host "[切换] 最后的时代 Ultra 叠加：$(if ($script:Ultra) { '开' } else { '关' })（3 工人版已自动关闭）"
        Read-Host "按回车继续"
        continue
    }

    if ($choice -eq "c") {
        $script:Council = -not $script:Council
        Write-Host "[切换] 议会霸权叠加：$(if ($script:Council) { '开' } else { '关' })"
        Read-Host "按回车继续"
        continue
    }

    if ($choice -eq "0") {
        Copy-Item -LiteralPath $Orig -Destination $GameBundle -Force
        Write-Host "[完成] 已还原原版剧本包，DLC 选择界面会回来"
        Read-Host "按回车继续"
        continue
    }

    $idx = 0
    $ok = [int]::TryParse($choice, [ref]$idx)
    if (-not $ok -or $idx -lt 1 -or $idx -gt $Presets.Count) {
        Write-Host "[提示] 编号无效"
        continue
    }

    $p = $Presets[$idx - 1]
    $file = $p.File
    $extra = ""
    if ($p.Force) {
        # Ultra 模式自带跃迁 + 最后的时代 Ultra，只允许再叠议会霸权
        $extra = $p.Force
        if ($script:Council) { $extra += "_council" }
    } else {
        if ($script:Holtz) { $extra += "_holtz" }
        if ($script:Last) { $extra += "_last" }
        if ($script:Ultra) { $extra += "_ultra" }
        if ($script:Council) { $extra += "_council" }
    }
    if ($extra) { $file = $file.Replace("_$Name", "$($extra)_$Name") }
    $src = Join-Path $OutDir $file
    if (-not (Test-Path -LiteralPath $src)) {
        Write-Host "[错误] 找不到预设包：$file"
        Write-Host "       请先运行：python mods/tools/build_presets.py"
        Read-Host "按回车继续"
        continue
    }

    Copy-Item -LiteralPath $src -Destination $GameBundle -Force
    $tag = if ($p.Force) { " + 霍尔兹曼跃迁 + 最后的时代 Ultra" } else { "" }
    if (-not $p.Force -and $script:Holtz) { $tag += " + 霍尔兹曼跃迁" }
    if (-not $p.Force -and $script:Last) { $tag += " + 最后的时代" }
    if (-not $p.Force -and $script:Ultra) { $tag += " + 最后的时代 Ultra" }
    if ($script:Council) { $tag += " + 议会霸权" }
    Write-Host "[完成] 已安装：$($p.Key)$tag"
    Write-Host "       开游戏 -> 单人模式 -> 标准剧本"
    Read-Host "按回车继续"
}

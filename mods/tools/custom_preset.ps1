#requires -Version 5.1
# 自定义玩法生成器：任选内容集 + 模式（马拉松 / 分阵营 + 阵营），现场构建并安装。
# 本文件须为 UTF-8 with BOM。

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$Builder = Join-Path $Root "mods\tools\build_presets.py"

# UnityPy 装在 WorkBuddy 内置的 Python 3.13 里，系统 python 没有
$PyCandidates = @(
    (Join-Path $env:USERPROFILE ".workbuddy\binaries\python\versions\3.13.12\python.exe"),
    "python"
)
$Py = $null
foreach ($c in $PyCandidates) {
    if ($c -eq "python") { $Py = "python"; break }
    if (Test-Path -LiteralPath $c) { $Py = $c; break }
}

$SetMap = [ordered]@{
    "1" = "BaseSet"
    "2" = "RiseOfIx"
    "3" = "Immortality"
    "4" = "Uprising"
    "5" = "CHOAMModule"
}

$ModeMap = [ordered]@{
    "1" = "marathon"
    "2" = "faction"
    "3" = "faction_pro"
    "4" = "faction_extra"
}

$FactionMap = [ordered]@{
    "1" = "fremen"
    "2" = "bene"
    "3" = "guild"
    "4" = "emperor"
}

Write-Host ""
Write-Host "============ 自定义玩法 ============"
Write-Host ""
Write-Host "可选内容集（可多选）："
Write-Host "  1) 基础组合（必有）"
Write-Host "  2) 伊克斯的崛起"
Write-Host "  3) 不朽"
Write-Host "  4) 崛起"
Write-Host "  5) 宇联商会模块（建议搭配 2 一起选）"
Write-Host ""
$setInput = Read-Host "输入编号，逗号分隔，如 1,2（直接回车 = 全部开启）"

if ([string]::IsNullOrWhiteSpace($setInput)) {
    $chosen = @($SetMap.Values)
} else {
    $chosen = @()
    foreach ($tok in ($setInput -split "[,\s]+")) {
        if ($SetMap.Contains($tok)) {
            if ($chosen -notcontains $SetMap[$tok]) { $chosen += $SetMap[$tok] }
        }
    }
    if ($chosen.Count -eq 0) {
        Write-Host "[提示] 没有识别到有效编号，已回退为基础组合"
        $chosen = @("BaseSet")
    }
    if ($chosen -notcontains "BaseSet") {
        Write-Host "[提示] 基础组合是必需的，已自动补上"
        $chosen = @("BaseSet") + $chosen
    }
}

Write-Host ""
Write-Host "可选模式："
Write-Host "  1) 马拉松       20 分胜利，1 水开局，牌组多一张控制香料"
Write-Host "  2) 分阵营       正常 10 分胜利，标准冲突牌堆，按阵营增删"
Write-Host "  3) 分阵营 Pro   12 分胜利，冲突牌堆 5 张二级 + 5 张三级（无一级）"
Write-Host "  4) 分阵营 Extra 14 分胜利，10 张冲突全部三级，Pro 加牌之上再各加两张"
Write-Host ""
$modeInput = Read-Host "输入编号（1-4）"

if (-not $ModeMap.Contains($modeInput)) {
    Write-Host "[错误] 模式编号无效"
    Read-Host "按回车退出"
    exit 1
}

$mode = $ModeMap[$modeInput]
$fac = $null

if ($mode -ne "marathon") {
    Write-Host ""
    Write-Host "可选阵营（你选的那家给你，三个 AI 随机分到剩下三家）："
    if ($mode -eq "faction_extra") {
        Write-Host "  1) 弗雷曼   [12 张]  晶牙匕 毛拉枪 巨沙虫 +沙虫骑士 +为勇士唱赞歌；-2 沙丘-侦查-詹米"
        Write-Host "  2) 姐妹会   [11 张]  +1 随机密卡；+暗中准备 奇异女士 基因操控 为了全人类 +魁萨茨；-侦查-沙丘-匕首"
        Write-Host "  3) 宇航工会 [11 张]  +1 香料；+私人航班 降落许可 卫星禁令 公会协约 公会使者 +公会领航员；"
        Write-Host "                       -匕首-2 沙丘-侦查-寻求盟友-太空旅行"
        Write-Host "  4) 皇帝     [11 张]  +1 太阳币；+双面间谍 萨多卡士兵 帝国镇暴兵 全面进攻 +奢靡 +萨多卡协同作战；"
        Write-Host "                       -说服-匕首"
    } elseif ($mode -eq "faction_pro") {
        Write-Host "  1) 弗雷曼   [12 张]  +晶牙匕 +毛拉枪 +詹米 +巨沙虫夏胡鲁；-2 沙丘"
        Write-Host "  2) 姐妹会   [11 张]  +1 随机密卡；+暗中准备 +奇异女士 +基因操控 +为了全人类；-侦查-沙丘-匕首"
        Write-Host "  3) 宇航工会 [11 张]  +1 香料；+私人航班 +降落许可 +卫星禁令 +公会协约 +公会使者；"
        Write-Host "                       -匕首-沙丘-侦查-寻求盟友-太空旅行"
        Write-Host "  4) 皇帝     [11 张]  +1 太阳币；+双面间谍 +萨多卡士兵 +帝国镇暴兵 +全面进攻；-说服-匕首-侦查"
    } else {
        Write-Host "  1) 弗雷曼   [11 张]  +晶牙匕 +毛拉枪 +詹米；-2 沙丘"
        Write-Host "  2) 姐妹会   [10 张]  +1 随机密卡；+暗中准备 +奇异女士 +基因操控；-侦查-沙丘-匕首"
        Write-Host "  3) 宇航工会 [10 张]  +1 香料；+私人航班 +降落许可 +太空旅行 +卫星禁令；"
        Write-Host "                       -匕首-沙丘-侦查-寻求盟友"
        Write-Host "  4) 皇帝     [10 张]  +1 太阳币；+双面间谍 +萨多卡士兵 +帝国镇暴兵；-说服-匕首-侦查"
    }
    Write-Host ""
    $facInput = Read-Host "输入编号（1-4）"
    if (-not $FactionMap.Contains($facInput)) {
        Write-Host "[错误] 阵营编号无效"
        Read-Host "按回车退出"
        exit 1
    }
    $fac = $FactionMap[$facInput]
}

$modArgs = @()
Write-Host ""
Write-Host "可叠加玩法（可多选，叠加在任意模式之上）："
Write-Host "  1) 霍尔兹曼跃迁   每人起始带霍兹曼引擎；寻求盟友 → 伊克斯舰组"
Write-Host "  2) 最后的时代     每人开局 3 个工人（特工），自带剑术大师"
Write-Host "  3) 议会霸权       每人多一块弹药制约科技，基础资源 +1 太阳币"
$modInput = Read-Host "输入编号，逗号分隔，如 1,2（直接回车 = 不叠加）"

$chosenMods = @()
if (-not [string]::IsNullOrWhiteSpace($modInput)) {
    foreach ($tok in ($modInput -split "[,\s]+")) {
        if ($tok -eq "1" -and $chosenMods -notcontains "holtzman") { $chosenMods += "holtzman" }
        if ($tok -eq "2" -and $chosenMods -notcontains "lasttime") { $chosenMods += "lasttime" }
        if ($tok -eq "3" -and $chosenMods -notcontains "council") { $chosenMods += "council" }
    }
    if ($chosenMods.Count -eq 0) { Write-Host "[提示] 没有识别到有效编号，按不叠加处理" }
}
if ($chosenMods.Count -gt 0) { $modArgs = @("--mod", ($chosenMods -join ",")) }

$setsArg = ($chosen -join ",")

Write-Host ""
Write-Host "[执行] 内容集：$setsArg"
Write-Host "[执行] 模式：$mode"
if ($fac) { Write-Host "[执行] 阵营：$fac" }
Write-Host ""

$probe = & $Py -c "import UnityPy" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "[错误] 这个 Python 没有安装 UnityPy：$Py"
    Write-Host "       $probe"
    Read-Host "按回车退出"
    exit 1
}

$LogFile = Join-Path $Root "mods\out\last_build.log"
if ($fac) {
    & $Py $Builder --mode $mode --faction $fac --sets $setsArg @modArgs --install --log $LogFile
} else {
    & $Py $Builder --mode $mode --sets $setsArg @modArgs --install --log $LogFile
}
if (Test-Path -LiteralPath $LogFile) {
    Get-Content -LiteralPath $LogFile -Encoding UTF8 | ForEach-Object { Write-Host $_ }
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "[错误] 构建失败"
    Read-Host "按回车退出"
    exit 1
}

Write-Host "[完成] 已安装。开游戏 -> 单人模式 -> 标准剧本。"
Write-Host "       想回到原版：运行 切换玩法.bat，选 0。"
Read-Host "按回车关闭"

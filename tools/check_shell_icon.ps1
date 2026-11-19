param([Parameter(Mandatory=$true)][string]$Exe)

# 用资源管理器同一条 API（ExtractAssociatedIcon）读出 exe 的文件图标，
# 验证「单文件发布后资源管理器/桌面里有没有图标」。
#
# 注意：Windows PowerShell 5.1 不一定能加载 System.Drawing，要用 pwsh 才能取到类型。
$path = (Resolve-Path -LiteralPath $Exe).Path
$loaded = $false
foreach ($asm in @('System.Drawing.Common', 'System.Drawing')) {
    try { Add-Type -AssemblyName $asm -ErrorAction Stop; $loaded = $true; break } catch { }
}
if (-not $loaded) {
    Write-Output "CANNOT-LOAD-System.Drawing (use pwsh)"
    exit 2
}

$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($path)
if ($null -eq $icon) {
    Write-Output "NO ICON RESOURCE"
    exit 1
}
Write-Output ("icon: " + $icon.Width + "x" + $icon.Height)
Write-Output ("size: " + (Get-Item -LiteralPath $path).Length)
exit 0

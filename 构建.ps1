$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
& $compilerPath /nologo /target:winexe "/out:$PSScriptRoot\程序联动助手.exe" "/win32icon:$PSScriptRoot\程序联动助手.ico" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll "$PSScriptRoot\程序源码.cs"
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Write-Output '程序联动助手已编译。'

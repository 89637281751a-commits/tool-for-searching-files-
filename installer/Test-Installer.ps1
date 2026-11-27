param(
    [Parameter(Mandatory)] [string] $CurrentMsi,
    [Parameter(Mandatory)] [version] $CurrentVersion,
    [Parameter(Mandatory)] [string] $OlderMsi
)

$ErrorActionPreference = 'Stop'
$logFolder = (New-Item 'installer-logs' -ItemType Directory -Force).FullName
$installFolder = Join-Path $env:LOCALAPPDATA 'QuickFind'
$exe = Join-Path $installFolder 'QuickFind.exe'
$index = Join-Path $installFolder 'index.txt'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'QuickFind.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'

function Invoke-Installer($operation, $package, $logName, $expectedExitCode = 0) {
    $log = Join-Path $logFolder "$logName.log"
    $quote = [char]34
    $arguments = "$operation $quote$package$quote /qn /norestart /l*v $quote$log$quote"
    $process = Start-Process msiexec.exe -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne $expectedExitCode) {
        throw "Unexpected MSI exit code $($process.ExitCode) in $logName. See $log."
    }
}

function Get-QuickFindRegistrations {
    Get-ChildItem $uninstallKey -ErrorAction SilentlyContinue | ForEach-Object {
        Get-ItemProperty $_.PSPath
    } | Where-Object { $_.DisplayName -eq 'QuickFind' }
}

function Assert-Installed([version] $expectedVersion) {
    $exeVersion = [version](Get-Item $exe).VersionInfo.FileVersion
    if ($exeVersion -ne $expectedVersion) {
        throw "Expected installed file version $expectedVersion, found $exeVersion."
    }
    if (-not (Test-Path $shortcut -PathType Leaf)) { throw 'Missing Start menu shortcut.' }
    $shell = New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($shortcut).TargetPath -ne $exe) { throw 'Incorrect shortcut target.' }
    $product = @(Get-QuickFindRegistrations)
    if ($product.Count -ne 1 -or [version]$product[0].DisplayVersion -ne [version]($expectedVersion.ToString(3))) {
        throw 'Expected one QuickFind entry in Installed apps at the current version.'
    }
    if (Get-Process QuickFind -ErrorAction SilentlyContinue) { throw 'Setup started QuickFind.' }
    if ((Get-Content $index -Raw) -ne 'preserve this index') { throw 'Setup changed the index.' }
}

if ((Test-Path $installFolder) -or (Test-Path $shortcut) -or @(Get-QuickFindRegistrations).Count -ne 0 -or
    $null -ne (Get-ItemProperty $runKey -Name QuickFind -ErrorAction SilentlyContinue).QuickFind) {
    throw 'The installer test requires a Windows account without an existing QuickFind installation or data.'
}

# The old installer compiled a versionless binary locally. Reproduce that layout.
New-Item $installFolder -ItemType Directory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$compilerArguments = @(
    '/nologo', '/target:winexe', '/optimize+', '/codepage:65001', "/out:$exe", '/win32icon:QuickFind.ico',
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:Microsoft.CSharp.dll', 'QuickFind.cs'
)
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Cannot compile the legacy fixture.' }
(Get-Item $exe).LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(1)
Set-Content $index 'preserve this index' -NoNewline

Invoke-Installer /i $OlderMsi 'legacy-migration'
Assert-Installed '0.0.1.0'
if ($null -ne (Get-ItemProperty $runKey -Name QuickFind -ErrorAction SilentlyContinue).QuickFind) {
    throw 'Installation enabled QuickFind at sign-in.'
}

# A sign-in preference created by the app must survive a major upgrade.
$startup = '"{0}" /background' -f $exe
if (-not (Test-Path $runKey)) { New-Item $runKey -Force | Out-Null }
New-ItemProperty $runKey -Name QuickFind -PropertyType String -Value $startup -Force | Out-Null
Invoke-Installer /i $CurrentMsi 'upgrade'
Assert-Installed $CurrentVersion
if ((Get-ItemPropertyValue $runKey -Name QuickFind) -ne $startup) {
    throw 'Upgrade changed the existing sign-in preference.'
}

Invoke-Installer /i $OlderMsi 'downgrade-blocked' 1603
Assert-Installed $CurrentVersion
Remove-ItemProperty $runKey -Name QuickFind
Invoke-Installer /x $CurrentMsi 'uninstall'
if ((Test-Path $exe) -or (Test-Path $shortcut) -or @(Get-QuickFindRegistrations).Count -ne 0) {
    throw 'Uninstall left the program, shortcut or Installed apps entry behind.'
}
if ((Get-Content $index -Raw) -ne 'preserve this index') { throw 'Uninstall deleted the index.' }

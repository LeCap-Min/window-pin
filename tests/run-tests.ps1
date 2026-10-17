$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appPath = Join-Path $projectRoot '窗口置顶.exe'
if (-not (Test-Path -LiteralPath $appPath)) { throw 'Build the application with build.ps1 first.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$resultsDir = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
$testExe = Join-Path $resultsDir 'WindowPin.NativeTests.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 /warn:4 "/out:$testExe" "/reference:$appPath" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'NativeTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test harness compilation failed.' }
Copy-Item -LiteralPath $appPath -Destination (Join-Path $resultsDir '窗口置顶.exe') -Force
$appHash = (Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash
Set-Content -LiteralPath (Join-Path $resultsDir 'tested-app-sha256.txt') -Value $appHash -Encoding UTF8
& $testExe $resultsDir
if ($LASTEXITCODE -ne 0) { throw 'Native Windows tests failed. See tests/results/native-test-results.txt.' }
Get-Content -LiteralPath (Join-Path $resultsDir 'native-test-results.txt') | Select-Object -Last 1

$uiExe = Join-Path $resultsDir 'WindowPin.UiTests.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 /warn:4 "/out:$uiExe" "/reference:$appPath" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'UiTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI test harness compilation failed.' }
& $uiExe $resultsDir
if ($LASTEXITCODE -ne 0) { throw 'UI tests failed. See tests/results/ui-tests.txt.' }

# Launch the delivered executable itself, not just its classes from a harness.
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WindowPinStartupTest {
    private delegate bool Callback(IntPtr h,IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback c,IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h,StringBuilder t,int n);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
    public static IntPtr Find(int processId,string title) {
        IntPtr result=IntPtr.Zero;
        EnumWindows(delegate(IntPtr h,IntPtr p) {
            uint pid; GetWindowThreadProcessId(h,out pid);
            if(pid!=(uint)processId) return true;
            var text=new StringBuilder(256); GetWindowText(h,text,text.Capacity);
            if(text.ToString()!=title) return true;
            result=h; return false;
        },IntPtr.Zero);
        return result;
    }
}
'@
$appProcess = Start-Process -FilePath $appPath -PassThru -WindowStyle Hidden
$mainHandle = [IntPtr]::Zero
try {
    if (-not $appProcess.WaitForInputIdle(10000)) { throw 'Application did not become ready.' }
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        $mainHandle = [WindowPinStartupTest]::Find($appProcess.Id,'窗口置顶')
        if ($mainHandle -ne [IntPtr]::Zero) { break }
        if ($appProcess.HasExited) { throw 'Application exited during startup.' }
        Start-Sleep -Milliseconds 100
    }
    if ($mainHandle -eq [IntPtr]::Zero) { throw 'Application did not create its main window.' }
    if (-not [WindowPinStartupTest]::PostMessage($mainHandle,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)) { throw 'Application did not accept normal close.' }
    if (-not $appProcess.WaitForExit(10000)) { throw 'Application did not exit normally.' }
    if ($appProcess.ExitCode -ne 0) { throw 'Application returned a nonzero exit code.' }
    Set-Content -LiteralPath (Join-Path $resultsDir 'executable-startup.txt') -Encoding UTF8 -Value @(
        'PASS: Delivered executable created its named main window (hidden startup)',
        'PASS: Delivered executable accepted normal close and exited with code 0',
        "SHA256: $appHash"
    )
} finally {
    if (-not $appProcess.HasExited) {
        if ($mainHandle -ne [IntPtr]::Zero) { $null = [WindowPinStartupTest]::PostMessage($mainHandle,0x0010,[IntPtr]::Zero,[IntPtr]::Zero) }
        if (-not $appProcess.WaitForExit(3000)) { $appProcess.Kill() }
    }
    $appProcess.Dispose()
}
Write-Output 'Native tests, UI tests, and executable startup passed.'

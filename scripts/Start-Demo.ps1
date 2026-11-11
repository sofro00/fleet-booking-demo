param([switch]$NoClient)
$ErrorActionPreference = 'Stop'
$demoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $demoRoot
$localDir = Join-Path $demoRoot '.local'
New-Item -ItemType Directory -Path $localDir -Force | Out-Null
$passwordFile = Join-Path $localDir 'demo-password.txt'
if (-not (Test-Path -LiteralPath $passwordFile)) {
    $randomBytes = New-Object byte[] 24
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($randomBytes) } finally { $generator.Dispose() }
    $demoPassword = 'Demo!' + [Convert]::ToBase64String($randomBytes) + 'a1'
    [IO.File]::WriteAllText($passwordFile, $demoPassword)
}
$demoPassword = [IO.File]::ReadAllText($passwordFile).Trim()
$env:FLEET_Demo__Password = $demoPassword
[IO.File]::WriteAllText((Join-Path $localDir 'demo-access.txt'), "LOCAL DEMO ONLY`r`nAdministrator: admin@example.com`r`nEmployees: ana@example.com, luka@example.com`r`nPassword: $demoPassword`r`nAPI: https://localhost:58443/`r`nThese credentials must never be used for business accounts.`r`n")

& dotnet dev-certs https --check --quiet
if ($LASTEXITCODE -ne 0) { throw 'A local HTTPS certificate is required. Run: dotnet dev-certs https --trust' }
# Serialize startup, but allow a later launch to restart an older demo.
try {
    $startupLock = [IO.File]::Open((Join-Path $localDir 'startup.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
} catch { throw 'Another demo startup is in progress. Wait for it to finish before trying again.' }
$apiDll = Join-Path $demoRoot 'src\FleetBooking.Api\bin\Debug\net10.0\FleetBooking.Api.dll'
$clientDll = Join-Path $demoRoot 'src\FleetBooking.Client\bin\Debug\net10.0-windows\FleetBooking.Client.dll'
$apiProcess = $null
try {
    # Match exact executable/DLL paths inside this checkout, never process names alone.
    $targets = @(
        @{ Dll = $clientDll; Client = $true },
        @{ Dll = $apiDll; Client = $false }
    )
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='FleetBooking.Client.exe' OR Name='FleetBooking.Api.exe'")
    foreach ($target in $targets) {
        $exe = [IO.Path]::ChangeExtension($target.Dll, '.exe')
        $dllArgument = '(?i)(?:^|\s)"' + [regex]::Escape($target.Dll) + '"(?:\s|$)'
        $unquotedArgument = '(?i)(?:^|\s)' + [regex]::Escape($target.Dll) + '(?:\s|$)'
        foreach ($candidate in $processes) {
            $isHost = [IO.Path]::GetFileName($candidate.ExecutablePath) -ieq 'dotnet.exe'
            if ($candidate.ExecutablePath -ine $exe -and -not ($isHost -and
                ($candidate.CommandLine -match $dllArgument -or $candidate.CommandLine -match $unquotedArgument))) { continue }
            $running = Get-Process -Id $candidate.ProcessId -ErrorAction SilentlyContinue
            if ($null -eq $running) { continue }
            Write-Host 'Closing the previous instance of this demo...'
            if ($target.Client) {
                if (-not $running.CloseMainWindow() -or -not $running.WaitForExit(10000)) {
                    throw 'Close the previous Fleet Booking Demo window, then run the launcher again.'
                }
            } else {
                Stop-Process -Id $running.Id -ErrorAction Stop
                $running.WaitForExit()
            }
        }
    }
    # Check before building; never stop an unrelated listener.
    $portCheck = New-Object System.Net.Sockets.TcpClient
    try {
        $portCheck.Connect('localhost', 58443)
        throw 'Port 58443 is occupied by another application. Free this port before starting the demo.'
    } catch [System.Net.Sockets.SocketException] {
    } finally { $portCheck.Dispose() }
    foreach ($project in @('src\FleetBooking.Api\FleetBooking.Api.csproj', 'src\FleetBooking.Client\FleetBooking.Client.csproj')) {
        & dotnet build $project --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Build failed for $project. See the compiler output above." }
    }
    $apiProcess = Start-Process -FilePath 'dotnet' -ArgumentList @(('"' + $apiDll + '"')) -WorkingDirectory $demoRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $localDir 'api-output.log') -RedirectStandardError (Join-Path $localDir 'api-error.log')
    $ready = $false
    for ($attempt = 0; $attempt -lt 90; $attempt++) {
        if ($apiProcess.HasExited) { throw 'The demo API stopped. Inspect .local/api-error.log and .local/api-output.log locally.' }
        try {
            $health = Invoke-RestMethod -Uri 'https://localhost:58443/api/health' -TimeoutSec 2
            if ($health.version -eq '1.0.0-demo') { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw 'The demo API did not become ready. Check local HTTPS trust and SQL Express availability.' }
    Write-Host 'Fleet Booking Demo is ready at https://localhost:58443/'
    Write-Host 'Local demo credentials: .local/demo-access.txt'
    $startupLock.Dispose()
    $startupLock = $null
    if ($NoClient) {
        Write-Host 'Press Ctrl+C to stop the demo API.'
        $apiProcess.WaitForExit()
    } else {
        & dotnet $clientDll
    }
} finally {
    if ($null -ne $startupLock) { $startupLock.Dispose() }
    if ($null -ne $apiProcess -and -not $apiProcess.HasExited) { Stop-Process -Id $apiProcess.Id }
    Remove-Item Env:FLEET_Demo__Password -ErrorAction SilentlyContinue
}

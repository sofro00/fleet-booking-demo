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
& dotnet build FleetBooking.sln --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$apiDll = Join-Path $demoRoot 'src\FleetBooking.Api\bin\Debug\net10.0\FleetBooking.Api.dll'
$apiProcess = $null
try {
    # Do not connect to or stop an unrelated process that occupies the demo port.
    $portCheck = New-Object System.Net.Sockets.TcpClient
    try {
        $portCheck.Connect('localhost', 58443)
        throw 'Port 58443 is already in use. Stop the earlier demo before starting another copy.'
    } catch [System.Net.Sockets.SocketException] {
        # No listener: it is safe to start our own process.
    } finally { $portCheck.Dispose() }
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
    if ($NoClient) {
        Write-Host 'Press Ctrl+C to stop the demo API.'
        $apiProcess.WaitForExit()
    } else {
        & dotnet (Join-Path $demoRoot 'src\FleetBooking.Client\bin\Debug\net10.0-windows\FleetBooking.Client.dll')
    }
} finally {
    if ($null -ne $apiProcess -and -not $apiProcess.HasExited) { Stop-Process -Id $apiProcess.Id }
    Remove-Item Env:FLEET_Demo__Password -ErrorAction SilentlyContinue
}

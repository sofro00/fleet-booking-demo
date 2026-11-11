param([switch]$Sql)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
$previousSql = $env:FLEET_SQL_TEST_CONNECTION
try {
    Remove-Item Env:FLEET_SQL_TEST_CONNECTION -ErrorAction SilentlyContinue
    if ($Sql) {
        $env:FLEET_SQL_TEST_CONNECTION = 'Server=.\SQLEXPRESS;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
    }
    & dotnet test tests/FleetBooking.Tests/FleetBooking.Tests.csproj --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally {
    if ($null -eq $previousSql) { Remove-Item Env:FLEET_SQL_TEST_CONNECTION -ErrorAction SilentlyContinue }
    else { $env:FLEET_SQL_TEST_CONNECTION = $previousSql }
}

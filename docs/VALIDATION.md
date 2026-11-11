# Local verification

Verified on Windows on 11 November 2026 with .NET SDK 10.0.301 and local SQL Server Express.

| Check | Result |
| --- | --- |
| Solution build | Passed, no warnings or errors |
| API and business-rule tests with in-memory databases | 65 passed |
| Same suite with real SQL Server integration enabled | 65 passed |
| WPF layout and control checks | Passed: vehicle states, reservation forms, settings, user/admin tables and issue reporting |
| Local HTTPS API smoke | Passed: health, demo login, synthetic users/vehicles, role restrictions, booking, overlap rejection and retained cancellation history |
| Publication scanner self-tests | Passed: secret/email patterns, changed file, unexpected file and unsafe manifest path |
| Source text and configuration check | Passed for manifest-listed public files |
| Gitleaks 8.30.1 source scan | No findings in the source-only candidate snapshot |

SQL integration tests use uniquely named disposable test databases. The runtime smoke uses only `FleetBookingDemo`, creates one additional fictional booking and cancels it afterwards. No operational database was queried or copied. The public ZIP includes source files and the synthetic WPF screenshot; it excludes all local test output and database contents.

The source hashes of the original files selected for copying were also checked and remained unchanged. Checks do not establish publication rights, and automatic secret scanning cannot guarantee the absence of every kind of personal or confidential data. Review the manifest and privacy notes before publishing.

This is a local verification record. GitHub Actions execution and public hosting have not been performed.

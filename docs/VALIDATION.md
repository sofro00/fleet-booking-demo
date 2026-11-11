# Local verification

Verified on Windows on 11 November 2026 with .NET SDK 10.0.301 and local SQL Server Express.

| Check | Result |
| --- | --- |
| Launcher restart regression | Passed: reproduced MSB3027 from an older running API; launcher now closes only processes from this checkout before building, starts HTTPS successfully and opens the desktop login |
| Solution build | Passed, no warnings or errors |
| API and business-rule tests with in-memory databases | 67 passed, including fresh image seeding and missing-image backfill without overwriting custom images |
| Same suite with real SQL Server integration enabled | 67 passed |
| WPF layout and control checks | Passed: all five vehicle cutouts decoded with genuine transparent pixels and rendered at a larger size, vehicle states, reservation forms, settings, user/admin tables and issue reporting |
| Local HTTPS API smoke | Passed: health, demo login, synthetic users/vehicles, all five image responses matched their bundled assets, role restrictions, booking, overlap rejection and retained cancellation history |
| Publication scanner self-tests | Passed: secret/email patterns, changed file, unexpected file and unsafe manifest path |
| Source text and configuration check | Passed for manifest-listed public files |
| Gitleaks 8.30.1 source scan | No findings in the source-only candidate snapshot |

SQL integration tests use uniquely named disposable test databases. The runtime smoke uses only `FleetBookingDemo`, creates one additional fictional booking and cancels it afterwards. No operational database was queried or copied. The public ZIP includes source files and the synthetic WPF screenshot; it excludes all local test output and database contents.

The vehicle-image update was also checked against the previously initialized local demo database. Missing images were added without recreating accounts or reservations. All five image assets are embedded resources, so the API and screenshot checks do not depend on an external image URL.

The transparent-image update replaced all five exact previous bundled images in the running local demo database. Account and reservation counts were unchanged, and the existing HTTPS API returned files matching all five new cutouts. Custom images are excluded from this upgrade by content-hash checks. The desktop now trims transparent outer margins and fits the full vehicle in an image area up to 128 by 66 pixels, with padding reduced to two pixels in the image cells.

The source hashes of the original files selected for copying were also checked and remained unchanged. Checks do not establish publication rights, and automatic secret scanning cannot guarantee the absence of every kind of personal or confidential data. Review the manifest and privacy notes before publishing.

This is a local verification record. GitHub Actions execution and public hosting have not been performed.

# Privacy and publication notes

This edition was prepared as a separate source tree with new project names and configuration. It does not include the original Git history, production settings, company artwork, operational documentation, shortcuts, databases, backups, browser sessions or existing build artifacts.

## Synthetic data

- All seeded accounts use the reserved `example.com` domain. Names are explicitly marked as demo identities.
- Vehicle identifiers use `DEMO-` followed by three digits. There are no real registration plates in the demo seed.
- The five bundled vehicle illustrations are AI-generated studio images with blank plates, not photographs from a company fleet. They were visually reviewed before inclusion; provenance and prompts are recorded in `docs/IMAGES.md`.
- Seeded accounts have no phone numbers. Seeded bookings contain no contact phone numbers.
- Vehicles, bookings, destinations, maintenance, issue reports and audit entries are newly created examples; they are not transformed exports from a live database.
- No initial password, JWT signing key or password hash is stored in the public configuration. The launcher generates its own local password; the API generates its signing key in memory on startup.
- Automated tests contain deliberately fictional test credentials and test-only identifiers. They do not grant access to any external system.

## Separation from business systems

The API accepts only a local SQL Server connection to the exact database name `FleetBookingDemo`, using Windows authentication. Startup refuses an existing database unless it carries the demo marker created by this application. It never opens the business database to inspect, copy or modify its records.

The API binds only to localhost on port 58443. The desktop client accepts only `https://localhost:58443/` and does not follow HTTP redirects. Production settings, user secrets, generic environment settings and command-line configuration are not loaded as application configuration. Only the demo appsettings file and explicitly prefixed `FLEET_` variables are used. Public listeners and scheduled backups are rejected.

Application files, assembly names, process activation signals and local session storage are separate from the operational application. The launcher stops only the API process it starts. It does not install a service, change the firewall, start SQL services or reuse a running API.

## Publication checks

`PUBLIC_FILES.txt` lists the reviewed public files and SHA-256 checksums. `verify_public.py` checks that manifest, rejects unexpected files outside known local-output folders, scans text for non-example email domains, private network addresses, original branding, private key blocks and common token formats, and checks the committed JSON configuration. Findings contain paths and line numbers rather than matched values.

The packaging script calls the check again, copies only manifest entries into a new ZIP, then verifies every ZIP entry against the manifest. Local credentials, logs, screenshots from unrelated applications and database backups must never be added to the manifest. A manifest change requires another review; checksums do not prove that content is non-sensitive.

Automatic scanning is one layer. It cannot reliably identify every person's name, every registration plate, arbitrary passwords or text embedded in images. Review screenshots and documentation before adding them. Do not enter real business data into this demo. The restriction to example emails and demo vehicle identifiers reduces accidental reuse, but free-text fields are still user input.

Before publishing code developed for an employer or client, establish that you are permitted to publish that code. Technical sanitization does not establish ownership or publication rights. No GitHub upload is performed by the local preparation scripts.

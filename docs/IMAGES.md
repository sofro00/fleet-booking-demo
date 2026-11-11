# Demo vehicle illustrations

These five images were generated and then edited with the built-in image generation tool for this demo. They are transparent PNG vehicle cutouts, not photographs of actual company vehicles or exact manufacturer specifications. No company images or reference photographs were supplied. All visible license plates are blank.

| Demo vehicle | Bundled asset |
| --- | --- |
| Toyota Corolla / DEMO-001 | `src/FleetBooking.Api/Assets/Vehicles/toyota-corolla.png` |
| Škoda Octavia / DEMO-002 | `src/FleetBooking.Api/Assets/Vehicles/skoda-octavia.png` |
| Renault Clio / DEMO-003 | `src/FleetBooking.Api/Assets/Vehicles/renault-clio.png` |
| Ford Transit / DEMO-004 | `src/FleetBooking.Api/Assets/Vehicles/ford-transit.png` |
| Volkswagen Golf / DEMO-005 | `src/FleetBooking.Api/Assets/Vehicles/volkswagen-golf.png` |

Images are embedded in the API assembly and stored in the demo database with their PNG content type and update timestamp. The desktop uses the existing authenticated image endpoint, trims transparent outer margins at display time, and fits the complete vehicle inside a larger image area. Transparency lets the table row background show through, including selected rows.

Startup fills missing images on the matching original demo model and identifier. It also recognizes the previous bundled images by their SHA-256 content hashes and replaces those exact images with the new transparent versions. Custom images are preserved. The updated image timestamp invalidates the desktop image cache.

## Generation prompts

Each image used this common prompt, with the corresponding subject from the table below substituted for `{subject}`:

> Use case: product-mockup. Asset type: single vehicle illustration for a fictional Fleet Booking Demo application. Generate a clean photorealistic studio catalog image of {subject}. One complete vehicle only, front three-quarter view facing left, centered, fully in frame with generous margins around the car, on a light neutral grey studio floor with a soft natural contact shadow. Consistent soft studio lighting, realistic proportions, crisp details, horizontal composition suitable for a small 160 by 90 thumbnail. No people, buildings, signage, watermarks, written text, readable branding or badges. Both license plate areas must be plain blank rectangles with no letters or numbers. This is an illustrative synthetic demo asset, not an actual company vehicle.

| Asset | Subject |
| --- | --- |
| `toyota-corolla.png` | a pearl white Toyota Corolla compact sedan, modern 2020s body shape |
| `skoda-octavia.png` | a metallic graphite grey Skoda Octavia liftback sedan, modern 2020s body shape |
| `renault-clio.png` | a metallic blue Renault Clio small five-door hatchback, modern 2020s body shape |
| `ford-transit.png` | a white Ford Transit medium high-roof panel cargo van, modern 2020s body shape |
| `volkswagen-golf.png` | a metallic red Volkswagen Golf five-door compact hatchback, modern 2020s body shape |

Some generated images retain recognizable vehicle badges despite the prompt. Brand names and badges identify the depicted models; no affiliation with a manufacturer is claimed.

## Transparent-background edit prompt

Each original vehicle image was supplied as the single edit target to the built-in tool, with transparent background enabled and this prompt:

> Use case: background-extraction. Edit target: the supplied vehicle image for a small Fleet Booking table thumbnail. Remove the grey studio background, floor and all cast/contact shadows completely, replacing them with genuine transparent alpha, including all background visible underneath the vehicle and through gaps. Preserve the exact vehicle model, paint color, body shape, camera angle, wheels, windows, badges and blank license plate. Keep natural dark details that belong to the car itself. Reframe as a tight horizontal cutout around the entire vehicle with only about 2 percent transparent margin on each side, making the vehicle occupy almost the full canvas. Keep all bumpers, wheels, roof and mirrors fully visible. No checkerboard baked into the image, no white or grey rectangle, no artificial outline, no new text, no extra objects. Output a real RGBA transparent PNG.

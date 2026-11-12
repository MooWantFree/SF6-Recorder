# Official rank artwork

The PNG files in this directory were downloaded from CAPCOM's public Buckler assets on 2026-11-12. `manifest.json` records each exact source URL and SHA-256 hash. The images are embedded as WPF resources by `Sf6Prototype.csproj`; displaying a rank does not perform a network request.

The UI now displays colored rank names instead of the images. The artwork remains as a local reference for `Rank*` brushes in `Themes/Tokens.xaml`; text colors preserve the badge hues but are darkened for legibility on light backgrounds.

The website's rank component uses `/6/buckler/assets/images/material/rank/rank{ID}_s.png`. For league rank 36 it selects a nonzero `master_league` badge when present. The downloaded artwork confirms 37 = Legend, 40 = High Master, 41 = Grand Master and 42 = Ultimate Master. League IDs 1–35 include the five individual stars for Rookie through Diamond, and 36 is Master. IDs 38 and 43 returned 404; the website explicitly omits a badge for the unranked sentinel 39.

To restore missing source files, run `pwsh -File scripts/Fetch-RankAssets.ps1`. This maintenance script uses the saved proxy setting and unauthenticated serial HTTP requests. It does not read cookies, update user data, build the application or run during normal application use.

Artwork belongs to CAPCOM. Keep this provenance with the bundled files.

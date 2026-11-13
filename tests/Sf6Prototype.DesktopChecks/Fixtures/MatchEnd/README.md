These 640 × 64 PNGs contain only the normalized top strip consumed by the detector.

- `result-zh-hans.png`: an actual Simplified Chinese result captured on 2026-11-14 while diagnosing three missed matches. Its title is “结果”, not “RESULT”. No account names are present in this strip.
- `result-ja.png`: Japanese result UI with the English-styled “RESULT” title. Original screenshot: https://note.com/saburo15/n/n5f64ebae38a7 (top strip; retain the original 16:9 scale because the published image is vertically cropped).
- `result-en.png`: English result UI. Original screenshot: https://esports.gg/news/street-fighter-6/street-fighter-6-communication-error-sf6/ (top strip).
- `battle.png`: standard Ryu/Chun-Li battle HUD, normalized from the existing visual investigation fixture; negative example for result matching.

Keep real image checks in the normal desktop check run. Synthetic observations alone cannot reveal a missing localized title template.

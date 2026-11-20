# Nova's character art

Put the art here and it ships next to `Beacon.exe` (in `Avatar\`). Either:

- **`sheet.png`**: the 6 x 4 design sheet, poses in this order (left to right, top to bottom):
  Idle, Listening, Thinking, Working, Alert, Happy · Searching, Loading, Confirmed, Error, Tired, Shy ·
  Idea, No Signal, Calculating, Sad, Muted, Charging · Celebrating, Heart, Doubtful, Sleepy, Greeting, Encrypting.
  Each cell's bottom 14% (the name label) is cut off.
- **one PNG per pose**, named after it in lower case with dashes: `idle.png`, `listening.png`,
  `no-signal.png`... Transparent backgrounds look best.

Beacon turns the art's blue and cyan glow into the current style's accent colour (Tactical amber,
HUD cyan, Cyberpunk pink, Retro CRT green, Minimal mono grey). Status colours (red alert, green
confirmed, pink heart) stay as drawn.

A user can override the shipped art by putting their own in `%APPDATA%\Beacon\avatar\`.

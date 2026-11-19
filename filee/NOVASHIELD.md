# Filee in NovaShield HUD

This folder is a fork of [Filee](https://github.com/KnifeLemon/Filee) by Myeonggu Kim (KnifeLemon) and the Filee
contributors, MIT licensed (see [LICENSE](LICENSE)), taken at upstream commit
`cf96cd5caa34e5122be8621639dd4ff529344f81`. The app's sources, build scripts and notices are copied as they were;
upstream's tests, docs, installers and website are not (get them from upstream).

## What the fork changes

Everything else is upstream's. The changes are marked `NovaShield HUD` in the code:

| File | Change |
|---|---|
| `src/Filee.Core/Settings/CustomTheme.cs` | `theme.json` takes an optional `suite` section (`name`, `accent`, `mode`, `radius`, `font`): the look of the suite Filee runs in. |
| `src/Filee.App/App.axaml.cs` | The `suite` look is laid over Filee's own Theme settings (settings.json is not changed), and `theme.json` is watched, so a new look applies in place. |
| `src/Filee.App/Services/ThemeService.cs` | `suite.font` goes in front of Filee's font chain. |

Beacon writes the file from its Appearance settings (`beacon/Core/Suite.Filee.cs`):

```json
{
  "suite": { "name": "tactical", "accent": "#F0A030", "mode": "dark", "radius": 2, "font": "Bahnschrift" },
  "dark":  { "page": "#17181A", "surface": "#2C2E31", "surfaceAlt": "#3E4145", "border": "#55585E",
             "text": "#E6E6E6", "textSecondary": "#A4A7AC" }
}
```

A `theme.json` without a `suite` section is the user's own, and Beacon leaves it alone.

## Pulling upstream changes

```bash
git clone https://github.com/KnifeLemon/Filee /tmp/filee-upstream
```

Copy `src/`, `build/`, the props files, `global.json`, `LICENSE`, `THIRD-PARTY-NOTICES.md` and `README.md` over this
folder, re-apply the three changes above, and update the commit hash here.

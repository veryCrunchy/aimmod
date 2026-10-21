# PP target scan speed

A scan measures each candidate map's patterns and calculates its PP for about ten score shapes with the official osu! calculator.

## Workers

Calculations run in separate worker processes: one per two logical processors, up to six, and one per 2 GB of memory. Workers stay alive between the scan's 200-map batches, keeping their decoded maps and difficulty attributes, and exit after 90 idle seconds. Each worker decodes a map once for all of its score shapes.

## Caches

Map patterns and beatmap files are kept in a pattern cache folder, which keeps a running size and only lists its files when it goes over its limits. An identical cached map is never rewritten. PP results are cached for 49,152 calculations, enough for a full 2,000-map scan, so a rescan whose score shapes did not change reuses them.

## Microsoft Defender

On Windows, Microsoft Defender checks every beatmap file a scan writes, and those checks can take longer than the calculation. While a scan runs, PP targets offers once to exclude AimMod's two scan folders: the pattern cache and the scan's scratch folder. **Speed up scans** explains exactly which folders, then Windows asks for permission and one visible PowerShell window runs `Add-MpPreference -ExclusionPath` for those two folders only. **Not now** hides the offer. The same choice, and **Remove exclusions**, are in Settings > General.

Windows does not let a normal user read Defender's exclusions back, so AimMod remembers what it last did. An exclusion removed in Windows Security still shows as added until it is removed in AimMod too. On a PC where an organisation manages Defender, adding the exclusions can fail; AimMod says so and changes nothing.

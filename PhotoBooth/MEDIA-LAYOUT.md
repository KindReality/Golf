# Media layout handoff — ALPHA

Verified October 18, 2026. ALPHA remains the primary development machine.
Repository: https://github.com/KindReality/Golf (PhotoBooth subdirectory).

This document and MEDIA-LAYOUT.json define the approved media paths. The JSON
manifest records each file's exact byte length and SHA-256 from ALPHA's current
SourceMedia copy. There are 34 approved titles: 33 in category folders, plus
one intentional root exception. Media binaries are excluded from Git.

## Locations and responsibilities

Resolve environment variables on the computer doing the work. Do not copy
ALPHA's username or absolute user-profile paths onto another computer.

1. Canonical, OneDrive-synchronized library:
   %OneDrive%\Home Technologies\SourceMedia
   Use OneDriveConsumer, then OneDriveCommercial, if OneDrive is unavailable.
   Preserve the relative paths below. Approved new masters belong here after
   the primary developer approves their name and category.

2. Each Controller's local distribution library:
   %LOCALAPPDATA%\Home Technologies\Controller\Media
   This is the folder exposed as \\<controller-hostname>\media.
   On ALPHA the share is \\ALPHA\media.
   Copy approved media from SourceMedia into this folder, preserving categories.
   OneDrive does not synchronize this AppData folder. Do not assume another
   Controller receives these files automatically or that its share is current.

3. Each Experience's local playback cache:
   %LOCALAPPDATA%\Home Technologies\ExperienceX\Media
   Experience copies the Controller share recursively and preserves categories.
   Playback uses these local files, never the UNC path or OneDrive directly.
   VideoDirectory and AudioDirectory normally remain "Media"; they resolve
   to this local cache, independently of the executable location.

4. Legacy media copy alongside the OneDrive Experience build:
   %OneDrive%\Home Technologies\Bin\ExperienceX\Media
   Its existing files were reorganized, but it is NOT the canonical source or
   normal playback directory. It contains older versions listed below. Never
   copy it over SourceMedia or Controller's current library without comparison.

5. Application packages and machine configuration:
   %OneDrive%\Home Technologies\Bin\Controller
   %OneDrive%\Home Technologies\Bin\ExperienceX
   appsettings.defaults.json is the shipped template.
   appsettings.<Windows-hostname>.json is that computer's active configuration.
   Edit only the intended hostname file; do not replace another machine's file
   with your machine's settings. Do not use the device alias as the filename.
   Media organization changes relative media paths, not display/screen names,
   roles, device aliases, credentials, network permissions or hardware settings.

6. Shared handoff files:
   %OneDrive%\Home Technologies\DevelopmentHandoff\Media-Layout-Handoff.txt
   %OneDrive%\Home Technologies\DevelopmentHandoff\MEDIA-LAYOUT.json
   Source copies: PhotoBooth/MEDIA-LAYOUT.md and PhotoBooth/MEDIA-LAYOUT.json.

## Exact approved placement

The following tree is relative to SourceMedia, each Controller's shared Media
folder, and each Experience's playback cache. Use exactly the same spelling and
case. Category folders group subject matter, not audio versus video.

```text
MediaRoot/
  ping.mp4
  Collections/
    HalloweenWindowProjection2024.mp4
    HalloweenWindowProjection2026.mp4
  Creatures/
    EvilRoar.wav
    TRex.mp4
  Effects/
    Bell.wav
    Crash.mp4
    DoorOpens.wav
    Footsteps.wav
    Warp.mp4
  Eyes/
    Eye.mp4
  Ghosts/
    DancingGhostsDual.mp4
    FloatingGhost.mp4
    Ghost2.mp4
    Ghosts.mp4
    Slimer.mp4
    WallGhost.mp4
  Pumpkins/
    JackLantern.mp4
  Skeletons/
    DancingSkeletonSingle.mp4
    DancingSkeletonsMany.mp4
    Skeleton.mp4
  Skulls/
    SkullAttackOverFog.mp4
    SkullStrobe.mp4
  Unsorted/
    ping-compatible.mp4
    Video.mp4
  Whispers/
    CanYouSeeMe.wav
    CanYouSeeMe01.wav
    CanYouSeeMe02.wav
    CanYouSeeMe03.wav
    ISeeYou.wav
    WhisperLong.wav
  Witches/
    HoodedWitch.mp4
  Zombies/
    HallowindowZombies.mp4
    TerrifyingZombieHorde.mp4
```

ping.mp4 intentionally remains at the root: Camera triggers 3/4/5 request that
exact path. Do not move or rename it without separately updating Camera.
The legacy lowercase filenames ping.mp4 and ping-compatible.mp4 are deliberate
exceptions. Other new approved names should use PascalCase without spaces.
Unsorted contains only the TWO explicitly listed existing titles. It is not
permission to silently file unknown or newly discovered media there.

## Rules for new, unknown and conflicting files

- Treat the manifest as an allowlist. A filename not on the list, a known file
  in another folder, or an unexpected extra root file must be REPORTED.
  Do not guess its category, rename it, delete it, overwrite another file or
  add it to playlists. Preserve it in place while requesting a placement decision.
- A matching filename does not establish identical content. Compare relative
  path, length and SHA-256. A same-path file with a different hash is a content
  conflict: report both paths, lengths and hashes and retain both existing copies.
- If hashes match, reuse the existing canonical copy rather than creating
  '(1)', 'copy', machine-name or OneDrive conflict-name variants.
- Never flatten the library. Do not create Audio or Video folders. Preserve
  the category-relative path through copying, caching and configuration.
- Missing approved media is a separate finding, not authority to delete any
  local-only files. Copy only from a verified approved source after checking
  permissions and confirming the destination is the intended local library.
- Approving a new file means assigning an exact category/path and recording its
  hash and byte length in MEDIA-LAYOUT.json, then updating this description.
  Coordinate that change with ALPHA; do not have two machines independently
  modify the same defaults, manifest or hostname configuration.
- Do not use robocopy /MIR, destructive synchronization, recursive deletion,
  wholesale output-folder replacement or blanket overwrite flags to reconcile
  differences. Existing media, machine settings and share permissions survive.
- Keep media out of Git. The manifest and documentation may be versioned;
  binary assets are distributed separately through OneDrive/SMB.

Report each exception with: computer, absolute path, observed relative path,
expected relative path (if known), bytes, SHA-256 when relevant, and the action
needed. Distinguish UNLISTED, WRONG LOCATION, CONTENT CONFLICT and MISSING.
Finish the audit with counts and explicitly say whether files were changed.

## Configuration and rollout

Use category-relative paths with forward slashes in command/configuration JSON:

```json
{"experience":"video","value":"Ghosts/WallGhost.mp4","screen":"*"}
{"experience":"audio","value":"Whispers/WhisperLong.wav","audioDevice":"*"}
{"MediaFile":"Eyes/Eye.mp4"}
```

The audio wildcard above illustrates the all-output selector. It is not a
random-output selector by itself. Controller's whispers-sequence playlist uses
AudioOutputMode: Random and Roles: "^subwoofer,*" to select one discovered
non-subwoofer endpoint before sending each command. Its six files run in the listed alphabetical order using
known-duration cue delays and a total duration covering the final file. This
playlist was added to ALPHA's Idle ambient playlist and shipped defaults; other
hostname configurations remain unchanged. See Controller/README.md for details.
Role rules are evaluated left to right and the first matching positive or
negative rule decides. Do not reorder them: ^subwoofer,* excludes subwoofers,
whereas *,^subwoofer includes everyone. Unmatched outputs are not selected.

Keep a screen's identity/name unless a separate change is requested. Update its
MediaFile path when explicitly bound; do not rely on basename matching if copies
of the same filename exist in archives or old caches.

Completed on ALPHA: organized SourceMedia, Controller's local shared library,
existing Experience cache files, and the legacy OneDrive Media copy. Updated
ALPHA's Controller media references and the source/shipped default template.
Other computers' hostname configurations were NOT changed. Existing archives
were preserved. Both applications were left stopped locally.

On another computer:
1. Audit first against this tree and the hash manifest; report exceptions.
2. Finish OneDrive synchronization and confirm the handoff/manifest are present.
3. For a Controller, stage the approved category folders in its LOCAL media
   share. Do not assume OneDrive synchronizes that local folder.
4. For Experience, restart after the Controller/share is reachable so discovery
   can download the new folders. Confirm ControllerMediaSyncCompleted and local
   file presence. Normal unsolicited announcements alone do not guarantee a
   successful media sync runs again after it already completed.
5. Only after the new files are local, update that computer's applicable playlist
   value and screen MediaFile paths. Back up its configuration first, preserve
   unrelated settings and retain file contents/timestamps when moving media.
6. The cache deliberately retains local extras. Old root copies are NOT removed
   automatically; report them rather than deleting or merging them silently.
7. Do not build or run stress tests for a file-layout audit. A quick JSON/path
   check and, if necessary, one playback check are sufficient. Stop local apps
   before any build. Leave them stopped after debugging unless asked otherwise.

If shipped defaults change in source, build/publish through the normal package
process so build-manifest.sha256 is regenerated. Never edit package defaults
without updating their manifest. Install/Restart scripts must keep validating.

## Known findings to report, not silently merge

- CinematicBoom.wav is referenced in Controller's appsettings.SAFTTHING1.json,
  but is NOT in the approved 34-file library on ALPHA. Check whether it exists
  locally on SAFTTHING1; report it and request an approved path/category.
- Skull.png is referenced in Experience's appsettings.LAPTOPMSPIN.json, but is
  NOT approved active media. The still image was previously removed from active
  use. Only archived copies were found here. Do not restore it from an archive
  or replace it with SkullAttackOverFog.mp4 without a separate decision.
- The legacy OneDrive Bin/ExperienceX/Media folder contains icon.ico and icon.png.
  These are not approved playback titles. Report them as legacy technical assets
  and leave them alone; do not distribute them as newly approved media.
- Preserve existing Originals-20261010-165753 and
  Removed-Skull-Original-Warp-20261012-b229d6a0da1349dba76ccf962955af3b
  archive directories wherever they already exist. Their contents are outside
  this active allowlist. Do not promote, flatten, distribute as new media, or
  delete archived items. Any other archive directory needs to be reported.

Known legacy size conflicts (canonical SourceMedia versus Bin/ExperienceX/Media):

Relative path                  Canonical bytes   Legacy bytes
Creatures/TRex.mp4                   1,868,692      1,870,224
Effects/Crash.mp4                    2,979,178      2,980,402
Eyes/Eye.mp4                        1,160,272      1,160,832
Ghosts/Ghost2.mp4                    6,664,118      6,668,062
Ghosts/Ghosts.mp4                   18,662,814     18,692,174
Skeletons/Skeleton.mp4              14,606,747     14,615,587

These are differences, not proof of corruption. Use the canonical manifest to
identify approved content; do not overwrite it with the legacy copies. ALPHA's
SourceMedia contains all 34 approved paths with no unlisted active files.

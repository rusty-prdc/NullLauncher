# NullLauncher IPC Contract (v1)

The WPF host loads `UI/index.html` inside WebView2 (virtual host `https://app.nulllauncher.local/`).
All communication happens over `window.chrome.webview.postMessage`.

## Frontend → host

```js
// request
window.chrome.webview.postMessage({ id: 42, method: "instances.list", params: { } });
```

## Host → frontend

```js
// success
{ "id": 42, "result": ... }
// failure
{ "id": 42, "error": { "code": "friendly title", "message": "human readable text", "detail": "technical detail (optional)", "recoverable": true } }
// event (no id)
{ "event": "downloads.progress", "data": { ... } }
```

JS helper exposed by `app.js`:

```js
await api("instances.list", { });
on("downloads.progress", data => { ... });   // subscribe
```

Errors are always user friendly: never raw .NET exception names. `detail` may hold technical info
(shown only behind "Подробнее").

## Method catalogue

### system
| method | params | result |
|---|---|---|
| `system.info` | – | `{ appVersion, os, osVersion, dataDir, configDir, ramTotalMb, ramFreeMb, cpu, cpuCores, gpu, screen, freeDiskGb, isFirstRun, expertMode, locale, exePath }` |
| `system.pickDirectory` | `{ title?, initial? }` | `{ path } \| null` |
| `system.pickFiles` | `{ title?, filter? }` | `string[]` (filter e.g. `"mrpack"`, `"jar"`, `"image"`, `"zip"`) |
| `system.openPath` | `{ path, select? }` | `{ ok:true }` |
| `system.openUrl` | `{ url }` | `{ ok:true }` |
| `system.clipboard` | `{ text }` | `{ ok:true }` |
| `system.window` | `{ action: "minimize"\|"maximize"\|"close"\|"toggleMax" }` | `{ ok:true }` |
| `system.firstRun` | – | `{ needed: bool, steps: [...] }` |
| `system.completeFirstRun` | `{ folder?, java?, account?, ram?, theme? }` | `{ ok:true }` |
| `system.diagnose` | – | `{ checks: [{id,title,status:"ok"\|"warn"\|"error",message}] }` |
| `system.components` | – | `{ items: [{ key:"core"\|"ui"\|"webview2"\|"dotnet"\|"manifest"\|"java"\|"modrinth", name, version, updated?(ISO), detail, logo }] }` (реальные версии/даты: версия exe, mtime UI/index.html, реестр WebView2, Environment.Version, кэш `metadata/versions.json`, `JavaService.Detect()`, kv `modrinthLastSync`) |
| `system.storage` | – | `{ items:[{key,label,bytes,path}], totalBytes, freeBytes }` |
| `system.clearStorage` | `{ key }` | `{ freedBytes }` |
| `system.checkUpdate` | – | `{ current, latest?, url?, notes?, updateAvailable, sourceConfigured }` (`sourceConfigured=false` — источник обновлений не задан) |

### settings
| method | params | result |
|---|---|---|
| `settings.get` | – | full settings object (flat, camelCase) |
| `settings.set` | `{ key: value, ... }` | `{ ok:true }` (persisted to `config/settings.json`) |
| `settings.reset` | – | `{ ok:true }` (never touches instances) |
| `settings.export` | `{ includeInstances }` | `{ path }` → creates `NullLauncher-Backup.zip` |
| `settings.search` | `{ query }` | `[{ key, title, section, hint }]` |

### instances
| method | params | result |
|---|---|---|
| `instances.list` | – | `Instance[]` |
| `instances.get` | `{ id }` | `Instance` |
| `instances.create` | `{ name, description?, icon?, color?, mcVersion, loader, loaderVersion?, java?, ramMinMb?, ramMaxMb?, groupIds? }` | `Instance` |
| `instances.update` | `{ id, ...patch }` | `Instance` |
| `instances.delete` | `{ id, backup? }` | `{ ok:true }` |
| `instances.duplicate` | `{ id, name?, includeWorlds?, includeMods? }` | `Instance` |
| `instances.setFavorite` | `{ id, favorite }` | `{ ok:true }` |
| `instances.openFolder` | `{ id, sub? }` | `{ ok:true }` |
| `instances.verify` | `{ id }` | `{ total, ok, missing, repaired }` |
| `instances.repair` | `{ id }` | `{ downloaded, failed }` |
| `instances.changeVersion` | `{ id, mcVersion, loader, loaderVersion? }` | `{ ok:true }` (auto-backup + mod check) |
| `instances.export` | `{ id, targetPath, include: {mods,resourcepacks,shaders,configs,worlds,options} }` | `{ path, bytes }` |
| `instances.import` | `{ sourcePath }` | `Instance` (accepts `.zip` instance archives) |
| `instances.exportMrpack` | `{ id, targetPath, meta:{name,description,author,versionId} }` | `{ path }` |
| `instances.importMrpack` | `{ sourcePath, instanceName? }` | `Instance` |
| `instances.recentLaunches` | `{ limit? }` | `[{ instanceId, name, startedAt, exitCode, durationMs, error? }]` |
| `instances.installedVersions` | – | `string[]` (installed client json ids) |
| `instances.image` | `{ id, kind: "cover"\|"icon" }` | `string?` (data URL `data:image/...;base64,...`, `null` если файла нет / формат не изображение / больше 4 МБ) |

`Instance`:
```jsonc
{
  "id":"a1b2...", "name":"Fabric Survival", "description":"", "iconPath":null, "color":"#3ecf8e",
  "favorite":false, "mcVersion":"1.21.1", "loader":"fabric", "loaderVersion":"0.16.14",
  "javaMajor":21, "javaPath":null, "ramMinMb":512, "ramMaxMb":4096,
  "jvmArgs":"", "gameArgs":"", "windowMode":"windowed", "width":1920, "height":1080,
  "fullscreen":false, "vsync":true, "fpsLimit":120,
  "path":"...\\instances\\a1b2", "createdAt":"...", "lastPlayedAt":"...", "playTimeMs":0, "launchCount":0,
  "groupIds":["g1"], "modCount":12, "accountUuid":null, "serverIp":null, "serverPort":25565,
  "modpack":null, "safeModeDefault":false
}
```

### groups
`groups.list` → `[{id,name,color,icon,order,instanceIds:[]}]`,
`groups.create {name,color?,icon?}`, `groups.update {id,...}`, `groups.delete {id}`,
`groups.addInstance {groupId,instanceId}`, `groups.removeInstance {groupId,instanceId}`,
`groups.reorder {orderedIds}`

### profiles
`profiles.list` → `[{id,name,isDefault,accountUuid,theme,accent,groupId}]`,
`profiles.create {name}`, `profiles.update {id,...}`, `profiles.delete {id}`, `profiles.activate {id}`

### versions
| method | params | result |
|---|---|---|
| `versions.list` | `{ type?, query?, loader?, limit? }` | `[{id,type,releaseTime,url,installed,javaMajor}]` |
| `versions.refresh` | – | `{ count }` (re-fetches Mojang manifest) |
| `versions.loaders` | `{ mcVersion }` | `[{loader,version,stable}]` – real Fabric/Quilt/Forge/NeoForge metadata |
| `versions.detail` | `{ id }` | `{ id, type, releaseTime, javaMajor, libraries:number, assets:number }` |
| `versions.delete` | `{ id }` | `{ ok:true }` |

### modrinth
| method | params | result |
|---|---|---|
| `modrinth.search` | `{ query?, type?, loaders?, gameVersions?, categories?, facets?, limit?, offset?, sort? }` | `{ hits:[...], total, offline }` |
| `modrinth.project` | `{ id }` | project detail (description html, gallery, license, links, body) |
| `modrinth.versions` | `{ id, loaders?, gameVersions? }` | `[{id,name,versionNumber,datePublished,gameVersions,loaders,files:[{filename,url,primary,size,hashes}],downloads,channels}]` |
| `modrinth.tags` | – | `{ categories, loaders, gameVersions, projectTypes, licenses }` |
| `modrinth.install` | `{ instanceId, versionId, kind }` | `{ installed:[{filename,enabled}], warnings:string[] }` |
| `modrinth.checkUpdates` | `{ instanceId }` | `[{ modId, filename, current, latest, latestVersionId, projectId, projectName }]` |
| `modrinth.projectByFile` | `{ hashes:[..] }` | matches existing files against Modrinth (for import) |

`modrinth.search` hits: `{projectId, slug, title, description, author, iconUrl, downloads, follows, categories, loaders, versions, projectType, dateModified}`

### content (mods / resourcepacks / shaders / datapacks)
| method | params | result |
|---|---|---|
| `content.list` | `{ instanceId, kind }` | `[{id,filename,name,version,author,sizeBytes,enabled,updatable,latestVersion,mcVersions,loaders,sourceUrl,source:"modrinth"\|"local",lastModified,projectId}]` (`kind`: `mods`\|`resourcepacks`\|`shaderpacks`\|`datapacks`) |
| `content.toggle` | `{ instanceId, kind, id, enabled }` | `{ ok:true }` (file renamed `.jar` ↔ `.jar.disabled`) |
| `content.remove` | `{ instanceId, kind, id }` | `{ ok:true }` |
| `content.update` | `{ instanceId, kind, id, versionId? }` | `{ ok:true }` |
| `content.updateAll` | `{ instanceId, kind }` | `{ updated, failed }` |
| `content.import` | `{ instanceId, kind, paths:[...] }` | `{ imported, skipped }` |
| `content.checkIntegrity` | `{ instanceId, kind }` | `{ total, issues:[...] }` |
| `content.openFolder` | `{ instanceId, kind }` | `{ ok:true }` |
| `content.presets` | `{ instanceId }` | `[{id,name,createdAt}]` |
| `content.savePreset` | `{ instanceId, name }` | preset |
| `content.applyPreset` | `{ instanceId, presetId }` | `{ ok:true }` |
| `content.deletePreset` | `{ instanceId, presetId }` | `{ ok:true }` |

### worlds
`worlds.list {instanceId}` → `[{name,dir,sizeBytes,lastModified,version,iconDataUrl,hasLevel}]`
`worlds.play {instanceId,world?}`, `worlds.rename {instanceId,world,newName}`,
`worlds.delete {instanceId,world,backup?}`, `worlds.backup {instanceId,world}` → backup,
`worlds.importZip {instanceId,zipPath}`, `worlds.exportZip {instanceId,world,targetPath}`,
`worlds.openFolder {instanceId,world?}`

### servers
`servers.list {instanceId}` → `[{id,name,ip,port,icon?}]`,
`servers.add {instanceId,name,ip,port?}`, `servers.update {...}`, `servers.remove {instanceId,serverId}`,
`servers.connect {instanceId,serverId}` → launches instance with `--server ip --port port`

### backups
`backups.list {instanceId?}` → `[{id,name,path,createdAt,sizeBytes,kind}]`
`backups.create {instanceId,kind:"full"|"worlds"|"mods"|"settings",name?}`
`backups.restore {instanceId,backupId,what?}`, `backups.delete {backupId}`, `backups.openFolder {instanceId?}`

### java
`java.list` → `[{id,path,version,major,arch,vendor,valid,source}]`
`java.detect` → same + triggers rescan
`java.install {major}` → `{ ok, path }` (downloads Temurin to `java/`)
`java.remove {id}`
`java.recommend {instanceId}` → `{ major, path, reason, candidates:[...] }`
`java.validate {path}` → `{ valid, version, major, arch }`
`java.setInstance {instanceId, mode:"auto"|"major"|"path", value?}`

### accounts
`accounts.list` → `[{uuid,name,type,expiresAt,default,skinUrl}]`
`accounts.addMicrosoft` → `{ deviceCode, userCode, verificationUrl, expiresInSeconds }`
`accounts.pollMicrosoft { uuid }` → `{ status:"pending"\|"done"\|"expired", account? }`
`accounts.remove {uuid}`, `accounts.setDefault {uuid}`

### downloads
`downloads.list` → `[{id,kind,title,url,target,totalBytes,receivedBytes,progress,state,speedBps,etaS,error,retryCount}]`
`downloads.cancel {id}`, `downloads.retry {id}`, `downloads.clearFinished`, `downloads.pause {id}`/`downloads.resume {id}`

### launch
`launch.start {instanceId, safeMode?, onlyMods?: string[], server?: {ip,port}}` → `{ ok:true }`
`launch.stop {instanceId}` → `{ ok:true }`
`launch.compatCheck {instanceId}` → `{ ok, issues:[{severity,title,message,fixable,fixed?}] , fixedCount }` (deps, conflicts, missing libs, java, ram)
`launch.lastStatus {instanceId}` → `{ state, exitCode, startedAt, endedAt, error }`

Events: `launch.progress {instanceId, stage, step, total, message, percent}`, `launch.state {instanceId,state,exitCode,error}`, `launch.log {instanceId, line, level}`

### logs / crash
`logs.listLauncher` → `[{name,sizeBytes,createdAt}]`
`logs.readLauncher {name, query?, level?}` → `{ lines:[{ts,level,message}] }`
`logs.instanceFiles {instanceId}` → `[{name,path,sizeBytes,createdAt}]`
`logs.readInstance {instanceId, path, query?, level?, offset? }` → `{ lines:[...], eof }`
`crashes.list {instanceId?}` → `[{id,path,createdAt,sizeBytes,analyzed?}]`
`crashes.read {id}` → `{ text }`
`crashes.analyze {id}` → `{ cause, summary, suggestions:[...] }`

### storage / cache
`storage.overview` → `[{key,label,bytes,items}]`
`storage.analyze` → detailed breakdown per instance + categories
`cache.stats` → `[{key,label,bytes}]`
`cache.clear {keys:[...]}` → `{ freed }`

### launcher
`launcher.notifications.push {type,title,message}` (used by host only)
`launcher.sidebar` / `launcher.ui` → not needed (settings.set handles)
`search.global {query}` → `{ instances:[], mods:[], settings:[], worlds:[], versions:[], modrinth:[] }`
`hotkeys.get` / `hotkeys.set {map}` (also via settings)

### events (host → frontend)
- `notify {type:"success"|"error"|"warn"|"info", title, message, actions?:[{id,label}]}`
- `downloads.progress {…}` (batched list snapshot)
- `launch.progress`, `launch.state`, `launch.log`
- `instance.changed {id}` – content modified externally
- `settings.changed {…}`
- `app.updateAvailable {…}`
- `modrinth.status {offline:boolean}`

## Notes for the frontend
- Every method listed here must be used if shown in UI; no decorative buttons.
- Loading, empty and error states are mandatory.
- Never display raw exception text; show `error.message` with "Подробнее" revealing `error.detail`.
